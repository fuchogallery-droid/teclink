(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const edges = ['left', 'right', 'top', 'bottom'];
  const names = { left: 'Izquierda', right: 'Derecha', top: 'Arriba', bottom: 'Abajo' };
  const arrows = { left: '←', right: '→', top: '↑', bottom: '↓' };
  const opposite = { left: 'right', right: 'left', top: 'bottom', bottom: 'top' };
  const monitorSvg = '<svg viewBox="0 0 48 36" aria-hidden="true"><rect x="2" y="2" width="44" height="26" rx="3"/><path d="M16 34h16m-8-6v6"/></svg>';
  const escape = value => String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
  let state, draft, selected, dirty = false, saving = false, dragged, toastTimer;

  function applyTheme(theme) {
    const next = theme === 'system' ? (matchMedia('(prefers-color-scheme:dark)').matches ? 'dark' : 'light') : theme;
    if (document.body.dataset.theme === next) return;
    document.body.classList.add('theme-switch');
    document.body.dataset.theme = next;
    void document.body.offsetHeight;
    requestAnimationFrame(() => requestAnimationFrame(() => document.body.classList.remove('theme-switch')));
  }
  function markDirty() {
    dirty = true;
    $('save-heading').textContent = 'Cambios pendientes';
    $('save-message').textContent = 'Guarda el mapa para activar las nuevas salidas.';
    $('save').innerHTML = 'Guardar mapa <span aria-hidden="true">↗</span>';
  }
  function toast(message) {
    clearTimeout(toastTimer);
    $('toast').textContent = message;
    $('toast').classList.add('visible');
    toastTimer = setTimeout(() => $('toast').classList.remove('visible'), 4500);
  }
  function route() { return draft.routes.find(r => r.sourceId === selected); }
  function current() { return state.local.find(s => s.id === selected); }
  function targetOf(r) {
    return state.remote.find(s => s.id === r.targetId) || { id: '', label: 'Escritorio de la otra PC', availableEdges: edges };
  }
  function receive(message) {
    if (message.type === 'error') {
      saving = false;
      renderAll();
      markDirty();
      $('save-message').textContent = 'No se guardó el mapa. Revisa el aviso.';
      toast(message.message);
      return;
    }
    if (!message.state) return;
    const incoming = message.state;
    const changedScreens = state && JSON.stringify([state.local.map(s => [s.id, s.width, s.height]), state.remote]) !== JSON.stringify([incoming.local.map(s => [s.id, s.width, s.height]), incoming.remote]);
    state = incoming;
    if (!draft || message.type === 'saved') {
      draft = { routes: state.local.map(s => structuredClone(s.route)), order: state.local.map(s => s.id), automatic: state.automatic, theme: state.theme };
      selected = state.local.some(s => s.id === selected) ? selected : state.local[0]?.id;
      dirty = false; saving = false;
      renderAll();
      if (message.type === 'saved') {
        $('save-heading').textContent = 'Mapa guardado';
        $('save-message').textContent = 'Las salidas elegidas ya están activas.';
        $('save').innerHTML = 'Mapa guardado <span aria-hidden="true">✓</span>';
        toast('Mapa guardado. Tienes el mando local.');
      }
    } else if (changedScreens) {
      draft.order = draft.order.filter(id => state.local.some(s => s.id === id));
      draft.routes = draft.routes.filter(r => state.local.some(s => s.id === r.sourceId));
      state.local.forEach(s => {
        if (!draft.order.includes(s.id)) draft.order.push(s.id);
        if (!draft.routes.some(r => r.sourceId === s.id)) draft.routes.push(structuredClone(s.route));
      });
      if (!state.local.some(s => s.id === selected)) selected = state.local[0]?.id;
      renderAll();
    }
    renderStatus();
  }
  function send(message) {
    if (window.chrome?.webview) window.chrome.webview.postMessage(message);
    else if (new URLSearchParams(location.search).get('preview') === '1') {
      window.__tecMessages.push(structuredClone(message));
      if (message.type === 'save') setTimeout(() => {
        const next = structuredClone(state);
        next.local = message.map.order.map(id => next.local.find(s => s.id === id));
        next.local.forEach(s => s.route = message.map.routes.find(r => r.sourceId === s.id));
        next.automatic = message.map.automatic; next.theme = message.map.theme;
        receive({ type: 'saved', state: next });
      }, 180);
    }
  }
  function renderStatus() {
    $('connection').classList.toggle('online', state.connected);
    $('connection').innerHTML = '<i></i>' + (state.connected ? 'Conectado' : 'Sin conexión');
    $('mode').textContent = state.mode === 'remote' ? 'Controlando la otra PC' : state.mode === 'peer' ? 'La otra PC tiene el mando' : 'Control local';
    $('toggle-control').disabled = !state.connected;
    $('local-name').textContent = state.machineName;
    $('remote-name').textContent = state.connected ? state.remoteName : 'Esperando conexión';
    $('connection-title').textContent = state.connected ? 'Tus equipos están conectados' : 'Esperando a la otra PC';
    $('detail-name').textContent = state.machineName;
    $('detail-remote').textContent = state.connected ? state.remoteName : 'Sin conectar';
    $('detail-role').textContent = state.role === 'Client' ? 'Cliente' : 'Servidor';
    $('detail-host').textContent = state.host || 'Esta PC espera la conexión';
    $('detail-port').textContent = state.port;
    $('detail-status').textContent = state.status;
  }
  function renderMap() {
    const container = $('local-screens');
    container.replaceChildren();
    draft.order.forEach(id => {
      const screen = state.local.find(s => s.id === id);
      if (!screen) return;
      const r = draft.routes.find(item => item.sourceId === id);
      const card = document.createElement('article');
      card.className = 'screen-card' + (selected === id ? ' selected' : '');
      card.draggable = true; card.dataset.id = id;
      card.setAttribute('aria-label', screen.label + ', arrastra para ordenar');
      card.innerHTML = (selected === id ? '<span class="screen-selected">Seleccionada</span>' : '') +
        '<button class="screen-select" aria-pressed="' + (selected === id) + '">' + monitorSvg + '<strong>' + escape(screen.label) + '</strong><small>' + screen.width + ' × ' + screen.height + (screen.primary ? ' · Principal' : '') + '</small></button>' +
        edges.map(side => '<button class="port ' + side + (r.exit === side ? ' on' : '') + '" data-side="' + side + '" aria-label="Salida ' + names[side].toLowerCase() + ' de ' + escape(screen.label) + '" aria-pressed="' + (r.exit === side) + '" ' + (!screen.availableEdges.includes(side) ? 'disabled title="Este borde une pantallas locales"' : '') + '>' + arrows[side] + '</button>').join('') +
        '<p class="screen-label">' + (r.exit ? 'Salida: <b>' + names[r.exit].toLowerCase() + '</b>' : 'Sin salida automática') + '</p>';
      card.querySelector('.screen-select').onclick = () => { selected = id; renderMap(); renderPanel(); renderSummary(); };
      card.querySelectorAll('.port').forEach(port => port.onclick = () => {
        selected = id; const r = route(); r.exit = r.exit === port.dataset.side ? null : port.dataset.side;
        chooseEntry(); markDirty(); renderMap(); renderPanel(); renderSummary();
      });
      card.addEventListener('dragstart', event => { dragged = id; event.dataTransfer.setData('text/plain', id); event.dataTransfer.effectAllowed = 'move'; });
      card.addEventListener('dragover', event => { event.preventDefault(); card.classList.add('drag-over'); });
      card.addEventListener('dragleave', () => card.classList.remove('drag-over'));
      card.addEventListener('drop', event => {
        event.preventDefault(); const from = draft.order.indexOf(dragged || event.dataTransfer.getData('text/plain')); const to = draft.order.indexOf(id);
        if (from >= 0 && to >= 0 && from !== to) { const [item] = draft.order.splice(from, 1); draft.order.splice(to, 0, item); markDirty(); }
        dragged = null; renderMap(); renderPanel();
      });
      container.append(card);
    });
    const selectedRoute = route();
    $('remote-screens').innerHTML = state.remote.length ? state.remote.map(screen =>
      '<article class="screen-card remote-card' + (selectedRoute?.targetId === screen.id ? ' selected' : '') + '">' +
      (selectedRoute?.targetId === screen.id ? '<span class="screen-selected">Destino elegido</span>' : '') +
      '<div class="screen-select">' + monitorSvg + '<strong>' + escape(screen.label) + '</strong><small>' + screen.width + ' × ' + screen.height + '</small></div>' +
      '<p class="screen-label">' + (selectedRoute?.targetId === screen.id && selectedRoute.exit ? 'Entrada: <b>' + names[selectedRoute.entry].toLowerCase() + '</b>' : 'Disponible como destino') + '</p></article>').join('') :
      '<div class="empty"><strong>Conecta la otra PC</strong>Sus pantallas aparecerán aquí. Puedes preparar las salidas de esta PC mientras tanto.</div>';
  }
  function options(select, items, value) {
    select.replaceChildren(...items.map(item => {
      const option = new Option(item.text, item.value); option.disabled = !!item.disabled; return option;
    }));
    select.value = value ?? '';
  }
  function renderPanel() {
    const screen = current(), r = route(); if (!screen || !r) return;
    $('settings-title').textContent = screen.label;
    $('selected-size').textContent = screen.width + ' × ' + screen.height + (screen.primary ? ' · Pantalla principal' : ' · Pantalla adicional');
    options($('exit'), [{ value: '', text: 'Desactivada' }, ...edges.map(side => ({ value: side, text: names[side], disabled: !screen.availableEdges.includes(side) }))], r.exit);
    const targets = [{ value: '', text: 'Escritorio de la otra PC' }, ...state.remote.map(s => ({ value: s.id, text: s.label + ' · ' + s.width + ' × ' + s.height }))];
    if (r.targetId && !state.remote.some(s => s.id === r.targetId)) targets.push({ value: r.targetId, text: 'Destino guardado · al conectar' });
    options($('target'), targets, r.targetId);
    const available = targetOf(r).availableEdges;
    options($('entry'), edges.map(side => ({ value: side, text: names[side], disabled: !available.includes(side) })), r.entry);
    $('target').disabled = !r.exit || !state.remote.length;
    $('entry').disabled = !r.exit;
    const index = draft.order.indexOf(selected);
    $('move-before').disabled = index <= 0;
    $('move-after').disabled = index === draft.order.length - 1;
    $('automatic').checked = draft.automatic;
    $('automatic').disabled = saving;
    $('exit').disabled = saving;
    $('theme').disabled = saving;
    $('theme').value = draft.theme;
  }
  function renderSummary() {
    const r = route(), screen = current(); if (!r || !screen) return;
    $('route-title').textContent = r.exit ? screen.label + ' → ' + targetOf(r).label : screen.label + ' sin salida automática';
    $('route-description').textContent = r.exit ? 'Sale por ' + names[r.exit].toLowerCase() + ' y entra por ' + names[r.entry].toLowerCase() + '.' : 'El atajo seguirá disponible para cambiar de mando.';
  }
  function renderAll() {
    applyTheme(draft.theme); renderMap(); renderPanel(); renderSummary(); renderStatus();
    $('save').disabled = saving; $('save').classList.toggle('saving', saving);
  }
  function chooseEntry() {
    const r = route(); if (!r?.exit) return;
    const preferred = opposite[r.exit], available = targetOf(r).availableEdges;
    r.entry = available.includes(preferred) ? preferred : available[0] || preferred;
  }
  $('exit').onchange = () => { route().exit = $('exit').value || null; chooseEntry(); markDirty(); renderMap(); renderPanel(); renderSummary(); };
  $('target').onchange = () => { route().targetId = $('target').value || null; chooseEntry(); markDirty(); renderMap(); renderPanel(); renderSummary(); };
  $('entry').onchange = () => { route().entry = $('entry').value; markDirty(); renderMap(); renderSummary(); };
  $('automatic').onchange = () => { draft.automatic = $('automatic').checked; markDirty(); };
  $('theme').onchange = () => { draft.theme = $('theme').value; applyTheme(draft.theme); markDirty(); };
  matchMedia('(prefers-color-scheme:dark)').addEventListener('change', () => { if (draft?.theme === 'system') applyTheme('system'); });
  for (const [id, delta] of [['move-before', -1], ['move-after', 1]]) $(id).onclick = () => {
    const index = draft.order.indexOf(selected), target = index + delta;
    if (target < 0 || target >= draft.order.length) return;
    [draft.order[index], draft.order[target]] = [draft.order[target], draft.order[index]];
    markDirty(); renderMap(); renderPanel();
  };
  $('save').onclick = () => {
    if (!draft || saving) return;
    saving = true; $('save').disabled = true; $('save').classList.add('saving');
    $('save').innerHTML = 'Guardando… <span aria-hidden="true"></span>';
    ['exit', 'target', 'entry', 'automatic', 'theme', 'move-before', 'move-after'].forEach(id => $(id).disabled = true);
    document.querySelectorAll('.screen-card button').forEach(button => button.disabled = true);
    send({ type: 'save', map: structuredClone(draft) });
  };
  $('toggle-control').onclick = () => send({ type: 'toggleControl' });
  document.querySelectorAll('.tab').forEach(tab => tab.onclick = () => {
    document.querySelectorAll('.tab').forEach(item => { item.classList.toggle('active', item === tab); item.setAttribute('aria-selected', String(item === tab)); });
    $('screens-tab').hidden = tab.dataset.tab !== 'screens'; $('connection-tab').hidden = tab.dataset.tab !== 'connection';
  });
  if (window.chrome?.webview) { window.chrome.webview.addEventListener('message', event => receive(event.data)); send({ type: 'ready' }); }
  else if (new URLSearchParams(location.search).get('preview') === '1') {
    window.__tecMessages = [];
    receive({ type: 'state', state: { machineName: 'jenav', remoteName: 'rafael', connected: true, mode: 'local', status: 'Conectado con rafael', automatic: true, theme: 'dark', role: 'Client', host: '192.168.12.183', port: 51777,
      local: [{ id: 'screen1', label: 'Pantalla 1', width: 2240, height: 1400, primary: true, availableEdges: edges, route: { sourceId: 'screen1', exit: 'left', targetId: 'remote1', entry: 'right' } }, { id: 'screen2', label: 'Pantalla 2', width: 3840, height: 2160, primary: false, availableEdges: edges, route: { sourceId: 'screen2', exit: null, targetId: 'remote1', entry: 'right' } }],
      remote: [{ id: 'remote1', label: 'Pantalla 1', width: 1920, height: 1080, availableEdges: edges }] } });
  }
  window.__tecTest = { receive, draft: () => structuredClone(draft) };
})();
