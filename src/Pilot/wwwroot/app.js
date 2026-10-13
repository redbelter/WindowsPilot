// WindowsPilot console — zero build, no deps. Same-origin REST + /ws/view
// frames + /api/audit tail. Token handling: ?token= (WS can't set headers).
'use strict';

const $ = s => document.querySelector(s);
const tokEl = $('#tok');
let TOKEN = localStorage.getItem('pilot_tok') || '';
tokEl.value = TOKEN;
tokEl.onchange = () => { TOKEN = tokEl.value.trim(); localStorage.setItem('pilot_tok', TOKEN); status(); };

// ---------- tiny api helper (hyperv dashboard pattern: {ok,data} | Err) ----
async function api(path, body) {
  const opt = body ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {};
  const r = await fetch('/api' + path + (path.includes('?') ? '&' : '?') + 'token=' + encodeURIComponent(TOKEN), opt);
  const j = await r.json();
  if (j.ok === false) throw Object.assign(new Error(j.error + ': ' + (j.msg || '')), j);
  return j.data !== undefined ? j.data : j;
}
async function job(tool, args) {
  const { job: id } = await api('/act', Object.assign({ tool }, args || {}));
  for (let i = 0; i < 200; i++) {
    await new Promise(r2 => setTimeout(r2, 100));
    const j = await api('/job?id=' + id);
    if (j.status === 'done') return j.result;
    if (j.status === 'error') throw new Error(j.error);
  }
  throw new Error('job timeout');
}

// ---------- status + log strip ---------------------------------------------
let hdr = { scale: 1, origin: { x: 0, y: 0, w: 1, h: 1 }, tier: '?' };
let armed = 'read';
async function status() {
  try {
    const s = await api('/status');
    armed = s.armed;
    $('#hostPill').textContent = `${s.session.interactive ? 'session ' + s.session.id : 'NO-SESSION!'}`;
    $('#armPill').textContent = armed + (s.arm_expires ? ' ⏱' : '');
    $('#armPill').className = 'pill' + (armed === 'act' ? ' act' : armed === 'drive' ? ' drive' : '');
    $('#tierPill').textContent = 'tier:' + s.capture_tier;
    $('#stateDot').style.background = s.session.interactive ? 'var(--ok)' : 'var(--acc)';
    document.title = `[${armed}] WindowsPilot`;
  } catch (e) {
    $('#hostPill').textContent = e.message.slice(0, 30);
  }
}
setInterval(status, 3000); status();

let logN = 0;
async function pollLog() {
  try {
    const { log, n } = await api('/log?since=' + logN);
    logN = n;
    if (log.length) {
      const el = $('#log');
      for (const e of log) {
        const div = document.createElement('div');
        div.textContent = `[${e.t}] ${e.m}`;
        div.style.color = e.k === 'panic' || e.k === 'job_error' ? 'var(--acc)' : '';
        el.appendChild(div);
      }
      while (el.children.length > 120) el.removeChild(el.firstChild);
      el.scrollTop = el.scrollHeight;
    }
  } catch { }
}
setInterval(pollLog, 3000);

// ---------- tabs ------------------------------------------------------------
for (const b of document.querySelectorAll('#tabs button'))
  b.onclick = () => {
    for (const x of document.querySelectorAll('#tabs button')) x.classList.remove('on');
    b.classList.add('on');
    for (const p of document.querySelectorAll('main section')) p.hidden = true;
    $('#pane' + b.dataset.tab[0].toUpperCase() + b.dataset.tab.slice(1)).hidden = false;
    if (b.dataset.tab === 'audit') auditRefresh();
    if (b.dataset.tab === 'operator') opsRefresh();
    if (b.dataset.tab === 'target') targetRefresh();
    if (b.dataset.tab === 'inspect' && !treeLoaded) rescan();
  };

// ---------- arm / stop / drive ----------------------------------------------
$('#stop').onclick = async () => { try { await api('/panic', {}); } catch (e) { logLine('panic failed ' + e.message); } };
async function logLine(m) { const el = $('#log'); const d = document.createElement('div'); d.textContent = m; el.appendChild(d); }

$('#armBtn').onclick = async () => {
  try {
    const scope = armed === 'act' ? 'read' : 'act';   // toggle
    const info = await api('/arm', { scope, ttl_min: 15, why: 'console' });
    armed = info.armed; status();
  } catch (e) { alert(e.message); }
};
$('#driveBtn').onclick = () => { $('#ckDrive').click(); };

// ---------- /ws/view frames -------------------------------------------------
let ws = null, frames = 0, driveOn = false;
function connectView() {
  ws = new WebSocket(`ws://${location.host}/ws/view?token=${encodeURIComponent(TOKEN)}`);
  ws.binaryType = 'arraybuffer';
  ws.onmessage = ev => {
    if (typeof ev.data === 'string') {
      const m = JSON.parse(ev.data);
      if (m.type === 'hdr' || m.type === 'meta') hdr = { scale: m.scale, origin: m.origin, tier: m.tier };
      if (m.type === 'meta') { armed = m.armed; }
      return;
    }
    frames++;
    $('#fpsPill').textContent = 'frames: ' + frames;
    const img = new Image();
    img.onload = () => {
      const c = $('#scr');
      if (c.width !== img.width) { c.width = img.width; c.height = img.height; $('#ovl').width = img.width; $('#ovl').height = img.height; }
      c.getContext('2d').drawImage(img, 0, 0);
    };
    img.src = URL.createObjectURL(new Blob([ev.data], { type: 'image/jpeg' }));
  };
  ws.onclose = () => setTimeout(connectView, 1500);
}
connectView();

/* ---- DRIVE MODE (design §6.6) ------------------------------------------
   Coordinate transform contract (MUST mirror the server-side §6):
   physical = origin + canvasCssPx * (dpr / canvasCssScale) where
   canvasCssScale = canvasCssWidth / frameWidth  — i.e. what the <canvas>
   element was visually scaled to in CSS. The SERVER re-applies its own
   inverse for rendering marks; T-13 asserts the drawn pixel end-to-end,
   which is the only thing that keeps this honest across browsers/DPI. */
function physFromEvent(ev) {
  const c = $('#scr');
  const r = c.getBoundingClientRect();
  const px = (ev.clientX - r.left) * (c.width / r.width) * (window.devicePixelRatio > 1 && c.width > r.width ? 1 : 1) / hdr.scale;
  const py = (ev.clientY - r.top) * (c.height / r.height) * 1 / hdr.scale;
  return { x: Math.round(hdr.origin.x + px), y: Math.round(hdr.origin.y + py) };
}
let dws = null;
function connectDrive() {
  dws = new WebSocket(`ws://${location.host}/ws/drive?token=${encodeURIComponent(TOKEN)}`);
  dws.onclose = () => { if (driveOn) setTimeout(connectDrive, 1000); $('#driveLock').hidden = true; $('#ckDrive').checked = false; };
}
function sendD(m) { if (dws && dws.readyState === 1) dws.send(JSON.stringify(m)); }
$('#scrWrap').addEventListener('contextmenu', e => e.preventDefault());
$('#scrWrap').addEventListener('mousedown', e => {
  if ($('#pick').dataset.on === '1') { pickAt(e); return; }
  if (!driveOn) return;
  const p = physFromEvent(e);
  sendD({ k: e.button === 2 ? 'down' : 'down', x: p.x, y: p.y, b: e.button === 2 ? 'right' : e.button === 1 ? 'middle' : 'left' });
});
$('#scrWrap').addEventListener('mousemove', e => { if (driveOn) { const p = physFromEvent(e); sendD({ k: 'move', x: p.x, y: p.y }); } });
$('#scrWrap').addEventListener('mouseup', e => { if (driveOn) { const p = physFromEvent(e); sendD({ k: 'up', x: p.x, y: p.y, b: e.button === 2 ? 'right' : e.button === 1 ? 'middle' : 'left' }); } });
$('#scrWrap').addEventListener('wheel', e => { if (driveOn) { e.preventDefault(); const p = physFromEvent(e); sendD({ k: 'wheel', x: p.x, y: p.y, dy: -Math.sign(e.deltaY), dx: 0 }); } }, { passive: false });
window.addEventListener('keydown', e => {
  if (!driveOn || e.target.tagName === 'INPUT') return;
  const m = { ControlLeft: 'ctrl', ControlRight: 'ctrl', AltLeft: 'alt', AltRight: 'alt', ShiftLeft: 'shift', ShiftRight: 'shift', MetaLeft: 'win', Escape: 'esc' };
  let k = m[e.code] || (e.key.length === 1 ? e.key : e.key.toLowerCase());
  if (k) sendD({ k: 'key', keys: k });
});

// ---------- inspect tab ------------------------------------------------------
let treeLoaded = false, selRef = null, treeNodes = [];
async function rescan() {
  try {
    const t = await api('/tree?depth=6&max_nodes=1500');
    treeNodes = t.nodes;
    renderTree();
    treeLoaded = true;
    $('#treeInfo').textContent = `${t.nodes.length} nodes ${t.truncated ? '(truncated)' : ''}`;
    drawOverlay();
  } catch (e) { $('#tree').textContent = e.message; }
}
$('#rescan').onclick = rescan;
function renderTree() {
  const el = $('#tree'); el.innerHTML = '';
  for (const n of treeNodes) {
    const row = document.createElement('div');
    row.className = 'trow'; row.style.paddingLeft = (4 + n.depth * 10) + 'px';
    row.innerHTML = `<span class="ct">${(n.control_type || '?')}</span> <span class="nm">${esc((n.name || '').slice(0, 34))}</span> <span class="rf">${n.ref}</span>`;
    row.onmouseenter = () => highlight(n);
    row.onclick = () => select(n);
    el.appendChild(row);
  }
}
function esc(s) { return (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;'); }
function highlight(n) {
  if (!n.rect) return;
  drawOverlay(n);
}
function drawOverlay(hot) {
  const o = $('#ovl'); const g = o.getContext('2d');
  g.clearRect(0, 0, o.width, o.height);
  for (const n of treeNodes) {
    if (!n.rect) continue;
    const x = (n.rect.x - hdr.origin.x) * hdr.scale, y = (n.rect.y - hdr.origin.y) * hdr.scale;
    const w = n.rect.w * hdr.scale, h = n.rect.h * hdr.scale;
    if (n.ref === (hot && hot.ref)) { g.strokeStyle = '#f43f5e'; g.lineWidth = 3; }
    else if (!n.actionable) continue;
    else { g.strokeStyle = 'rgba(244,63,94,.35)'; g.lineWidth = 1; }
    g.strokeRect(x, y, w, h);
  }
}
async function select(n) {
  selRef = n.ref;
  const sp = $('#selPill'); if (sp) { sp.textContent = 'ref ' + n.ref; sp.className = 'pill act'; }
  try {
    const p = await api('/properties?ref=' + encodeURIComponent(n.ref));
    let html = '<table>'; for (const [k, v] of Object.entries(p)) html += `<tr><td>${k}</td><td>${esc(String(v).slice(0, 300))}</td></tr>`;
    html += '</table><div>';
    const pats = (p.patterns || '').split(',').filter(Boolean);
    for (const pat of ['invoke', 'toggle', 'select'])
      if (pats.includes(pat)) html += `<button class="pbtn" data-p="${pat}">${pat}</button>`;
    if (pats.includes('expand')) html += `<button class="pbtn" data-p="expand">expand</button><button class="pbtn" data-p="collapse">collapse</button>`;
    if (pats.includes('scroll')) html += `<button class="pbtn" data-p="scroll+" title="wheel down">scroll▾</button><button class="pbtn" data-p="scroll-" title="wheel up">scroll▴</button>`;
    if (pats.includes('value')) html += `<button class="pbtn" data-p="value">set_value…</button>`;
    html += ` <button class="pbtn" data-p="focus">focus</button>
      <button class="pbtn" data-p="click">click</button>
      <button class="pbtn" data-p="typetarget" title="route the Operator type box to this ref">type here ▸</button></div>`;
    $('#props').innerHTML = html;
    for (const b of $('#props').querySelectorAll('.pbtn')) b.onclick = async () => {
      try {
        const P = b.dataset.p;
        if (P === 'typetarget') { $('#opText').focus(); $('#opText').placeholder = 'type into ' + n.ref; $('#paneOperator').hidden = false; for (const x of document.querySelectorAll('#tabs button')) { x.classList.toggle('on', x.dataset.tab === 'operator'); } for (const s of document.querySelectorAll('main section')) s.hidden = s.id !== 'paneOperator'; return; }
        if (P === 'value') {
          const v = prompt('new value'); if (v === null) return;
          await job('value', { req: n.ref, value: v, verify: true });
        } else if (P === 'collapse') await job('expand', { req: n.ref, expand: false, verify: true });
        else if (P === 'scroll+' || P === 'scroll-') await job('scroll', { req: n.ref, delta_y: P === 'scroll+' ? -3 : 3, delta_x: 0 });
        else if (P === 'click') await job('click', { req: n.ref, verify: true });
        else await job(P, { req: n.ref, verify: true });
        rescan();
      } catch (e) { alert(e.message); }
    };
  } catch (e) { $('#props').textContent = e.message; }
}
// crosshair pick: click canvas → element_at → scroll tree to it
$('#pick').onclick = () => {
  const on = $('#pick').dataset.on === '1';
  $('#pick').dataset.on = on ? '0' : '1';
  $('#pick').style.background = on ? '' : 'var(--acc)';
};
async function pickAt(e) {
  $('#pick').dataset.on = '0'; $('#pick').style.background = '';
  const p = physFromEvent(e);
  try {
    const n = await api('/element_at?x=' + p.x + '&y=' + p.y);
    for (const b of document.querySelectorAll('#tabs button')) if (b.dataset.tab === 'inspect') b.click();
    if (!treeLoaded) await rescan();
    selRef = n.ref;
    const n2 = treeNodes.find(t => t.ref === n.ref) || n;
    select(n2);
  } catch (err) { logLine('pick: ' + err.message); }
}

// ---------- operator tab -----------------------------------------------------
let opN = 0;
async function opsRefresh() {
  const { items } = await api('/audit?limit=60');   // both hosts — web acts visible too
  const el = $('#ops'); el.innerHTML = '';
  for (const it of items.slice().reverse()) {
    const d = document.createElement('div'); d.className = 'op';
    const res = it.res || {}, target = it.target || {}, guard = it.guard || {};
    d.innerHTML = `<div class="hd"><b>${it.tool}</b>
      <span class="badge ${res.ok ? 'ok' : 'err'}">${res.ok ? 'ok' : (res.code || 'err')}</span>
      <span class="pill">${res.ms ?? '—'}ms</span>
      <span class="pill">armed:${guard.armed || '?'}</span>
      ${it.args_red ? `<span class="pill">${esc(JSON.stringify(it.args_red).slice(0, 90))}</span>` : ''}</div>`;
    const shots = document.createElement('div'); shots.className = 'shots';
    for (const s of [res.before_shot, res.after_shot].filter(x => x)) {
      const img = document.createElement('img');
      img.src = s + '?token=' + encodeURIComponent(TOKEN);
      img.onclick = () => { $('#lbImg').src = img.src; $('#lightbox').style.display = 'flex'; };
      shots.appendChild(img);
    }
    if (shots.children.length) d.appendChild(shots);
    el.appendChild(d);
  }
  if (!el.children.length) el.textContent = 'no agent actions yet (host=mcp)';
}
$('#lightbox').onclick = () => $('#lightbox').style.display = 'none';

// ---------- audit tab ----------------------------------------------------------
async function auditRefresh() {
  const f = $('#auditFilter').value.trim();
  const { items } = await api('/audit?limit=200' + (f ? '&tool=' + encodeURIComponent(f) : ''));
  let html = '<table><tr><th>seq</th><th>ts</th><th>host</th><th>tool</th><th>ok</th><th>args</th></tr>';
  for (const it of items.slice().reverse()) {
    html += `<tr><td>${it.seq}</td><td>${(it.ts || '').slice(11, 23)}</td><td>${it.host}</td><td>${it.tool}</td>
      <td>${it.res && it.res.ok ? '✓' : '✗'}</td><td>${esc(JSON.stringify(it.args_red || {}).slice(0, 120))}</td></tr>`;
  }
  html += '</table>';
  $('#audit').innerHTML = html;
  $('#csv').href = '/api/audit?token=' + encodeURIComponent(TOKEN) + '&limit=5000&csv=1';
}
$('#auditRefresh').onclick = auditRefresh;
$('#auditFilter').onkeydown = e => { if (e.key === 'Enter') auditRefresh(); };

// ---------- target tab ----------------------------------------------------------
async function targetRefresh() {
  try { $('#tgt').textContent = JSON.stringify(await api('/status'), null, 2); }
  catch (e) { $('#tgt').textContent = e.message; }
}

// ---------- marks snapshot (set-of-marks, parity with pilot_screenshot) ----
let lastMarks = [];
async function marksSnapshot() {
  try {
    const s = await api('/screenshot', { marks: true, max_edge: 1568, quality: 75 });
    lastMarks = s.marks || [];
    // paint the marked shot onto the screen canvas (pauses live frames briefly)
    const img = new Image();
    img.onload = () => {
      const c = $('#scr');
      if (c.width !== img.width) { c.width = img.width; c.height = img.height; $('#ovl').width = img.width; $('#ovl').height = img.height; }
      c.getContext('2d').drawImage(img, 0, 0);
    };
    img.src = s.image_url + '?token=' + encodeURIComponent(TOKEN);
    renderChips();
    logLine('marks: ' + lastMarks.length + (s.empty_marks_hint ? ' (' + s.empty_marks_hint + ')' : ''));
  } catch (e) { alert('marks: ' + e.message); }
}
function renderChips() {
  const el = $('#markChips'); el.innerHTML = '';
  for (const m of lastMarks) {
    const b = document.createElement('span');
    b.className = 'chip'; b.textContent = m.n + ' ' + (m.control_type || '') + ' ' + (m.name || '').slice(0, 18);
    b.title = 'click mark ' + m.n + ' → ' + m.ref;
    b.onmouseenter = () => drawMarkBox(m);
    b.onclick = async () => {
      try { await job('click', { mark: m.n, verify: true }); logLine('clicked mark ' + m.n + ' (' + m.ref + ')'); }
      catch (e) { alert(e.message); }
    };
    el.appendChild(b);
  }
}
function drawMarkBox(m) {
  const o = $('#ovl'); const g = o.getContext('2d');
  drawOverlay();
  if (!m.rect) return;
  g.strokeStyle = '#f43f5e'; g.lineWidth = 3;
  g.strokeRect((m.rect.x - hdr.origin.x) * hdr.scale, (m.rect.y - hdr.origin.y) * hdr.scale,
    m.rect.w * hdr.scale, m.rect.h * hdr.scale);
}
$('#marksBtn').onclick = marksSnapshot;
$('#pickXY').onclick = opClickXY;
$('#snapBtn').onclick = async () => {
  try {
    const s = await api('/screenshot', { marks: $('#ckMarks').checked, max_edge: 3840, quality: 92 });
    window.open(s.image_url + '?token=' + encodeURIComponent(TOKEN), '_blank');
  } catch (e) { alert(e.message); }
};

// ---------- operator act console (full engine surface) ----------------------
function opClickXY() {           // click the screen canvas to fill x/y boxes
  const on = $('#scr').dataset.xy === '1';
  $('#scr').dataset.xy = on ? '0' : '1';
  logLine(on ? 'xy pick off' : 'click the screen to fill x,y (physical px)');
}
$('#scrWrap').addEventListener('click', e => {
  if ($('#scr').dataset.xy !== '1') return;
  const p = physFromEvent(e);
  $('#opX').value = p.x; $('#opY').value = p.y;
  $('#scr').dataset.xy = '0';
  for (const x of document.querySelectorAll('#tabs button')) if (x.dataset.tab === 'operator') x.click();
});
document.querySelectorAll('[data-act]').forEach(b => b.onclick = async () => {
  const T = b.dataset.act;
  const n = v => $(v).value.trim();
  const num = v => { const x = parseInt($(v).value, 10); return isNaN(x) ? null : x; };
  const args = {
    launch: () => ({ cmd: n('#opCmd'), args: n('#opArgs') || undefined, workdir: n('#opWdir') || undefined, wait_window_ms: 8000 }),
    kill: () => ({ process: n('#opProc') || undefined, pid: num('#opPid') || undefined }),
    type: () => ({ text: n('#opText'), mode: $('#opMode').value, verify: true }),
    keys: () => ({ keys: n('#opKeys'), repeat: num('#opRepeat') || 1 }),
    click: () => ({ x: num('#opX'), y: num('#opY'), button: $('#opBtn').value, clicks: num('#opClicks') || 1 }),
    drag: () => ({ x1: num('#opX'), y1: num('#opY'), x2: num('#opX2'), y2: num('#opY2') }),
    scroll: () => ({ delta_y: num('#opDy') || 0, delta_x: num('#opDx') || 0 }),
    wait: () => ({
      condition: {
        type: $('#opWaitType').value,
        process: n('#opWaitProc') || undefined, title_regex: n('#opWaitTitle') || undefined,
        contains: n('#opWaitText') || undefined,
        ref: ($('#opWaitType').value === 'text_matches' ? selRef : undefined),
      },
      timeout_s: num('#opWaitTo') || 15,
    }),
  }[T]();
  if (T === 'wait') $('#waitPill').textContent = 'waiting…';
  try {
    const r = await job(T, args);
    if (T === 'wait') $('#waitPill').textContent = 'matched:' + (r && r.matched) + (r && r.waited_ms ? ' ' + r.waited_ms + 'ms' : '');
    else logLine(T + ' ok');
    opsRefresh();
  } catch (e) {
    $('#waitPill').textContent = '';
    alert(T + ': ' + e.message);
  }
});
// ref-carrying ops: type/click/value reuse the inspected ref
const _origJob = job;
job = async function (tool, args) {
  if (selRef && ['type'].includes(tool) && !args.req) args = Object.assign({ req: selRef }, args);
  return _origJob(tool, args);
};

// ---------- drive arming (parity: /arm scope=drive, then the ws opens) ------
$('#ckDrive').onchange = async e => {
  driveOn = e.target.checked;
  $('#driveLock').hidden = !driveOn;
  if (!driveOn) { if (dws) dws.close(); return; }
  try { await api('/arm', { scope: 'drive', ttl_min: 15, why: 'console-drive' }); status(); connectDrive(); }
  catch (err) { alert('drive arm failed: ' + err.message); $('#ckDrive').checked = false; driveOn = false; $('#driveLock').hidden = true; }
};

window.addEventListener('error', e => logLine('js: ' + e.message));
