(() => {
  const $ = id => document.getElementById(id), M = TecMap;
  let state, draft, selected, dirty = false, saving = false, view = { scale: .1, x: 0, y: 0 }, drag;
  const preview = new URLSearchParams(location.search).has('preview');
  const rects = () => M.rectangles(state, draft.placements);
  const selectedRect = () => rects().find(s => M.key(s) === selected);
  const opposite = { left: 'right', right: 'left', top: 'bottom', bottom: 'top' };
  const names = { left: 'Izquierda', right: 'Derecha', top: 'Arriba', bottom: 'Abajo' };
  function toast(message) { $('toast').textContent = message; $('toast').classList.add('visible'); setTimeout(() => $('toast').classList.remove('visible'), 4500); }
  function theme() { document.documentElement.dataset.theme = draft.theme === 'system' ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : draft.theme; }
  function send(message) {
    if (!preview) { window.chrome?.webview?.postMessage(message); return; }
    window.__tecMessages.push(message);
    if (message.type === 'save') setTimeout(() => receive({ type: 'saved', state: { ...state, ...message.map, spatialEnabled: true }, message: 'Mapa guardado en ambas PCs.' }), 180);
  }
  function receive(message) {
    if (message.type === 'error') { saving = false; dirty = true; toast(message.message); update(); return; }
    if (!message.state) return;
    state = message.state;
    if (!draft || message.type === 'saved') {
      draft = { placements: state.placements?.length ? structuredClone(state.placements) : M.defaults(state), automatic: state.automatic, theme: state.theme || 'system' };
      dirty = !state.spatialEnabled; saving = false;
      if (message.type === 'saved') toast(message.message || 'Mapa guardado.');
    } else {
      const screens = M.all(state), defaults = M.defaults(state);
      draft.placements = draft.placements.filter(p => screens.some(s => M.key(s) === M.key(p)));
      for (const screen of screens) if (!draft.placements.some(p => M.key(p) === M.key(screen))) { draft.placements.push(defaults.find(p => M.key(p) === M.key(screen))); dirty = true; }
    }
    if (!selected || !rects().some(s => M.key(s) === selected)) selected = M.key(rects()[0] || { id: '', remote: false });
    $('automatic').checked = draft.automatic; $('theme').value = draft.theme; theme(); buildNodes(); update(); fit();
  }
  function fit() {
    if (!state) return;
    const b = M.bounds(rects()), board = $('spatial-map');
    view.scale = Math.max(.015, Math.min(.5, (board.clientWidth - 56) / b.width, (board.clientHeight - 68) / b.height));
    view.x = (board.clientWidth - b.width * view.scale) / 2 - b.x * view.scale;
    view.y = (board.clientHeight - b.height * view.scale) / 2 - b.y * view.scale; draw();
  }
  function buildNodes() {
    $('map-nodes').replaceChildren();
    rects().forEach((s, n) => {
      const node = document.createElement('button'); node.className = 'monitor ' + (s.remote ? 'remote-monitor' : 'local-monitor'); node.dataset.key = M.key(s);
      node.setAttribute('aria-label', `${s.remote ? state.remoteName || 'Otra PC' : state.machineName}, ${s.label || 'Pantalla'}, ${s.width} por ${s.height}`);
      for (const [tag, value] of [['strong', String(n + 1)], ['span', s.remote ? state.remoteName || 'Otra PC' : state.machineName], ['small', `${s.width} × ${s.height}`]]) { const el = document.createElement(tag); el.textContent = value; node.append(el); }
      node.addEventListener('pointerdown', e => {
        if (saving || e.button !== 0) return; e.preventDefault(); e.stopPropagation(); selected = M.key(s);
        const p = draft.placements.find(p => M.key(p) === selected);
        drag = { id: e.pointerId, node, startX: e.clientX, startY: e.clientY, x: p.x, y: p.y, original: structuredClone(draft.placements) };
        node.setPointerCapture(e.pointerId); update();
      });
      node.addEventListener('pointermove', e => {
        if (!drag || drag.node !== node) return;
        const p = draft.placements.find(p => M.key(p) === selected);
        Object.assign(p, M.snap(state, draft.placements, selected, drag.x + (e.clientX - drag.startX) / view.scale, drag.y + (e.clientY - drag.startY) / view.scale, 12 / view.scale)); update();
      });
      const end = () => { if (!drag || drag.node !== node) return; if (M.overlaps(rects())) { draft.placements = drag.original; toast('Las pantallas se superponen. Intenta unir sus bordes.'); } else dirty = true; drag = null; update(); };
      node.addEventListener('pointerup', end); node.addEventListener('pointercancel', end);
      node.addEventListener('keydown', e => { const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key]; if (d) { e.preventDefault(); selected = M.key(s); move(d[0] * (e.shiftKey ? 8 : 64), d[1] * (e.shiftKey ? 8 : 64)); } });
      $('map-nodes').append(node);
    });
  }
  function draw() {
    if (!state) return;
    for (const s of rects()) {
      const node = [...$('map-nodes').children].find(n => n.dataset.key === M.key(s));
      if (node) { Object.assign(node.style, { left: `${s.x * view.scale + view.x}px`, top: `${s.y * view.scale + view.y}px`, width: `${s.width * view.scale}px`, height: `${s.height * view.scale}px` }); node.classList.toggle('selected', M.key(s) === selected); node.setAttribute('aria-pressed', M.key(s) === selected ? 'true' : 'false'); }
    }
    $('map-links').replaceChildren();
    M.contacts(rects()).forEach(c => {
      const vertical = c.side === 'left' || c.side === 'right', line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
      const a = vertical ? [c.fixed, c.from, c.fixed, c.to] : [c.from, c.fixed, c.to, c.fixed];
      ['x1', 'y1', 'x2', 'y2'].forEach((k, n) => line.setAttribute(k, a[n] * view.scale + (n % 2 ? view.y : view.x))); $('map-links').append(line);
    });
    $('zoom-label').textContent = Math.round(view.scale * 100) + ' %';
  }
  function update() {
    if (!state) return;
    const contacts = M.contacts(rects()), overlaps = M.overlaps(rects()), s = selectedRect();
    $('connection').textContent = state.connected ? 'Conectado' : 'Sin conexión'; $('connection').classList.toggle('connected', state.connected);
    $('mode').textContent = { local: 'Control local', remote: 'Controlando la otra PC', peer: 'La otra PC tiene el mando' }[state.mode];
    const on = state.powered !== false, remote = state.mode === 'remote';
    $('power').setAttribute('aria-pressed', on); $('power-label').textContent = on ? 'Encendido' : 'Apagado';
    $('toggle-control').setAttribute('aria-checked', remote);
    $('switch-label').textContent = remote ? 'Mando: la otra PC' : state.mode === 'peer' ? 'Mando: la otra PC (te controla)' : 'Mando: esta PC';
    $('toggle-control').disabled = !on || !state.connected || saving;
    $('link-count').textContent = contacts.length + (contacts.length === 1 ? ' unión' : ' uniones');
    $('map-warning').textContent = overlaps ? 'Las pantallas se superponen. Sepáralas antes de guardar.' : contacts.length ? 'La línea verde marca cada paso del ratón.' : 'Sin rutas: acerca los bordes de dos pantallas para unirlas.';
    $('sync-message').textContent = state.peerMapSupported ? 'Al guardar, el mapa se comparte con la otra PC.' : 'Actualiza TecLink en la otra PC para compartir este mapa. Se puede guardar aquí.';
    $('settings-title').textContent = s ? `${s.label || 'Pantalla'} · ${s.remote ? 'Otra PC' : 'Esta PC'}` : 'Elige una pantalla';
    $('selected-size').textContent = s ? `${s.remote ? state.remoteName || 'Otra PC' : state.machineName} · ${s.width} × ${s.height}` : 'Conecta una pantalla.';
    if (s) { if (document.activeElement !== $('position-x')) $('position-x').value = s.x; if (document.activeElement !== $('position-y')) $('position-y').value = s.y; }
    $('routes-list').replaceChildren();
    contacts.filter(c => M.key(c.a) === selected || M.key(c.b) === selected).forEach(c => { const first = M.key(c.a) === selected, target = first ? c.b : c.a, side = first ? c.side : opposite[c.side], li = document.createElement('li'); li.textContent = `${names[side]} ↔ ${names[opposite[side]]} · ${target.remote ? state.remoteName || 'Otra PC' : 'Esta PC'} / ${target.label || 'Pantalla'}`; $('routes-list').append(li); });
    if (!$('routes-list').children.length) { const li = document.createElement('li'); li.textContent = 'Sin bordes unidos.'; $('routes-list').append(li); }
    $('save').disabled = saving || overlaps; $('save').textContent = saving ? 'Guardando…' : dirty ? 'Guardar mapa' : 'Mapa guardado';
    $('save-heading').textContent = dirty ? 'Cambios sin guardar' : 'Mapa actual'; $('save-message').textContent = saving ? 'Esperando confirmación…' : 'Los cambios se aplican al guardar.';
    ['theme', 'automatic', 'reset-map', 'position-x', 'position-y'].forEach(id => $(id).disabled = saving);
    document.querySelectorAll('[data-dx]').forEach(b => b.disabled = saving);
    for (const [id, value] of Object.entries({ 'connection-title': state.connected ? 'Tus equipos están conectados' : 'Esperando conexión', 'detail-name': state.machineName, 'detail-remote': state.remoteName || '—', 'detail-role': state.role === 'Server' ? 'Servidor' : 'Cliente', 'detail-host': state.host, 'detail-port': state.port, 'detail-status': state.status })) $(id).textContent = value;
    const extras = !!state.extras, level = state.pointerSpeed || 10, size = state.cursorSize || 100, style = state.cursorStyle || 'system';
    if (!pointerBusy) { $('speed').value = level; $('cursor-size').value = size; chosenStyle = style; }
    $('speed-label').textContent = `${$('speed').value} de 20`; $('size-label').textContent = `${$('cursor-size').value} %`;
    document.querySelectorAll('.swatch').forEach(b => b.setAttribute('aria-checked', b.dataset.style === chosenStyle ? 'true' : 'false'));
    $('cursor-size').disabled = chosenStyle === 'system';
    $('mouse-sync').textContent = extras ? 'Sincronizado con la otra PC.' : 'La otra PC no está conectada o necesita esta versión de TecLink: por ahora los cambios se guardan solo aquí.';
    $('peer-sync').textContent = extras ? '' : 'Conecta la otra PC con esta misma versión de TecLink para ver su pantalla y enviar archivos.';
    ['view-screen', 'pick-files', 'pick-folder'].forEach(id => $(id).disabled = !extras);
    $('dropzone').setAttribute('aria-disabled', extras ? 'false' : 'true');
    $('transfer-status').textContent = state.transfer || 'Sin envíos todavía.';
    if (state.receiveFolder) $('receive-folder').textContent = state.receiveFolder;
    draw();
  }
  let pointerBusy = false, pointerTimer, chosenStyle = 'system';
  function sendPointer() {
    pointerBusy = true; clearTimeout(pointerTimer);
    $('speed-label').textContent = `${$('speed').value} de 20`; $('size-label').textContent = `${$('cursor-size').value} %`;
    pointerTimer = setTimeout(() => { send({ type: 'pointer', speed: +$('speed').value, style: chosenStyle, size: +$('cursor-size').value }); setTimeout(() => pointerBusy = false, 400); }, 160);
  }
  $('speed').oninput = sendPointer; $('cursor-size').oninput = sendPointer;
  document.querySelectorAll('.swatch').forEach(b => b.onclick = () => { chosenStyle = b.dataset.style; document.querySelectorAll('.swatch').forEach(o => o.setAttribute('aria-checked', o === b ? 'true' : 'false')); $('cursor-size').disabled = chosenStyle === 'system'; sendPointer(); });
  $('view-screen').onclick = () => send({ type: 'viewScreen' });
  $('pick-files').onclick = () => send({ type: 'pickFiles' }); $('pick-folder').onclick = () => send({ type: 'pickFolder' });
  $('open-received').onclick = () => send({ type: 'openReceived' });
  $('dropzone').onclick = () => { if (state?.extras) send({ type: 'pickFiles' }); };
  $('dropzone').onkeydown = e => { if ((e.key === 'Enter' || e.key === ' ') && state?.extras) { e.preventDefault(); send({ type: 'pickFiles' }); } };
  addEventListener('dragover', e => { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = state?.extras ? 'copy' : 'none'; $('dropzone').classList.toggle('over', !!state?.extras); });
  addEventListener('dragleave', e => { if (!e.relatedTarget) $('dropzone').classList.remove('over'); });
  addEventListener('drop', e => {
    e.preventDefault(); $('dropzone').classList.remove('over');
    const files = [...(e.dataTransfer?.files || [])];
    if (!state?.extras || !files.length) return;
    if (preview) { window.__tecMessages.push({ type: 'dropFiles', count: files.length }); return; }
    window.chrome.webview.postMessageWithAdditionalObjects({ type: 'dropFiles' }, files);
  });
  function move(dx, dy) { if (saving) return; const p = draft.placements.find(p => M.key(p) === selected); if (!p) return; p.x += dx; p.y += dy; dirty = true; update(); }
  document.querySelectorAll('[data-dx]').forEach(b => b.onclick = () => move(+b.dataset.dx, +b.dataset.dy));
  ['x', 'y'].forEach(axis => $('position-' + axis).onchange = e => { const p = draft.placements.find(p => M.key(p) === selected), n = Number(e.target.value); if (p && Number.isInteger(n) && Math.abs(n) <= 1000000) { p[axis] = n; dirty = true; update(); } });
  $('automatic').onchange = e => { draft.automatic = e.target.checked; dirty = true; update(); };
  $('theme').onchange = e => { draft.theme = e.target.value; dirty = true; theme(); applyColors(); update(); };
  $('save').onclick = () => { if (saving || M.overlaps(rects())) return; saving = true; update(); send({ type: 'save', map: structuredClone(draft) }); };
  $('toggle-control').onclick = () => send({ type: 'toggleControl' });
  $('power').onclick = () => send({ type: 'setPower', on: $('power').getAttribute('aria-pressed') !== 'true' });
  $('fit-map').onclick = fit;
  function zoom(factor) { const board = $('spatial-map'), old = view.scale, next = Math.max(.015, Math.min(.5, old * factor)), cx = board.clientWidth / 2, cy = board.clientHeight / 2; view.x = cx - (cx - view.x) * next / old; view.y = cy - (cy - view.y) * next / old; view.scale = next; draw(); }
  $('zoom-in').onclick = () => zoom(1.25); $('zoom-out').onclick = () => zoom(.8);
  $('reset-map').onclick = () => { draft.placements = M.defaults(state); dirty = true; update(); fit(); };
  document.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => { document.querySelectorAll('[data-tab]').forEach(t => { t.classList.toggle('active', t === b); t.setAttribute('aria-selected', t === b ? 'true' : 'false'); }); ['screens', 'peer', 'connection'].forEach(name => $(name + '-tab').hidden = b.dataset.tab !== name); $('savebar').hidden = b.dataset.tab !== 'screens'; if (b.dataset.tab === 'screens') fit(); });
  // Botón "Cambiar de pantalla": recorre todas las pantallas (esta PC y la otra).
  $('next-screen').onclick = () => { const all = rects(); if (!all.length) return; const i = all.findIndex(s => M.key(s) === selected); selected = M.key(all[(i + 1) % all.length]); update(); };
  // Configuración (engranaje): apariencia de la plataforma y ratón.
  const drawer = $('settings-drawer'), scrim = $('settings-scrim');
  function openSettings(open) { drawer.hidden = scrim.hidden = !open; if (open) $('settings-close').focus(); else $('settings-open').focus(); }
  $('settings-open').onclick = () => openSettings(true); $('settings-close').onclick = () => openSettings(false); scrim.onclick = () => openSettings(false);
  addEventListener('keydown', e => { if (e.key === 'Escape' && !drawer.hidden) openSettings(false); });
  const colorMap = { bg: ['--bg'], panel: ['--panel', '--raised'], line: ['--line', '--grid'], accent: ['--accent'], text: ['--text'] };
  const saved = () => { try { return JSON.parse(localStorage.getItem('tecColors') || '{}'); } catch { return {}; } };
  function applyColors() {
    const c = saved();
    for (const [key, vars] of Object.entries(colorMap)) vars.forEach(v => c[key] ? document.documentElement.style.setProperty(v, c[key]) : document.documentElement.style.removeProperty(v));
    document.querySelectorAll('[data-var]').forEach(i => { const v = getComputedStyle(document.documentElement).getPropertyValue(colorMap[i.dataset.var][0]).trim(); i.value = c[i.dataset.var] || (/^#[0-9a-f]{6}$/i.test(v) ? v : '#000000'); });
  }
  document.querySelectorAll('[data-var]').forEach(i => i.oninput = () => { const c = saved(); c[i.dataset.var] = i.value; localStorage.setItem('tecColors', JSON.stringify(c)); applyColors(); });
  $('colors-reset').onclick = () => { localStorage.removeItem('tecColors'); applyColors(); };
  applyColors();
  new ResizeObserver(() => fit()).observe($('spatial-map'));
  window.__tecTest = { receive, draft: () => structuredClone(draft), select: key => { selected = key; update(); } };
  if (preview) {
    window.__tecMessages = [];
    receive({ type: 'state', state: { machineName: 'JENAV', remoteName: 'RAFAEL', connected: true, mode: 'local', role: 'Client', host: '192.168.12.183', port: 51777, status: 'Conexión por red local', extras: true, pointerSpeed: 10, cursorStyle: 'green', cursorSize: 150, transfer: '', receiveFolder: 'C:\\Users\\jenav\\Downloads\\TecLink', automatic: true, theme: 'dark', peerMapSupported: true, spatialEnabled: true,
      local: [{ id: 'screen1', label: 'Pantalla 1', primary: true, left: 0, top: 0, width: 2240, height: 1400 }, { id: 'screen2', label: 'Pantalla 2 · HDMI', left: -1600, top: -2160, width: 3840, height: 2160 }], remote: [{ id: 'screen1', label: 'Pantalla 1', left: 0, top: 0, width: 1920, height: 1080 }], placements: [{ id: 'screen1', remote: false, x: 0, y: 0 }, { id: 'screen2', remote: false, x: -1600, y: -2160 }, { id: 'screen1', remote: true, x: -1920, y: 0 }] } });
  } else { window.chrome?.webview?.addEventListener('message', e => receive(e.data)); send({ type: 'ready' }); }
})();
