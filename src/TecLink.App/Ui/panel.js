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
    if (message.type === 'tile') { TecRoom.tile(message); return; }
    if (message.type === 'controlOff') { TecRoom.controlOff(); return; }
    if (message.type === 'shown') { TecRoom.shown(); return; }
    if (message.type === 'removeAsk') { askConsent(message); return; }
    if (message.type === 'myFiles' || message.type === 'peerFiles') return;
    if (message.type === 'chat') { TecChat.incoming(message.from, message.text, message.at); return; }
    if (message.type === 'voice') { TecChat.voice(message.data); return; }
    if (message.type === 'diagnostic') { $('diag-out').textContent = message.lines.join('\n'); $('diag-out').hidden = false; $('diag-run').disabled = false; $('diag-run').textContent = 'Probar conexión'; $('diag-copy').hidden = false; return; }
    if (message.type === 'toast') { toast(message.message); return; }
    if (message.type === 'error') { saving = false; dirty = true; toast(message.message); update(); return; }
    if (!message.state) return;
    state = message.state;
    // El mapa lo decide el dueno de la sala: un invitado siempre muestra el que recibe, y el dueno
    // adopta el que la app completo sola (al conectarse alguien) mientras no tenga cambios a medias.
    const saved = message.type === 'saved';
    if (!draft || saved || !state.canArrange || (!dirty && !drag && !saving)) {
      draft = { placements: state.placements?.length ? structuredClone(state.placements) : M.defaults(state), automatic: draft && !saved ? draft.automatic : state.automatic, theme: draft && !saved ? draft.theme : state.theme || 'system' };
      dirty = false; if (saved) { saving = false; if (message.message && message.message !== lastSavedToast) toast(message.message); lastSavedToast = message.message; }
    }
    {
      const screens = M.all(state), defaults = M.defaults(state);
      draft.placements = draft.placements.filter(p => screens.some(s => M.key(s) === M.key(p)));
      for (const screen of screens) if (!draft.placements.some(p => M.key(p) === M.key(screen))) { draft.placements.push(defaults.find(p => M.key(p) === M.key(screen))); if (state.canArrange && !state.connected) dirty = true; }
    }
    if (!selected || !rects().some(s => M.key(s) === selected)) selected = M.key(rects()[0] || { id: '', remote: false });
    $('automatic').checked = draft.automatic; $('theme').value = draft.theme; theme(); buildNodes(); update(); fit();
  }
  let lastSavedToast = '', autoTimer;
  // El dueno no tiene que acordarse de guardar: cada cambio valido del mapa se aplica solo en las dos PCs.
  function autoSave() { clearTimeout(autoTimer); autoTimer = setTimeout(() => { if (!state.canArrange || saving || drag || !dirty || M.overlaps(rects())) return; saving = true; update(); send({ type: 'save', map: structuredClone(draft) }); }, 450); }
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
        if (e.button !== 0) return; e.preventDefault(); e.stopPropagation(); selected = M.key(s);
        if (saving || !state.canArrange) { update(); if (!state.canArrange) toast('El orden de las pantallas lo decide el dueño de la sala. Tu mapa se ajusta solo.'); return; }
        const p = draft.placements.find(p => M.key(p) === selected);
        drag = { id: e.pointerId, node, startX: e.clientX, startY: e.clientY, x: p.x, y: p.y, original: structuredClone(draft.placements) };
        node.setPointerCapture(e.pointerId); update();
      });
      node.addEventListener('pointermove', e => {
        if (!drag || drag.node !== node) return;
        const p = draft.placements.find(p => M.key(p) === selected);
        Object.assign(p, M.snap(state, draft.placements, selected, drag.x + (e.clientX - drag.startX) / view.scale, drag.y + (e.clientY - drag.startY) / view.scale, 12 / view.scale)); update();
      });
      const end = () => { if (!drag || drag.node !== node) return; if (M.overlaps(rects())) { draft.placements = drag.original; toast('Las pantallas se superponen. Intenta unir sus bordes.'); } else { dirty = true; autoSave(); } drag = null; update(); };
      node.addEventListener('pointerup', end); node.addEventListener('pointercancel', end);
      node.addEventListener('keydown', e => { const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key]; if (d && state.canArrange) { e.preventDefault(); selected = M.key(s); move(d[0] * (e.shiftKey ? 8 : 64), d[1] * (e.shiftKey ? 8 : 64)); } });
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
    const arrange = state.canArrange !== false;
    $('sync-message').textContent = !arrange ? 'Eres invitado: el dueño de la sala ordena las pantallas y tu mapa se ajusta solo.' : state.connected ? 'Cada cambio se aplica solo en las dos PCs.' : 'Cuando alguien se una, sus pantallas se suman solas a la derecha.';
    $('screens-lead').textContent = arrange ? 'Ordena las pantallas, sus números y su posición. Donde dos pantallas se tocan (línea verde) pasa el mouse.' : 'El dueño de la sala ordena las pantallas. Donde dos pantallas se tocan (línea verde) pasa el mouse.';
    $('settings-title').textContent = s ? `${s.label || 'Pantalla'} · ${s.remote ? 'Otra PC' : 'Esta PC'}` : 'Elige una pantalla';
    $('selected-size').textContent = s ? `${s.remote ? state.remoteName || 'Otra PC' : state.machineName} · ${s.width} × ${s.height}` : 'Conecta una pantalla.';
    if (s) { if (document.activeElement !== $('position-x')) $('position-x').value = s.x; if (document.activeElement !== $('position-y')) $('position-y').value = s.y; }
    $('routes-list').replaceChildren();
    contacts.filter(c => M.key(c.a) === selected || M.key(c.b) === selected).forEach(c => { const first = M.key(c.a) === selected, target = first ? c.b : c.a, side = first ? c.side : opposite[c.side], li = document.createElement('li'); li.textContent = `${names[side]} ↔ ${names[opposite[side]]} · ${target.remote ? state.remoteName || 'Otra PC' : 'Esta PC'} / ${target.label || 'Pantalla'}`; $('routes-list').append(li); });
    if (!$('routes-list').children.length) { const li = document.createElement('li'); li.textContent = 'Sin bordes unidos.'; $('routes-list').append(li); }
    const removed = draft.placements.filter(p => p.hidden).length, admin = state.role === 'Server';
    $('restore-screens').hidden = !removed || !admin; $('restore-screens').textContent = `Restaurar pantallas quitadas (${removed})`;
    $('remove-screen').disabled = !(admin && s && s.remote && state.connected) || saving;
    $('remove-note').textContent = !admin ? 'Solo el dueño de la sala puede quitar pantallas.' : s && !s.remote ? 'Las pantallas propias no se pueden quitar: solo las de invitados.' : 'Se quita cuando la otra persona lo acepta.'; $('remove-screen').textContent = removeArmed === selected ? '¿Seguro? Pulsa otra vez' : 'Quitar esta pantalla';
    TecRoom.render(state); TecChat.render(state); renderMembers(); renderShares();
    $('savebar').hidden = !arrange || $('screens-tab').hidden;
    $('save').disabled = saving || overlaps; $('save').textContent = saving ? 'Guardando…' : dirty ? 'Guardar mapa' : 'Mapa guardado';
    $('save-heading').textContent = dirty ? 'Cambios sin guardar' : 'Mapa actual'; $('save-message').textContent = saving ? 'Aplicando en las dos PCs…' : overlaps ? 'Separa las pantallas que se superponen.' : 'Los cambios se guardan solos.';
    ['theme', 'automatic'].forEach(id => $(id).disabled = saving); ['reset-map', 'position-x', 'position-y'].forEach(id => $(id).disabled = saving || !arrange);
    document.querySelectorAll('[data-dx]').forEach(b => b.disabled = saving || !arrange);
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
    $('peer-sync').textContent = extras ? '' : 'Conecta la otra PC con esta misma versión de TecLink para compartir archivos.';
    ['pick-files', 'pick-folder'].forEach(id => $(id).disabled = !extras);
    $('auto-receive').setAttribute('aria-pressed', state.autoReceive !== false); $('auto-receive').classList.toggle('on', state.autoReceive !== false); $('auto-receive').querySelector('span').textContent = state.autoReceive !== false ? 'Recibir solo: sí' : 'Recibir solo: no';
    $('dropzone').setAttribute('aria-disabled', extras ? 'false' : 'true');
    $('transfer-status').textContent = state.transfer || 'Sin envíos todavía.';
    if (state.receiveFolder) $('receive-folder').textContent = state.receiveFolder;
    draw();
  }
  // ---- Barra de estado: siempre visible, con un boton grande que cambia segun lo que pasa ----
  function updateStatusBar() {
    const on = state.powered !== false, host = state.role === 'Server', name = state.remoteName || 'la otra PC';
    let dot = 'off', title = '', sub = '', main = null, alt = null;
    const idle = state.netMode === 'idle';
    if (!on) { title = 'TecLink está apagado'; sub = 'No comparte mouse ni teclado.'; main = { label: 'Encender', act: () => send({ type: 'setPower', on: true }) }; }
    else if (idle) { dot = 'off'; title = 'Sin conexión'; sub = 'TecLink no está conectado a nadie. Tú decides cuándo conectar.'; main = { label: 'Invitar a alguien', act: () => { send({ type: 'session', action: 'open' }); document.querySelector('[data-tab=connection]').click(); } }; alt = { label: 'Conectar con un código', act: () => { document.querySelector('[data-tab=connection]').click(); setTimeout(() => $('join-code').focus(), 50); } }; }
    else if (!state.connected) {
      const gone = state.peerStatus === 'left' ? `${name} salió de la sesión` : state.peerStatus === 'lost' ? `${name} se desconectó` : '';
      if (!host && state.joinCode && !gone) { dot = 'warn'; title = 'Buscando la sesión…'; sub = state.joinNotice || 'La otra PC debe tener TecLink abierto en esta misma red.'; main = { label: 'Cancelar', act: () => send({ type: 'session', action: 'stop' }) }; }
      else { dot = gone ? 'bad' : 'ok'; title = gone || 'Sesión abierta: esperando a la otra PC'; sub = `Tu código: ${state.sessionCode || '—'}. Escríbelo en la otra PC.`; main = { label: 'Cerrar mi sesión', act: () => send({ type: 'session', action: 'stop' }) }; }
    } else if (state.mode === 'remote') { dot = 'live'; title = `Estás controlando la PC de ${name}`; sub = 'Tu mouse y tu teclado están en la otra PC.'; main = { label: 'Volver a mi PC', act: () => send({ type: 'toggleControl' }) }; }
    else if (state.mode === 'peer') { dot = 'live'; title = `${name} está controlando esta PC`; sub = 'Tu mouse sigue funcionando: muévelo cuando quieras o recupera el control.'; main = { label: 'Recuperar mi mouse', act: () => send({ type: 'freeInput' }) }; }
    else if (state.sessionMode && !state.management) { dot = 'warn'; title = `Conectado con ${name}: el mouse NO pasa todavía`; sub = host ? 'Activa la gestión de pantallas para mover el mouse entre las dos PCs.' : 'El anfitrión debe activar la gestión de pantallas.'; main = host ? { label: 'Activar gestión', act: () => send({ type: 'setManagement', on: true }) } : null; }
    else { dot = 'ok'; title = `Conectado con ${name}`; sub = 'Lleva el mouse al borde donde las pantallas se tocan, o pulsa el botón, para controlar la otra PC.'; main = { label: `Controlar la PC de ${name}`, act: () => send({ type: 'toggleControl' }) }; }
    $('sb-dot').className = 'sb-dot ' + dot; $('sb-title').textContent = title; $('sb-sub').textContent = sub;
    const b = $('sb-main'); b.hidden = !main; if (main) { b.textContent = main.label; b.onclick = main.act; }
    if (!alt && !idle && on) alt = { label: host ? 'Cerrar mi sesión' : 'Salir de la sesión', act: () => send({ type: 'session', action: 'stop' }) };
    if (alt && main && (alt.label === main.label || main.label === 'Cancelar')) alt = null;
    const a = $('sb-alt'); a.hidden = !alt; if (alt) { a.textContent = alt.label; a.onclick = alt.act; }
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
  // ---- Miembros de la sala (Pantallas) y participantes (Archivos) ----
  const size = n => n >= 1 << 30 ? (n / (1 << 30)).toFixed(1) + ' GB' : n >= 1 << 20 ? (n / (1 << 20)).toFixed(1) + ' MB' : n >= 1024 ? Math.round(n / 1024) + ' KB' : n + ' B';
  function people() {
    const host = state.role === 'Server', inSession = state.netMode !== 'idle', shown = (state.remote || []).length > 0;
    const list = [{ who: 'me', name: state.machineName, tag: 'Tú', role: !inSession ? 'Sin sesión' : host ? 'Dueño de la sala' : 'Invitado', screens: state.local || [], on: true,
      status: state.mode === 'remote' ? 'Controlando la otra PC' : state.mode === 'peer' ? 'La otra PC maneja esta' : 'Mouse en esta PC' }];
    if (state.connected || shown) list.push({ who: 'peer', name: state.remoteName || 'Otra PC', tag: '', role: host ? 'Invitado' : 'Dueño de la sala', screens: state.remote || [], on: !!state.connected,
      status: state.connected ? 'Conectada' : state.peerStatus === 'left' ? 'Salió de la sesión' : 'Desconectada' });
    return list;
  }
  function memberCard(m) {
    const li = document.createElement('li'), av = document.createElement('i'), box = document.createElement('div'), name = document.createElement('strong'), role = document.createElement('small'), data = document.createElement('small'), st = document.createElement('small');
    li.className = 'member' + (m.on ? '' : ' offline'); av.className = 'avatar'; av.textContent = (m.name || '?').slice(0, 2).toUpperCase(); av.setAttribute('aria-hidden', 'true');
    name.textContent = m.name + (m.tag ? ` (${m.tag})` : ''); role.className = 'role'; role.textContent = m.role;
    data.textContent = `${m.screens.length} ${m.screens.length === 1 ? 'pantalla' : 'pantallas'}${m.screens.length ? ' · ' + m.screens.map(d => `${d.width}×${d.height}`).join(', ') : ''}`;
    st.className = 'state'; st.textContent = m.status; box.append(name, role, data, st); li.append(av, box); return li;
  }
  let membersSig = '', shareFilter = null, sharesSig = '';
  function renderMembers() {
    const list = people(), sig = JSON.stringify(list) + '|' + shareFilter + '|' + (state.shares || []).length;
    if (sig === membersSig) return; membersSig = sig;
    $('members').replaceChildren(...list.map(memberCard)); $('members-count').textContent = list.length;
    $('file-people').replaceChildren(...list.map(m => {
      const li = memberCard(m), count = (state.shares || []).filter(f => f.mine === (m.who === 'me')).length, b = document.createElement('button');
      b.className = 'secondary'; b.setAttribute('aria-pressed', shareFilter === m.who); b.textContent = shareFilter === m.who ? 'Ver todos los archivos' : `Ver archivos que compartió (${count})`;
      b.onclick = () => { shareFilter = shareFilter === m.who ? null : m.who; update(); };
      li.querySelector('div').append(b); return li;
    }));
  }
  // ---- Archivos: historial unico de la sesion, con los mismos botones para todos ----
  function renderShares() {
    const all = state.shares || [], items = all.filter(f => !shareFilter || f.mine === (shareFilter === 'me')), sig = JSON.stringify(items) + '|' + shareFilter + '|' + state.connected;
    if (sig === sharesSig) return; sharesSig = sig;
    const who = shareFilter === 'me' ? state.machineName : state.remoteName || 'la otra PC';
    $('history-filter').hidden = !shareFilter; $('history-filter').textContent = shareFilter ? `Solo lo que compartió ${who}.` : '';
    const list = $('share-list'); list.replaceChildren();
    if (!items.length) { const li = document.createElement('li'); li.className = 'empty'; li.textContent = shareFilter ? `${who} todavía no compartió nada en esta sesión.` : 'Todavía no se compartió nada en esta sesión. Lo que envíe cualquiera aparecerá aquí.'; list.append(li); return; }
    for (const f of items) {
      const li = document.createElement('li'), info = document.createElement('div'), name = document.createElement('strong'), meta = document.createElement('small'), chip = document.createElement('small'), actions = document.createElement('div');
      info.className = 'fi-name'; actions.className = 'fi-actions'; name.textContent = f.name; name.title = f.name;
      meta.textContent = `${new Date(f.at * 1000).toLocaleTimeString('es', { hour: '2-digit', minute: '2-digit' })} · ${f.mine ? 'Tú' : f.sender} · ${f.isDirectory ? 'Carpeta' : size(f.size)}`;
      chip.className = 'chip' + (f.have ? ' ok' : ''); chip.textContent = f.mine ? 'Compartido por ti' : f.have ? 'En tu carpeta' : f.fetching ? 'Trayendo…' : 'Disponible';
      info.append(name, meta, chip);
      const add = (label, aria, action) => { const b = document.createElement('button'); b.className = 'secondary'; b.textContent = label; b.setAttribute('aria-label', `${aria}: ${f.name}`); b.onclick = () => send({ type: 'shareAction', id: f.id, action }); actions.append(b); };
      add('Ver', 'Abrir', 'view'); add('A mi escritorio', 'Copiar a mi escritorio', 'desktop');
      li.append(info, actions); list.append(li);
    }
  }
  $('auto-receive').onclick = () => { const on = $('auto-receive').getAttribute('aria-pressed') !== 'true'; send({ type: 'autoReceive', on }); toast(on ? 'Lo que compartan se copiará solo a tu carpeta.' : 'Los archivos quedan en la lista: tráelos con «Ver» o «A mi escritorio».'); };
  // ---- Quitar una pantalla necesita el permiso de su dueno ----
  let consentId = null;
  function askConsent(m) { consentId = m.id; $('consent-text').textContent = `${m.from || 'El dueño de la sala'} quiere quitar tu ${m.label || 'pantalla'} de la sala. Si aceptas, el mouse ya no pasará a esa pantalla y dejará de verse en la sala.`; $('consent').hidden = $('consent-scrim').hidden = false; $('consent-no').focus(); }
  function answerConsent(accept) { if (consentId === null) return; send({ type: 'removeAnswer', id: consentId, accept }); consentId = null; $('consent').hidden = $('consent-scrim').hidden = true; toast(accept ? 'Aceptaste: tu pantalla sale de la sala.' : 'No aceptaste: tu pantalla sigue en la sala.'); }
  $('consent-yes').onclick = () => answerConsent(true); $('consent-no').onclick = () => answerConsent(false);
  // ---- Conectar: sesión con código, interruptor de gestión y cuadrícula ----
  function updateSession() {
    const host = state.role === 'Server', legacy = !host && !state.joinCode, on = state.powered !== false, max = state.maxScreens || 10;
    $('session-code').textContent = state.sessionCode || '——';
    $('host-card').classList.toggle('inactive', !host);
    const guest = !host && !legacy;
    const active = state.netMode === 'host', idleNet = state.netMode === 'idle';
    $('open-session').hidden = !(host && idleNet); $('close-session').hidden = !(host && active); $('copy-code').className = (host && active) ? 'primary' : 'secondary';
    $('host-status').textContent = idleNet && host ? 'Tu sesión está cerrada: nadie puede conectarse a esta PC. Ábrela cuando quieras invitar a alguien.' : legacy ? 'Ahora usas un enlace directo por IP. Pulsa «Abrir mi propia sesión» para invitar con código.' : guest ? 'Estás unido a otra sesión. Sal de ella para invitar a alguien.' : state.connected ? `${state.remoteName || 'Otra PC'} está conectada a tu sesión.` : 'Esperando a que alguien se una con este código.';
    $('host-card').classList.toggle('inactive', !host);
    const addrs = state.addresses || [];
    $('host-address').textContent = addrs.length && active ? `Dirección de esta PC (por si la otra no la encuentra sola): ${addrs.join(' · ')}` : '';
    $('firewall-box').hidden = state.firewall !== false || !(host && active);
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
  $('repair-run').onclick = () => { $('diag-out').hidden = false; $('diag-out').textContent = 'Reparando: si Windows pide permiso, pulsa Sí…'; send({ type: 'repair' }); };
  $('diag-run').onclick = () => { $('diag-run').disabled = true; $('diag-run').textContent = 'Probando…'; $('diag-out').hidden = false; $('diag-out').textContent = 'Revisando la red…'; send({ type: 'diagnose' }); };
  $('diag-copy').onclick = async () => { try { await navigator.clipboard.writeText($('diag-out').textContent); toast('Informe copiado. Pégalo donde te lo pidan.'); } catch { toast('Selecciona el texto del informe y cópialo.'); } };
  $('firewall-btn').onclick = () => { send({ type: 'session', action: 'firewall' }); toast('Windows te pedirá permiso: pulsa Sí.'); };
  $('leave-btn').onclick = () => send({ type: 'session', action: 'stop' });
  $('open-session').onclick = () => send({ type: 'session', action: 'open' });
  $('close-session').onclick = () => send({ type: 'session', action: 'stop' });
  let pointerBusy = false, pointerTimer, chosenStyle = 'system';
  function sendPointer() {
    pointerBusy = true; clearTimeout(pointerTimer);
    $('speed-label').textContent = `${$('speed').value} de 20`; $('size-label').textContent = `${$('cursor-size').value} %`;
    pointerTimer = setTimeout(() => { send({ type: 'pointer', speed: +$('speed').value, style: chosenStyle, size: +$('cursor-size').value }); setTimeout(() => pointerBusy = false, 400); }, 160);
  }
  $('speed').oninput = sendPointer; $('cursor-size').oninput = sendPointer;
  document.querySelectorAll('.swatch').forEach(b => b.onclick = () => { chosenStyle = b.dataset.style; document.querySelectorAll('.swatch').forEach(o => o.setAttribute('aria-checked', o === b ? 'true' : 'false')); $('cursor-size').disabled = chosenStyle === 'system'; sendPointer(); });
  $('color-mouse').oninput = () => { chosenStyle = $('color-mouse').value; document.querySelectorAll('.swatch').forEach(o => o.setAttribute('aria-checked', 'false')); $('cursor-size').disabled = false; sendPointer(); };
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
  function move(dx, dy) { if (saving || !state.canArrange) return; const p = draft.placements.find(p => M.key(p) === selected); if (!p) return; p.x += dx; p.y += dy; dirty = true; update(); autoSave(); }
  document.querySelectorAll('[data-dx]').forEach(b => b.onclick = () => move(+b.dataset.dx, +b.dataset.dy));
  ['x', 'y'].forEach(axis => $('position-' + axis).onchange = e => { const p = draft.placements.find(p => M.key(p) === selected), n = Number(e.target.value); if (p && Number.isInteger(n) && Math.abs(n) <= 1000000) { p[axis] = n; dirty = true; update(); autoSave(); } });
  $('automatic').onchange = e => { draft.automatic = e.target.checked; dirty = true; update(); };
  $('theme').onchange = e => { draft.theme = e.target.value; dirty = true; theme(); applyColors(); update(); };
  $('save').onclick = () => { if (saving || M.overlaps(rects())) return; saving = true; update(); send({ type: 'save', map: structuredClone(draft) }); };
  $('toggle-control').onclick = () => send({ type: 'toggleControl' });
  $('power').onclick = () => send({ type: 'setPower', on: $('power').getAttribute('aria-pressed') !== 'true' });
  $('fit-map').onclick = fit;
  function zoom(factor) { const board = $('spatial-map'), old = view.scale, next = Math.max(.015, Math.min(.5, old * factor)), cx = board.clientWidth / 2, cy = board.clientHeight / 2; view.x = cx - (cx - view.x) * next / old; view.y = cy - (cy - view.y) * next / old; view.scale = next; draw(); }
  $('zoom-in').onclick = () => zoom(1.25); $('zoom-out').onclick = () => zoom(.8);
  $('reset-map').onclick = () => { const hidden = draft.placements.filter(p => p.hidden); draft.placements = M.defaults(state).map(p => hidden.some(h => M.key(h) === M.key(p)) ? { ...p, hidden: true } : p); dirty = true; update(); fit(); autoSave(); };
  document.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => { document.querySelectorAll('[data-tab]').forEach(t => { t.classList.toggle('active', t === b); t.setAttribute('aria-selected', t === b ? 'true' : 'false'); }); ['screens', 'room', 'peer', 'connection'].forEach(name => $(name + '-tab').hidden = b.dataset.tab !== name); TecRoom.setActive(b.dataset.tab === 'room'); if (state) update(); if (b.dataset.tab === 'screens') fit(); });
  // Quitar una pantalla de un invitado: se confirma con un segundo clic y despues la otra persona debe aceptarlo.
  let removeArmed = null, armTimer;
  $('remove-screen').onclick = () => {
    const s = selectedRect(); if (!s || !s.remote || state.role !== 'Server') { toast('Solo el dueño de la sala puede quitar pantallas de invitados. Las pantallas propias no se quitan.'); return; }
    if (removeArmed !== selected) { removeArmed = selected; clearTimeout(armTimer); armTimer = setTimeout(() => { removeArmed = null; update(); }, 3500); update(); return; }
    removeArmed = null; send({ type: 'removeAsk', id: s.id }); update();
  };
  $('restore-screens').onclick = () => { draft.placements.forEach(p => { delete p.hidden; }); if (M.overlaps(rects())) draft.placements = M.defaults(state); dirty = true; buildNodes(); update(); fit(); autoSave(); toast('Pantallas restauradas.'); };
  const openTab = name => document.querySelector(`[data-tab=${name}]`).click();
  TecChat.init({ send, toast, onOpen: open => { TecRoom.setDocked(open); if (open) openTab('room'); else if (state) update(); } });
  TecRoom.init({ send, toast, key: M.key, changed: () => { if (state) update(); } });
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
    receive({ type: 'state', state: { machineName: 'JENAV', remoteName: 'RAFAEL', peerStatus: 'connected', netMode: 'host', connected: true, mode: 'local', role: 'Server', sessionCode: 'K7M-4QX', joinCode: '', management: true, sessionMode: true, maxScreens: 10, host: '192.168.12.183', port: 51777, status: 'Conexión por red local', extras: true, canArrange: true, autoReceive: true, shares: [{ id: 'a1', at: 1763100000, name: 'Propuesta TechnoCrazy.pdf', size: 482133, isDirectory: false, mine: true, sender: 'JENAV', have: true, fetching: false }, { id: 'b2', at: 1763100300, name: 'Fotos del evento', size: 0, isDirectory: true, mine: false, sender: 'RAFAEL', have: false, fetching: false }], pointerSpeed: 10, cursorStyle: 'green', cursorSize: 150, transfer: '', receiveFolder: 'C:\\Users\\jenav\\Downloads\\TecLink', automatic: true, theme: 'dark', peerMapSupported: true, spatialEnabled: true,
      local: [{ id: 'screen1', label: 'Pantalla 1', primary: true, left: 0, top: 0, width: 2240, height: 1400 }, { id: 'screen2', label: 'Pantalla 2 · HDMI', left: -1600, top: -2160, width: 3840, height: 2160 }], remote: [{ id: 'screen1', label: 'Pantalla 1', left: 0, top: 0, width: 1920, height: 1080 }], placements: [{ id: 'screen1', remote: false, x: 0, y: 0 }, { id: 'screen2', remote: false, x: -1600, y: -2160 }, { id: 'screen1', remote: true, x: -1920, y: 0 }] } });
  } else { window.chrome?.webview?.addEventListener('message', e => receive(e.data)); send({ type: 'ready' }); }
})();
