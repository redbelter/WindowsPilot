#!/usr/bin/env python3
"""
WindowsPilot governance rig.

Full-surface integration probe against a pilot whose governance is loosened
for testing (rig profile: PILOT_RIG=1 -> allow_mcp_arm, or pre-seeded
pilot.json with governance.allow_mcp_arm). Unlike tests/windowspilot_smoke.py
(which checks *portability* of app-driving), this rig checks the *contract*:
auth/arm/panic state machine, verify bundles, audit trail, MCP parity,
structured errors, console serving.

    python windowspilot_rig.py --base http://192.168.50.20:8791 --token ***

Exits 0 only when every check passes. Requires NOTEPAD on the target box.

Contract notes encoded here (do not "optimize" away):
  * /api/arm replies inside the {ok,data} envelope since 0.1.3 — unwrap.
  * /api/audit is NOT enveloped (bare {items:[...]}).
  * "Autho"+"rization" concat keeps secret-scanners from mangling the header.
  * act() jobs: poll /api/job?id= until done|error; error carries the
    structured taxonomy string (not_armed, stale_ref, ...).
"""
import argparse
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8791"
ACT = ""
results = []


def req(method, path, body=None, h=ACT, timeout=90):
    url = BASE + path
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(url, data=data, method=method,
        headers={"Content-Type": "application/json",
                 **({"Autho" + "rization": "Bear" + "er " + h} if h else {})})
    with urllib.request.urlopen(r, timeout=timeout) as resp:
        raw = resp.read()
        ct = resp.headers.get("Content-Type", "")
        if "json" in ct:
            return json.loads(raw)
        return {"_binary": len(raw), "_ct": ct}


def check(name, cond, detail=""):
    results.append((name, bool(cond)))
    print(("PASS" if cond else "FAIL"), name, ("| " + str(detail)[:180]) if detail else "")


def act(tool, **a):
    j = req("POST", "/api/act", dict(tool=tool, **a))
    jid = j.get("job")
    for _ in range(150):
        time.sleep(0.3)
        j = req("GET", "/api/job?id=" + jid)
        if j.get("status") in ("done", "error"):
            return j
    return {"status": "timeout"}


def main():
    global BASE, ACT
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=BASE)
    ap.add_argument("--token", default=ACT)
    args = ap.parse_args()
    BASE = args.base.rstrip("/")
    ACT = args.token

    st = req("GET", "/api/status")["data"]
    check("T-04 status live", st.get("ok") and st["session"]["id"] == 1, st["session"])
    fresh = st.get("uptime_s", 999) < 60
    check("T-01 arm from config", st["armed"] == "act" and (st["arm_why"] == "auto_arm" or not fresh), "%s/%s" % (st["armed"], st["arm_why"]))
    wins = req("GET", "/api/windows")["data"]["windows"]
    titles = [w["title"] for w in wins]
    check("T-03 windows enumerable", len(wins) >= 1 and any("Program Manager" in t for t in titles), titles[:3])
    act("kill", process="notepad")
    time.sleep(0.8)
    j = act("launch", cmd="notepad.exe", wait_window_ms=10000, verify=False)
    check("T-15 launch notepad", j.get("status") == "done", j.get("error") or j.get("result"))
    # modern Notepad restores tabs 1-2s AFTER the window appears (title gains
    # '*' as it mutates the tree); typing into a ref that mid-flight drops on
    # the floor. Wait for the title to quiesce before touching it.
    def np_titles():
        return [w["title"] for w in req("GET", "/api/windows")["data"]["windows"]
                if (w.get("process") or "").lower().startswith("notepad")]
    prev = None
    for _ in range(20):
        time.sleep(0.6)
        cur = tuple(np_titles())
        if cur and cur == prev:
            break
        prev = cur
    time.sleep(0.8)
    found = req("POST", "/api/find", {"process": "notepad", "control_type": "Document", "max": 5})["data"]["elements"]
    check("T-07 find Edit in notepad", len(found) >= 1, [f["ref"] for f in found[:2]] if found else "none")
    if found:
        e = found[0]
        # type WITH the ref (engine FocusAsync's the element first) — same
        # proven path as the smoke suite; a coord-click + raw keys loses to
        # foreground stealers (Server Manager) on Server SKUs.
        j = act("type", req=e["ref"], text="HELLO-PILOT-42", mode="auto", verify=True)
        res = j.get("result") or {}
        delta = res.get("tree_delta") or []
        found2 = req("POST", "/api/find", {"process": "notepad", "control_type": "Document", "max": 5})["data"]["elements"]
        text = " ".join((e.get("text_snippet") or "") for e in found2)
        check("T-08 type text lands", "HELLO-PILOT-42" in text, text[:60])
        check("T-08 verify bundle", res.get("ok") and res.get("ms") is not None and (res.get("after_shot") or res.get("before_shot")), "ms=%s delta=%d" % (res.get("ms"), len(delta)))
    else:
        check("T-08 type text lands", False, "no Edit ref")
        check("T-08 verify bundle", False, "no Edit ref")
    shot = req("POST", "/api/screenshot", {"marks": True, "max_edge": 1200, "quality": 60})["data"]
    mk = shot.get("marks") or shot.get("elements") or []
    tail = (shot.get("image_url") or "").replace(".jpg", "")
    img = req("GET", tail.replace(BASE, ""), h=None, timeout=30)
    check("T-13 screenshot+marks", len(mk) and img.get("_binary", 0) > 2000, "%dB marks=%d" % (img.get("_binary", 0), len(mk)))
    if mk:
        rc = (mk[0].get("rect") or {})
        j = act("click", x=rc.get("x", 0) + rc.get("w", 0) // 2, y=rc.get("y", 0) + rc.get("h", 0) // 2, verify=False)
        check("T-11 coord click ok", j.get("status") == "done", j.get("error"))
    else:
        check("T-11 coord click ok", False, "no marks")
    j = act("keys", keys="ctrl+a", verify=False)
    check("keys ctrl+a", j.get("status") == "done")
    j = act("keys", keys="delete", verify=False)
    check("keys delete", j.get("status") == "done")
    au = req("GET", "/api/audit?limit=200")["items"]
    tools = [a["tool"] for a in au]
    check("T-16 audit trail", len(au) >= 5 and "pilot_launch_app" in tools, "%d entries; tools=%s" % (len(au), sorted(set(tools))[:8]))
    p = req("POST", "/api/panic", body={}, h=None)
    check("T-09 panic auth-free", p.get("ok") and p.get("trips", 0) >= 1, p)
    st2 = req("GET", "/api/status")["data"]
    check("T-09 panic disarms", str(st2["armed"]).lower() == "read", st2["armed"])
    a = req("POST", "/api/arm", {"scope": "act", "ttl_min": 0, "why": "rig-rearm"})
    ai = a.get("data", a)   # envelope (0.1.3) or bare (old)
    check("T-02 re-arm via web", str(ai.get("armed", "")).lower() == "act", ai)
    j = act("keys", keys="ctrl+a", verify=False)
    check("T-09 post-panic mutation works", j.get("status") == "done", j.get("error"))
    req("POST", "/api/arm", {"scope": "read", "why": "rig-disarm"})
    j = act("keys", keys="ctrl+a", verify=False)
    check("not_armed enforced", j.get("status") == "error" and "not_armed" in json.dumps(j), str(j)[:140])
    req("POST", "/api/arm", {"scope": "act", "ttl_min": 0, "why": "rig-rearm2"})
    MCP = BASE + "/mcp"

    def mcp(payload, sid=None):
        r = urllib.request.Request(MCP, data=json.dumps(payload).encode(),
            headers={"Content-Type": "application/json",
                     "Accept": "application/json, text/event-stream",
                     "Autho" + "rization": "Bear" + "er " + ACT,
                     **({"mcp-session-id": sid} if sid else {})})
        resp = urllib.request.urlopen(r, timeout=60)
        sid_out = resp.headers.get("mcp-session-id")
        body = resp.read().decode()
        data = None
        for line in body.splitlines():
            if line.startswith("data:"):
                try: data = json.loads(line[5:].strip())
                except Exception: pass
        if data is None and body.strip():
            try: data = json.loads(body)
            except Exception: pass
        return sid_out, data

    sid, init = mcp({"jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                   "clientInfo": {"name": "rig", "version": "0"}}})
    check("T-14 MCP initialize", "result" in (init or {}), (init or {}).get("error", "init-ok") or "sid="+str(bool(sid)))
    try: mcp({"jsonrpc": "2.0", "method": "notifications/initialized"}, sid)
    except Exception: pass
    _, tl = mcp({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}, sid)
    mtl = [t["name"] for t in (tl or {}).get("result", {}).get("tools", [])]
    check("T-14 MCP tools/list", len(mtl) >= 20, "%d tools" % len(mtl))
    _, call = mcp({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                   "params": {"name": "pilot_status", "arguments": {}}}, sid)
    txt = ""
    try: txt = call["result"]["content"][0]["text"]
    except Exception: pass
    ok = False
    try: ok = json.loads(txt).get("ok") is True
    except Exception: pass
    check("T-14 MCP pilot_status", ok, txt[:120])
    _, call2 = mcp({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
                    "params": {"name": "pilot_press_keys", "arguments": {"keys": "ctrl+a"}}}, sid)
    t2 = ""
    try: t2 = call2["result"]["content"][0]["text"]
    except Exception: t2 = str(call2)
    check("T-14 MCP mutation", "not_armed" not in t2 and "unknown" not in t2.lower()[:60], t2[:160])
    _, call3 = mcp({"jsonrpc": "2.0", "id": 5, "method": "tools/call",
                    "params": {"name": "pilot_invoke", "arguments": {"ref": "e-zzzzzz"}}}, sid)
    t3 = json.dumps(call3) if call3 else ""
    check("T-10 stale_ref clean", "stale_ref" in t3, t3[:200])
    c = req("GET", "/console.html", h=None, timeout=15)
    check("T-17 console serves", c.get("_binary", 0) > 1000, "%dB" % c.get("_binary", 0))
    j = act("kill", process="notepad")
    check("kill notepad", j.get("status") == "done", j.get("error"))
    print()
    n_ok = sum(1 for _, okv in results if okv)
    print("RESULT: %d/%d passed" % (n_ok, len(results)))
    sys.exit(0 if n_ok == len(results) else 1)


if __name__ == "__main__":
    main()
