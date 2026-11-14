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
    if (message.type === 'cameraFrame') { TecRoom.peerFrame(message.data); return; }
    if (message.type === 'myFiles') { renderFiles('my-files', message.items, false); return; }
    if (message.type === 'peerFiles') { renderFiles('peer-files', message.items, true); return; }
    if (message.type === 'chat') { TecChat.incoming(message.from, message.text, message.at); return; }
    if (message.type === 'voice') { TecChat.voice(message.data); return; }
    if (message.type === 'diagnostic') { $('diag-out').textContent = message.lines.join('\n'); $('diag-out').hidden = false; $('diag-run').disabled = false; $('diag-run').textContent = 'Probar conexión'; $('diag-copy').hidden = false; return; }
    if (message.type === 'toast') { toast(message.message); return; }
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
      if (node) { node.classList.toggle('offline', !!s.remote && !state.connected); Object.assign(node.style, { left: `${s.x * view.scale + view.x}px`, top: `${s.y * view.scale + view.y}px`, width: `${s.width * view.scale}px`, height: `${s.height * view.scale}px` }); node.classList.toggle('selected', M.key(s) === selected); node.setAttribute('aria-pressed', M.key(s) === selected ? 'true' : 'false'); }
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
    const removed = draft.placements.filter(p => p.hidden).length, admin = state.role === 'Server';
    $('restore-screens').hidden = !removed || !admin; $('restore-screens').textContent = `Restaurar pantallas quitadas (${removed})`;
    $('remove-screen').disabled = !(admin && s && s.remote) || saving || rects().length <= 1;
    $('remove-note').textContent = !admin ? 'Solo el anfitrión de la sesión puede quitar pantallas.' : s && !s.remote ? 'Las pantallas propias no se pueden quitar: solo las de invitados.' : ''; $('remove-screen').textContent = removeArmed === selected ? '¿Seguro? Pulsa otra vez' : 'Quitar esta pantalla';
    TecRoom.render(state); TecChat.render(state);
    $('save').disabled = saving || overlaps; $('save').textContent = saving ? 'Guardando…' : dirty ? 'Guardar mapa' : 'Mapa guardado';
    $('save-heading').textContent = dirty ? 'Cambios sin guardar' : 'Mapa actual'; $('save-message').textContent = saving ? 'Esperando confirmación…' : 'Los cambios se aplican al guardar.';
    ['theme', 'automatic', 'reset-map', 'position-x', 'position-y'].forEach(id => $(id).disabled = saving);
    document.querySelectorAll('[data-dx]').forEach(b => b.disabled = saving);
    for (const [id, value] of Object.entries({ 'connection-title': state.connected ? 'Tus equipos están conectados' : 'Esperando conexión', 'detail-name': state.machineName, 'detail-remote': state.remoteName || '—', 'detail-role': state.role === 'Server' ? 'Anfitrión de la sesión' : state.joinCode ? 'Invitado' : 'Enlace directo por IP', 'detail-host': state.host, 'detail-port': state.port, 'detail-status': state.status })) $(id).textContent = value;
    peerChanges();
    updateSession();
    updateStatusBar();
    const extras = !!state.extras, level = state.pointerSpeed || 10, size = state.cursorSize || 100, style = state.cursorStyle || 'system';
    if (!pointerBusy) { $('speed').value = level; $('cursor-size').value = size; chosenStyle = style; }
    $('speed-label').textContent = `${$('speed').value} de 20`; $('size-label').textContent = `${$('cursor-size').value} %`;
    document.querySelectorAll('.swatch').forEach(b => b.setAttribute('aria-checked', b.dataset.style === chosenStyle ? 'true' : 'false'));
    if (/^#[0-9a-f]{6}$/i.test(chosenStyle)) $('color-mouse').value = chosenStyle;
    $('cursor-size').disabled = chosenStyle === 'system';
    $('mouse-sync').textContent = extras ? 'Sincronizado con la otra PC.' : 'La otra PC no está conectada o necesita esta versión de TecLink: por ahora los cambios se guardan solo aquí.';
    $('peer-sync').textContent = extras ? '' : 'Conecta la otra PC con esta misma versión de TecLink para ver su pantalla y enviar archivos.';
    ['pick-files', 'pick-folder'].forEach(id => $(id).disabled = !extras);
    $('theirs-title').textContent = state.remoteName || 'La otra PC';
    if (state.transfer !== lastTransfer || extras !== lastExtras) { lastTransfer = state.transfer; lastExtras = extras; refreshFiles(); }
    $('dropzone').setAttribute('aria-disabled', extras ? 'false' : 'true');
    $('transfer-status').textContent = state.transfer || 'Sin envíos todavía.';
    if (state.receiveFolder) $('receive-folder').textContent = state.receiveFolder;
    draw();
  }
  // ---- Barra de estado: siempre visible, con un boton grande que cambia segun lo que pasa ----
  function updateStatusBar() {
    const on = state.powered !== false, host = state.role === 'Server', name = state.remoteName || 'la otra PC';
    let dot = 'off', title = '', sub = '', main = null;
    if (!on) { title = 'TecLink está apagado'; sub = 'No comparte mouse ni teclado.'; main = { label: 'Encender', act: () => send({ type: 'setPower', on: true }) }; }
    else if (!state.connected) {
      const gone = state.peerStatus === 'left' ? `${name} salió de la sesión` : state.peerStatus === 'lost' ? `${name} se desconectó` : '';
      if (!host && state.joinCode && !gone) { dot = 'warn'; title = 'Buscando la sesión…'; sub = state.joinNotice || 'La otra PC debe tener TecLink abierto en esta misma red.'; main = { label: 'Cancelar', act: () => send({ type: 'session', action: 'host' }) }; }
      else { dot = gone ? 'bad' : 'off'; title = gone || 'Sin conexión'; sub = `Tu código: ${state.sessionCode || '—'}. Escríbelo en la otra PC, o conéctate al código de la otra.`; main = { label: 'Conectar a otra PC', act: () => { document.querySelector('[data-tab=connection]').click(); setTimeout(() => $('join-code').focus(), 50); } }; }
    } else if (state.mode === 'remote') { dot = 'live'; title = `Controlando a ${name}`; sub = 'Tu mouse y tu teclado están en la otra PC.'; main = { label: 'Volver a esta PC', act: () => send({ type: 'toggleControl' }) }; }
    else if (state.mode === 'peer') { dot = 'live'; title = `${name} tiene el mouse`; sub = 'La otra PC está manejando esta.'; main = { label: 'Recuperar mi mouse', act: () => send({ type: 'freeInput' }) }; }
    else if (state.sessionMode && !state.management) { dot = 'warn'; title = `Conectado con ${name}: el mouse NO pasa todavía`; sub = host ? 'Activa la gestión de pantallas para mover el mouse entre las dos PCs.' : 'El anfitrión debe activar la gestión de pantallas.'; main = host ? { label: 'Activar gestión', act: () => send({ type: 'setManagement', on: true }) } : null; }
    else { dot = 'ok'; title = `Conectado con ${name}`; sub = 'Lleva el mouse al borde de la pantalla, o pulsa el botón, para pasar a la otra PC.'; main = { label: `Pasar el mouse a ${name}`, act: () => send({ type: 'toggleControl' }) }; }
    $('sb-dot').className = 'sb-dot ' + dot; $('sb-title').textContent = title; $('sb-sub').textContent = sub;
    const b = $('sb-main'); b.hidden = !main; if (main) { b.textContent = main.label; b.onclick = main.act; }
  }
  $('sb-free').onclick = () => { send({ type: 'freeInput' }); toast('Mouse y teclado liberados en esta PC.'); };
  // ---- Salida de la otra PC: aviso y suite vacia ----
  let lastPeer;
  function peerChanges() {
    const now = state.peerStatus || 'none', name = state.remoteName || 'La otra PC';
    if (lastPeer === undefined) { lastPeer = now; return; }
    if (now === lastPeer) return;
    if (now === 'left') { toast(name + ' salió de la sesión.'); TecChat.system(name + ' salió de la sesión.'); }
    else if (now === 'lost') { toast(name + ' se desconectó.'); TecChat.system(name + ' se desconectó.'); }
    else if (now === 'connected') TecChat.system(name + ' se conectó.');
    else if (now === 'none') TecChat.reset();
    lastPeer = now;
  }
  // ---- Archivos compartidos: lista de cada PC con acciones ----
  let lastTransfer = null, lastExtras = null;
  const size = n => n >= 1 << 30 ? (n / (1 << 30)).toFixed(1) + ' GB' : n >= 1 << 20 ? (n / (1 << 20)).toFixed(1) + ' MB' : n >= 1024 ? Math.round(n / 1024) + ' KB' : n + ' B';
  function refreshFiles() { send({ type: 'listFiles' }); if (state?.extras) send({ type: 'listPeerFiles' }); else renderFiles('peer-files', [], true); }
  function renderFiles(id, items, remote) {
    const list = $(id); list.replaceChildren();
    if (!items.length) { const li = document.createElement('li'); li.className = 'empty'; li.textContent = remote ? (state?.extras ? 'La otra PC no tiene archivos en su carpeta de TecLink.' : 'Conecta la otra PC con esta versión para ver sus archivos.') : 'Todavía no hay archivos aquí. Lo que recibas aparecerá en esta lista.'; list.append(li); return; }
    for (const f of items) {
      const li = document.createElement('li'), info = document.createElement('div'), name = document.createElement('strong'), meta = document.createElement('small'), actions = document.createElement('div');
      info.className = 'fi-name'; actions.className = 'fi-actions'; name.textContent = (f.isDirectory ? '📁 ' : '') + f.name; name.title = f.name;
      meta.textContent = `${f.isDirectory ? 'Carpeta' : size(f.size)} · ${new Date(f.modified * 1000).toLocaleDateString('es')}`; info.append(name, meta);
      const add = (label, aria, fn) => { const b = document.createElement('button'); b.className = 'secondary'; b.textContent = label; b.setAttribute('aria-label', `${aria}: ${f.name}`); b.onclick = fn; actions.append(b); };
      if (remote) { add('Traer', 'Traer a esta PC', () => { send({ type: 'peerFileAction', action: 'fetch', name: f.name }); toast('Pidiendo el archivo…'); }); add('A su escritorio', 'Poner en el escritorio de la otra PC', () => { send({ type: 'peerFileAction', action: 'desktop', name: f.name }); toast('Enviado al escritorio de la otra PC.'); }); }
      else { add('Enviar', 'Enviar a la otra PC', () => send({ type: 'fileAction', action: 'send', name: f.name })); add('Al escritorio', 'Poner en mi escritorio', () => send({ type: 'fileAction', action: 'desktop', name: f.name })); add('Mostrar', 'Mostrar en la carpeta', () => send({ type: 'fileAction', action: 'show', name: f.name })); }
      li.append(info, actions); list.append(li);
    }
  }
  $('refresh-mine').onclick = $('refresh-theirs').onclick = refreshFiles;
  // ---- Conectar: sesión con código, interruptor de gestión y cuadrícula ----
  function updateSession() {
    const host = state.role === 'Server', legacy = !host && !state.joinCode, on = state.powered !== false, max = state.maxScreens || 10;
    $('session-code').textContent = state.sessionCode || '——';
    $('host-card').classList.toggle('inactive', !host);
    const guest = !host && !legacy;
    $('host-status').textContent = legacy ? 'Ahora usas un enlace directo por IP. Pulsa «Abrir mi propia sesión» para invitar con código.' : guest ? 'Estás unido a otra sesión. Sal de ella para invitar a alguien.' : state.connected ? `${state.remoteName || 'Otra PC'} está conectada a tu sesión.` : 'Esperando a que alguien se una con este código.';
    $('host-card').classList.toggle('inactive', !host);
    const addrs = state.addresses || [];
    $('host-address').textContent = addrs.length ? `Dirección de esta PC (por si la otra no la encuentra sola): ${addrs.join(' · ')}` : '';
    $('firewall-box').hidden = state.firewall !== false || !host;
    if (document.activeElement !== $('join-address') && !$('join-address').value && state.peerAddress) $('join-address').value = state.peerAddress;
    if (document.activeElement !== $('join-code') && guest) $('join-code').value = state.joinCode || '';
    $('leave-btn').hidden = host; $('leave-btn').textContent = legacy ? 'Abrir mi propia sesión' : 'Salir de la sesión';
    $('join-btn').hidden = guest; $('join-code').disabled = guest;
    $('join-notice').textContent = state.joinNotice || (!host && !state.connected && state.joinCode ? 'Buscando la sesión en tu red…' : !host && state.connected ? `Conectado a la sesión de ${state.remoteName || 'la otra PC'}.` : '');
    const m = !!state.management, sw = $('management');
    sw.hidden = legacy; $('management-note').hidden = legacy;
    sw.setAttribute('aria-checked', m); sw.disabled = !host || !on;
    $('management-label').textContent = host ? `Gestión de pantallas: ${m ? 'activada' : 'apagada'}` : `Gestión: ${m ? 'activada por el anfitrión' : 'la decide el anfitrión'}`;
    $('management-note').textContent = m ? 'Ambas PCs pueden ver y controlar las pantallas de la sesión.' : 'Las pantallas se suman al mapa, pero nadie las ve ni las controla hasta que el anfitrión active este interruptor.';
    const shown = (list, remote) => (list || []).filter(s => !(state.placements || []).some(p => p.hidden && p.id === s.id && !!p.remote === remote));
    const pcs = [{ name: state.machineName + ' (esta PC)', screens: shown(state.local, false) }];
    if (state.connected || (state.remote || []).length) pcs.push({ name: (state.remoteName || 'Otra PC') + (state.connected ? '' : ' (desconectada)'), screens: shown(state.remote, true), offline: !state.connected });
    const total = pcs.filter(p => !p.offline).reduce((n, p) => n + p.screens.length, 0), cols = Math.max(1, ...pcs.map(p => p.screens.length));
    $('screen-count').textContent = `${total} de ${max}`;
    const grid = $('screen-grid'); grid.replaceChildren(); grid.style.setProperty('--cols', cols);
    for (const pc of pcs) {
      const label = document.createElement('div'); label.className = 'grid-pc' + (pc.offline ? ' offline' : ''); label.textContent = pc.name; label.setAttribute('role', 'listitem'); grid.append(label);
      for (let i = 0; i < cols; i++) { const s = pc.screens[i], cell = document.createElement('div'); cell.className = 'grid-cell' + (s ? '' : ' empty') + (pc.offline ? ' offline' : ''); if (s) { const b = document.createElement('strong'), d = document.createElement('small'); b.textContent = s.label || `Pantalla ${i + 1}`; d.textContent = `${s.width} × ${s.height}`; cell.append(b, d); } grid.append(cell); }
    }
    $('grid-note').textContent = total >= max ? `Llegaste al límite de ${max} pantallas por sesión.` : `Caben ${max - total} pantallas más en esta sesión.`;
  }
  $('management').onclick = () => send({ type: 'setManagement', on: $('management').getAttribute('aria-checked') !== 'true' });
  $('copy-code').onclick = async () => { const code = (state?.sessionCode || '').replace('-', ''); if (!code) return; try { await navigator.clipboard.writeText(state.sessionCode); toast('Código copiado: ' + state.sessionCode); } catch { toast('Tu código es ' + state.sessionCode); } };
  $('join-code').oninput = e => { const raw = e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 6); e.target.value = raw.length > 3 ? raw.slice(0, 3) + '-' + raw.slice(3) : raw; };
  $('join-code').onkeydown = e => { if (e.key === 'Enter') $('join-btn').click(); };
  $('join-btn').onclick = () => { const code = $('join-code').value.replace(/[^A-Za-z0-9]/g, ''); if (code.length !== 6) { toast('El código tiene 6 letras o números.'); return; } send({ type: 'session', action: 'join', code, address: $('join-address').value.trim() }); toast('Buscando la sesión en tu red…'); };
  $('diag-run').onclick = () => { $('diag-run').disabled = true; $('diag-run').textContent = 'Probando…'; $('diag-out').hidden = false; $('diag-out').textContent = 'Revisando la red…'; send({ type: 'diagnose' }); };
  $('diag-copy').onclick = async () => { try { await navigator.clipboard.writeText($('diag-out').textContent); toast('Informe copiado. Pégalo donde te lo pidan.'); } catch { toast('Selecciona el texto del informe y cópialo.'); } };
  $('firewall-btn').onclick = () => { send({ type: 'session', action: 'firewall' }); toast('Windows te pedirá permiso: pulsa Sí.'); };
  $('leave-btn').onclick = () => send({ type: 'session', action: 'host' });
  let pointerBusy = false, pointerTimer, chosenStyle = 'system';
  function sendPointer() {
    pointerBusy = true; clearTimeout(pointerTimer);
    $('speed-label').textContent = `${$('speed').value} de 20`; $('size-label').textContent = `${$('cursor-size').value} %`;
    pointerTimer = setTimeout(() => { send({ type: 'pointer', speed: +$('speed').value, style: chosenStyle, size: +$('cursor-size').value }); setTimeout(() => pointerBusy = false, 400); }, 160);
  }
  $('speed').oninput = sendPointer; $('cursor-size').oninput = sendPointer;
  document.querySelectorAll('.swatch').forEach(b => b.onclick = () => { chosenStyle = b.dataset.style; document.querySelectorAll('.swatch').forEach(o => o.setAttribute('aria-checked', o === b ? 'true' : 'false')); $('cursor-size').disabled = chosenStyle === 'system'; sendPointer(); });
  $('color-mouse').oninput = () => { chosenStyle = $('color-mouse').value; document.querySelectorAll('.swatch').forEach(o => o.setAttribute('aria-checked', 'false')); $('cursor-size').disabled = false; sendPointer(); };
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
  document.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => { document.querySelectorAll('[data-tab]').forEach(t => { t.classList.toggle('active', t === b); t.setAttribute('aria-selected', t === b ? 'true' : 'false'); }); ['screens', 'room', 'peer', 'connection'].forEach(name => $(name + '-tab').hidden = b.dataset.tab !== name); $('savebar').hidden = b.dataset.tab !== 'screens'; if (b.dataset.tab === 'screens') fit(); if (b.dataset.tab === 'room') TecRoom.render(state, true); });
  // Quitar una pantalla del mapa (se confirma con un segundo clic) y restaurarlas.
  let removeArmed = null, armTimer;
  function hideScreen(key, saveNow) {
    const p = draft.placements.find(p => M.key(p) === key);
    if (!p || !p.remote || state.role !== 'Server' || rects().length <= 1) { toast('Solo el anfitrión puede quitar pantallas de invitados. Las pantallas propias no se quitan.'); return; }
    p.hidden = true; dirty = true; if (selected === key) selected = M.key(rects()[0]);
    buildNodes(); update(); fit();
    if (saveNow) { saving = true; update(); send({ type: 'save', map: structuredClone(draft) }); } else toast('Pantalla quitada. Guarda el mapa para aplicarlo.');
  }
  $('remove-screen').onclick = () => {
    if (removeArmed !== selected) { removeArmed = selected; clearTimeout(armTimer); armTimer = setTimeout(() => { removeArmed = null; update(); }, 3500); update(); return; }
    removeArmed = null; hideScreen(selected, false);
  };
  $('restore-screens').onclick = () => { draft.placements.forEach(p => { delete p.hidden; }); dirty = true; buildNodes(); update(); fit(); toast('Pantallas restauradas. Revisa que no se superpongan y guarda.'); };
  TecChat.init({ send, toast });
  TecRoom.init({ send, toast, key: M.key, removeScreen: key => hideScreen(key, true), viewScreen: () => send({ type: 'viewScreen' }), selectScreen: key => { selected = key; update(); } });
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
    receive({ type: 'state', state: { machineName: 'JENAV', remoteName: 'RAFAEL', peerStatus: 'connected', connected: true, mode: 'local', role: 'Server', sessionCode: 'K7M-4QX', joinCode: '', management: false, maxScreens: 10, host: '192.168.12.183', port: 51777, status: 'Conexión por red local', extras: true, pointerSpeed: 10, cursorStyle: 'green', cursorSize: 150, transfer: '', receiveFolder: 'C:\\Users\\jenav\\Downloads\\TecLink', automatic: true, theme: 'dark', peerMapSupported: true, spatialEnabled: true,
      local: [{ id: 'screen1', label: 'Pantalla 1', primary: true, left: 0, top: 0, width: 2240, height: 1400 }, { id: 'screen2', label: 'Pantalla 2 · HDMI', left: -1600, top: -2160, width: 3840, height: 2160 }], remote: [{ id: 'screen1', label: 'Pantalla 1', left: 0, top: 0, width: 1920, height: 1080 }], placements: [{ id: 'screen1', remote: false, x: 0, y: 0 }, { id: 'screen2', remote: false, x: -1600, y: -2160 }, { id: 'screen1', remote: true, x: -1920, y: 0 }] } });
  } else { window.chrome?.webview?.addEventListener('message', e => receive(e.data)); send({ type: 'ready' }); }
})();
