#!/usr/bin/env python3
"""
WindowsPilot portable smoke tests.

Runs against ANY Windows machine (or VM) that has WindowsPilot serving its API:

    python windowspilot_smoke.py --base http://192.168.50.20:8791 --token ***
    python windowspilot_smoke.py --base http://localhost:8791 --only notepad

Stdlib only (urllib). Every test is self-contained: it launches the app it
needs, drives it through the governed act API, verifies by reading UI
properties back, and cleans up. Nothing here is tied to Hyper-V, a specific
VM, or a specific Windows version.

Portability rules used throughout (do not "optimize" them away):
  * Prefer AutomationId over Name for locating elements — AutomationId is
    language-independent (localized Windows breaks name-matching).
  * Fall back to Name / ClassName only when ids are absent (classic apps
    like the Win32 calculator have no ids, only names).
  * Never assume modern-vs-classic: Notepad's editor is 'Document' on
    modern builds and 'Edit' on classic ones; Calculator is a XAML app on
    Win10/11 and the Win32 calc.exe on Server SKUs.
  * Read numeric results out of any text with a regex — the calculator's
    display Name is localized ("Display is 56" / "Anzeige: 56"), but the
    digits survive localization.
"""
import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "http://127.0.0.1:8791"
TOKEN = ""
TIMEOUT = 20.0

# ---------------------------------------------------------------- plumbing

def api(path, body=None, timeout=None):
    """One REST call to the pilot. body != None -> POST json."""
    url = BASE + path
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}
    if TOKEN:
        headers["Authorization"] = f"Bearer {TOKEN}"
    rq = urllib.request.Request(url, data=data, method="POST" if body is not None else "GET",
                                headers=headers)
    with urllib.request.urlopen(rq, timeout=timeout or TIMEOUT) as r:
        return json.loads(r.read())


def act(tool, timeout_s=90, **args):
    """Run one act verb through the /api/act job queue; return its result dict."""
    j = api("/api/act", dict(tool=tool, **args))
    if "job" not in j:
        raise RuntimeError(f"{tool} rejected: {json.dumps(j)[:200]}")
    jid = j["job"]
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        time.sleep(0.3)
        j = api("/api/job?id=" + urllib.parse.quote(jid))
        if j.get("status") == "done":
            return j.get("result") or {}
        if j.get("status") == "error":
            raise RuntimeError(f"{tool} failed: {json.dumps(j.get('error'))[:250]}")
    raise TimeoutError(f"{tool} job did not finish in {timeout_s}s")


def find(proc=None, hwnd=None, control_type=None, auto_id=None, name_regex=None,
         name_contains=None, class_name=None, max=2000):
    """REST find (POST /api/find) with the common filters; returns the element list.
    NOTE: the server filters by process NAME, not pid — resolve pid via windows()
    only for bookkeeping, pass the process name here."""
    body = {"max": max}
    if proc: body["process"] = proc
    if hwnd: body["hwnd"] = hwnd
    if control_type: body["control_type"] = control_type
    if auto_id: body["automation_id"] = auto_id
    if name_regex: body["name_regex"] = name_regex
    if name_contains: body["name_contains"] = name_contains
    if class_name: body["class_name"] = class_name
    r = api("/api/find", body, timeout=60)
    return r.get("data", {}).get("elements", [])


def windows():
    return api("/api/windows").get("data", {}).get("windows", [])


def wait(cond, timeout=15.0, poll=0.4):
    """Poll `cond()` until truthy or timeout; returns the value or None."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        v = cond()
        if v:
            return v
        time.sleep(poll)
    return None


def find_first(elems, pred):
    for e in elems:
        if pred(e):
            return e
    return None


def win_by(procs=(), title_sub=""):
    """First visible window whose process name is in `procs` (case-insensitive)
    or whose title contains title_sub. Returns the WindowDto or None."""
    for w in windows():
        t = (w.get("title") or "").lower()
        p = (w.get("process") or "").lower()
        if not w.get("title"):           # ignore invisible helper windows
            continue
        if (procs and p in [x.lower() for x in procs]) or (title_sub and title_sub.lower() in t):
            return w
    return None


def kill_processes(*names):
    """Best-effort cleanup; kill is whitelisted so this never needs arm."""
    for n in names:
        try:
            act("kill", process=n)
        except Exception:
            pass          # not running / not whitelisted — fine for cleanup
    time.sleep(0.6)


def launch(cmd, args=None, procs=(), title_sub="", wait_s=25):
    """Launch via the act seam. A launch verb whose synchronous window-verify
    times out still *did* launch the app (the process exists; the window just
    came late — typical on a slow first launch of modern apps). The window is
    the source of truth, so only a launch that never produced a process/window
    counts as a hard failure (e.g. exe missing -> not_found from the seam)."""
    def seen():
        return win_by(procs=procs, title_sub=title_sub) is not None if (procs or title_sub) else True
    try:
        act("launch", cmd=cmd, args=args, wait_window_ms=4000)
        return True
    except RuntimeError as e:
        if "not_found" in str(e) or "launch failed" in str(e):
            return False
        return bool(wait(seen, timeout=8))
    except TimeoutError:
        return bool(wait(seen, timeout=8))


# ---------------------------------------------------------------- tests

def test_health():
    """Server answers, governance reachable, act scope available (arms if needed)."""
    s = api("/api/status").get("data", {})
    assert s.get("ok"), f"status not ok: {s}"
    sess = s.get("session") or {}
    assert sess.get("interactive"), f"pilot is not in an interactive session (session={sess.get('id')})"
    armed = (s.get("armed") or "").lower()
    if armed != "act":
        a = api("/api/arm", {"scope": "act", "ttl_min": 10, "why": "smoke-suite"})
        ai = a.get("data", a)
        assert str(ai.get("armed", "")).lower() == "act", f"could not arm act: {json.dumps(a)[:200]}"
    w = api("/api/windows").get("data", {})
    assert "windows" in w, "windows feed missing"
    return f"version={s.get('version')} windows={len(w.get('windows', []))}"


def test_notepad():
    """Launch Notepad, type a unique marker into the editor, read it back via UIA, clean up.

    Works on classic Win32 notepad (editor = Edit control) and modern Notepad
    (editor = Document named 'Text editor')."""
    marker = f"PILOT-{int(time.time()) % 100000}"
    kill_processes("notepad")
    try:
        launch("notepad.exe", procs=("notepad",), title_sub="notepad")
        w = wait(lambda: win_by(procs=("notepad",), title_sub="notepad"), timeout=15)
        assert w, "notepad window never appeared"

        # Modern Notepad restores the previous session (tabs/content) on launch,
        # which can swap the tree under us a beat after it opens. Wait for the
        # window title to go quiet before touching anything.
        last = None
        stable_until = time.time() + 10
        while time.time() < stable_until:
            cur = (win_by(procs=("notepad",), title_sub="notepad") or {}).get("title")
            if cur is not None and cur == last:
                break
            last, _ = cur, time.sleep(0.8)
        w = win_by(procs=("notepad",), title_sub="notepad") or w

        # locate the editor: control type first, class/name fallbacks after.
        def editor():
            return find_first(
                find(hwnd=w["hwnd"], max=800) or find(proc="notepad", max=800),
                lambda e: (e.get("control_type") in ("Document", "Edit", "Text"))
                or (e.get("class_name") in ("Edit", "RICHEDIT50W", "RichEditD2DPT", "TextBox"))
                or (e.get("name") or "").lower() in ("text editor", "editor"))

        ed = wait(editor, timeout=12)
        assert ed, "no text editor element found in notepad"
        time.sleep(1.0)                      # restore may still be swapping refs
        ed = editor() or ed                  # re-locate on the settled tree

        act("type", req=ed["ref"], text=marker, mode="auto")

        # read back by scanning the window for the marker (UIA text, not a
        # screenshot). Ref identity is deliberately NOT required: tab restore
        # can re-create the element between the type and the read.
        def seen():
            for e in (find(hwnd=w["hwnd"], max=800) or find(proc="notepad", max=800)):
                blob = " ".join(filter(None, [e.get("text_snippet"), e.get("name")]))
                if marker in blob:
                    return True
            return False
        assert wait(seen, timeout=8), f"typed {marker!r} but could not read it back"

        # and kill must close the window (proves window lifecycle feed)
        act("kill", process="notepad")
        gone = wait(lambda: win_by(procs=("notepad",)) is None, timeout=10)
        assert gone, "notepad window survived kill"
        return f"editor={ed.get('control_type')} marker={marker} round-tripped"
    finally:
        kill_processes("notepad")


def _calc_button(elems, auto_ids, name_res):
    """Find a calculator button: AutomationId first (modern XAML calc, language
    independent), then Name regex (classic Win32 calc / English names)."""
    for aid in auto_ids:
        e = find_first(elems, lambda x, a=aid: (x.get("automation_id") or "") == a
                       and x.get("enabled", True))
        if e:
            return e, f"auto_id={aid}"
    for rx in name_res:
        e = find_first(elems, lambda x, r=rx: (x.get("control_type") == "Button")
                       and r.fullmatch(x.get("name") or "") and x.get("enabled", True))
        if e:
            return e, f"name~{rx.pattern}"
    return None, None


def test_calculator():
    """Launch Calculator, compute 7 x 8 = 56 by invoking UIA buttons, verify
    the displayed result by reading its UIA text back.

    Handles modern XAML Calculator (Win10/11, AutomationId-keyed) and the
    classic Win32 calc.exe (Server SKUs, name-keyed; process 'win32calc').
    On the classic app the UIA tree walk is MSAA-bridged and can hit the
    server's time budget mid-tree — each find returns a *partial* set and
    later rounds get deeper as the ref cache warms, so the test accumulates
    elements across rounds until the needed buttons appear.
    Numeric extraction from the display is locale-proof (regex over digits)."""
    kill_processes("CalculatorApp", "calc", "win32calc", "Calculator")
    procs = ("calc", "calculatorapp", "win32calc")   # classic Win32 calc.exe reports as 'win32calc' on Server SKUs
    try:
        launched = launch("calc.exe", procs=procs, title_sub="calculator")
        w = wait(lambda: win_by(procs=procs, title_sub="calculator"), timeout=12)
        if not launched or not w:
            # modern-only installs: shell-resolve the calculator: protocol
            launch("calculator:", procs=procs, title_sub="calculator")
            w = wait(lambda: win_by(procs=procs, title_sub="calculator"), timeout=20)
        assert w, "calculator window never appeared"

        # Find by HWND, not process: on Win11 the XAML app's window is hosted
        # by ApplicationFrameHost, so the window's process field never matches
        # the app's process name. The hwnd always owns the real UIA subtree.
        pool = {}
        def grow():
            for e in find(hwnd=w["hwnd"], max=500):
                pool[e["ref"]] = e
            return pool
        grow()
        wait(lambda: find_first(pool.values(), lambda e: e.get("control_type") == "Button") or grow(),
             timeout=20)
        assert find_first(pool.values(), lambda e: e.get("control_type") == "Button"), \
            "calculator has no buttons in its UIA tree"

        def press(auto_ids, name_res, label, expect=None):
            """Poll the growing tree until the button appears, invoke it, and —
            when `expect` is given — confirm the display actually changed.
            Win11 Calculator accepts Invoke-pattern clicks while it is still
            warming up and silently drops them, so a click that doesn't move
            the display is retried (on a re-grown pool; refs may be stale)."""
            how = None
            for _ in range(3):
                def locate():
                    grow()
                    e, how = _calc_button(list(pool.values()), auto_ids, name_res)
                    return (e, how) if e else None
                hit = wait(locate, timeout=25)
                assert hit, f"could not find button {label!r} (no AutomationId match, no name match)"
                e, how = hit
                act("click", req=e["ref"])      # pattern-first invoke via the act seam
                if expect is None:
                    time.sleep(0.35)
                    return how
                grow()
                if wait(lambda: result() == expect, timeout=4):
                    return how
            return how                          # last chance judged by the final assert

        def result():
            """Displayed number, whatever dialect the display speaks."""
            grow()
            es = list(pool.values())
            e = find_first(es, lambda x: (x.get("automation_id") or "") == "CalculatorResults")
            if e is None:                       # classic calc: 'Result' Static carries the value in its text
                e = find_first(es, lambda x: (x.get("name") or "").lower() == "result")
            if e is None:                       # any Edit whose text is numeric
                e = find_first(es, lambda x: x.get("control_type") == "Edit"
                               and re.search(r"\d", (x.get("text_snippet") or "")))
            if e is None:                       # any Text/Static whose *name* is just a number
                e = find_first(es, lambda x: x.get("control_type") in ("Text", "Edit")
                               and re.fullmatch(r"-?[\d.,\s]+", (x.get("name") or "").strip() or "x"))
            if e is None:
                return None
            text = " ".join(filter(None, [e.get("name"), e.get("text_snippet")]))
            nums = re.findall(r"-?\d+(?:[.,]\d+)?", text)
            if not nums:
                return None
            return nums[-1].replace(",", ".")   # last number = the live result


        hows = {}
        hows["7"] = press(["num7Button", "num7"], [re.compile(r"7")], "7", expect="7")
        hows["x"] = press(["multiplyButton", "timesButton", "mulButton"],
                          [re.compile(r"[*×]"), re.compile(r"multipl\w*", re.I), re.compile(r"times", re.I)],
                          "*", expect="7")      # operand stays on screen after ×
        hows["8"] = press(["num8Button", "num8"], [re.compile(r"8")], "8", expect="8")
        hows["="] = press(["equalButton", "equalsButton"],
                          [re.compile(r"[=]"), re.compile(r"equ\w*", re.I)], "=", expect="56")

        val = wait(lambda: result() in ("56", "56.0"), timeout=12)
        final = result()
        assert final == "56", f"expected 56 after 7*8=, display reads {final!r} (button locators: {hows})"
        return f"7x8=56 verified (locator style: {hows['7']}, {hows['x']})"
    finally:
        kill_processes("CalculatorApp", "calc", "win32calc", "Calculator")


# ---------------------------------------------------------------- driver

TESTS = [
    ("health", test_health),
    ("notepad", test_notepad),
    ("calculator", test_calculator),
]


def main():
    global BASE, TOKEN
    ap = argparse.ArgumentParser(description="WindowsPilot portable smoke tests")
    ap.add_argument("--base", default=None, help="pilot base URL (env PILOT_BASE), default http://127.0.0.1:8791")
    ap.add_argument("--token", default=None, help="bearer token (env PILOT_TOKEN); omit for rig-profile pilots")
    ap.add_argument("--only", default="", help="comma list of test names to run (default: all)")
    ap.add_argument("--list", action="store_true", help="list tests and exit")
    args = ap.parse_args()

    if args.list:
        for n, fn in TESTS:
            print(f"{n:12s} {fn.__doc__.strip().splitlines()[0]}")
        return 0

    BASE = (args.base or __import__("os").environ.get("PILOT_BASE") or BASE).rstrip("/")
    TOKEN = args.token or __import__("os").environ.get("PILOT_TOKEN") or ""
    wanted = {t.strip() for t in args.only.split(",") if t.strip()} or None

    print(f"WindowsPilot smoke suite -> {BASE}")
    try:
        api("/api/status", timeout=8)
    except Exception as e:
        print(f"FAIL server unreachable at {BASE}: {e}")
        return 1

    passed = failed = 0
    for name, fn in TESTS:
        if wanted and name not in wanted:
            continue
        t0 = time.time()
        try:
            detail = fn()
            passed += 1
            print(f"PASS {name:12s} {detail} [{time.time()-t0:.1f}s]")
        except Exception as e:
            failed += 1
            print(f"FAIL {name:12s} {e} [{time.time()-t0:.1f}s]")

    print(f"\nRESULT: {passed} passed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
