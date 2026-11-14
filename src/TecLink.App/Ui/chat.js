// Chat acoplado: al abrirlo la ventana se divide 70-30. A la izquierda quedan las pantallas de la sala y a la
// derecha este panel, con las camaras de cada persona, los botones de la sala (microfono, camara, control
// del mouse, enviar archivos) y los mensajes. El microfono solo se abre cuando el usuario lo enciende.
(() => {
  const RATE = 16000;
  let api = {}, state, open = false, unread = 0, topPct = Number(localStorage.getItem('tecChatTop')) || 55, lastFrom = null;
  const mic = { on: false, ptt: false, holding: false, stream: null, ctx: null, node: null, source: null, device: localStorage.getItem('tecMicDevice') || '', level: 0 };
  const spk = { ctx: null, gain: null, next: 0, volume: Number(localStorage.getItem('tecVoiceVolume') || 1), last: 0 };
  const el = {};
  const icon = d => `<svg viewBox="0 0 24 24" aria-hidden="true">${d}</svg>`;

  function build() {
    const bubble = document.createElement('button'); bubble.id = 'chat-bubble'; bubble.type = 'button'; bubble.setAttribute('aria-label', 'Abrir el chat de la sala'); bubble.setAttribute('aria-expanded', 'false');
    bubble.innerHTML = icon('<path d="M4 5h16a1 1 0 011 1v10a1 1 0 01-1 1h-9l-5 4v-4H4a1 1 0 01-1-1V6a1 1 0 011-1z"/>') + '<span id="chat-badge" hidden>0</span>';
    document.body.append(bubble);
    const panel = document.createElement('aside'); panel.id = 'chat-panel'; panel.hidden = true; panel.setAttribute('aria-labelledby', 'chat-title');
    panel.innerHTML = `
      <header class="chat-head"><div><h2 id="chat-title">Sala en vivo</h2><small id="chat-people"></small></div><button id="chat-files-btn" class="secondary" type="button" aria-label="Archivos compartidos" title="Archivos compartidos">${icon('<path d="M4 6a2 2 0 012-2h4l2 2h6a2 2 0 012 2v9a2 2 0 01-2 2H6a2 2 0 01-2-2z"/>')}</button><button id="chat-skin-btn" class="secondary" type="button" aria-label="Diseños y colores" title="Diseños y colores">${icon('<circle cx="12" cy="12" r="9"/><circle cx="8.5" cy="10" r="1.2"/><circle cx="12" cy="7.5" r="1.2"/><circle cx="15.5" cy="10" r="1.2"/><path d="M12 21a2 2 0 010-4h1.5a2.5 2.5 0 000-5"/>')}</button><button id="chat-gear-btn" class="secondary" type="button" aria-label="Configuración" title="Configuración">${icon('<circle cx="12" cy="12" r="3"/><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1"/>')}</button><button id="chat-close" class="secondary" type="button" aria-label="Cerrar el chat">✕</button></header>
      <div id="chat-cams" class="chat-cams" aria-label="Cámaras de la sala"></div>
      <div class="chat-tools" role="toolbar" aria-label="Controles de la sala">
        <button id="tool-mic" class="tool" type="button" data-mic-switch aria-pressed="false">${icon('<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0014 0M12 18v3"/>')}<span>Micrófono</span></button>
        <button id="tool-cam" class="tool" type="button" data-cam-switch aria-pressed="false">${icon('<rect x="3" y="6" width="13" height="12" rx="2"/><path d="M16 10l5-3v10l-5-3"/>')}<span>Cámara</span></button>
        <button id="tool-mouse" class="tool" type="button">${icon('<path d="M6 3v15l4-3.5 3 6.5 2.5-1.2-3-6.3H18z"/>')}<span>Controlar</span></button>
        <button id="tool-files" class="tool" type="button">${icon('<path d="M12 16V4m0 0-4 4m4-4 4 4M4 15v3a2 2 0 002 2h12a2 2 0 002-2v-3"/>')}<span>Enviar archivos</span></button>
      </div>
      <details id="chat-files" class="chat-files"><summary><span>Archivos compartidos</span><b id="chat-files-count">0</b></summary><ul id="chat-share-list" class="chat-share-list"></ul></details>
      <ul id="chat-log" class="chat-log" aria-live="polite"></ul>
      <form id="chat-form" class="chat-form"><label class="sr" for="chat-input">Mensaje</label><input id="chat-input" type="text" autocomplete="off" maxlength="2000" placeholder="Escribe un mensaje…"><button id="chat-send" class="primary" type="submit">Enviar</button></form>
      <details class="chat-voice"><summary>Ajustes de voz</summary>
        <div class="voice-row"><button id="mic-toggle" class="ctl-switch" role="switch" aria-checked="false" type="button"><span class="track" aria-hidden="true"><span class="thumb"></span></span><span class="sw-label">Micrófono: apagado</span></button><div id="mic-meter" class="meter" role="meter" aria-label="Nivel del micrófono" aria-valuemin="0" aria-valuemax="100" aria-valuenow="0"><i></i></div></div>
        <label class="field-label" for="mic-device">Micrófono</label><select id="mic-device" aria-label="Elegir micrófono"><option value="">Predeterminado de Windows</option></select>
        <label class="check"><input id="mic-ptt" type="checkbox"> Pulsar para hablar</label>
        <button id="mic-hold" class="secondary" type="button" hidden>Mantén pulsado para hablar</button>
        <label class="field-label" for="voice-volume">Volumen de la otra persona</label><input id="voice-volume" type="range" min="0" max="1" step="0.05" value="1">
        <p id="voice-note" class="map-note" role="status"></p>
      </details>`;
    (document.querySelector('.shell') || document.body).append(panel);
    for (const id of ['chat-bubble', 'chat-badge', 'chat-panel', 'chat-close', 'chat-send', 'chat-log', 'chat-form', 'chat-input', 'chat-people', 'tool-mouse', 'tool-files', 'mic-toggle', 'mic-meter', 'mic-device', 'mic-ptt', 'mic-hold', 'voice-volume', 'voice-note', 'chat-files', 'chat-files-count', 'chat-share-list', 'chat-files-btn', 'chat-skin-btn', 'chat-gear-btn']) el[id] = document.getElementById(id);
  }

  // ---- Burbuja: abre y cierra el chat; se puede subir o bajar arrastrandola ----
  // El boton se arrastra a donde quieras y se pega al borde mas cercano (arriba, abajo, izquierda o derecha, incluidas las esquinas).
  let spot = (() => { try { const s = JSON.parse(localStorage.getItem('tecChatSpot') || 'null'); if (s && ['left', 'right', 'top', 'bottom'].includes(s.edge)) return { edge: s.edge, pos: Math.max(5, Math.min(95, +s.pos || 55)) }; } catch { /* sin guardar */ } return { edge: 'right', pos: 55 }; })();
  function place() {
    const b = el['chat-bubble'], s = b.style, p = spot.pos, half = (b.offsetWidth || 56) / 2;
    s.top = s.bottom = s.left = s.right = 'auto';
    if (spot.edge === 'right') { s.right = '16px'; s.top = `calc(${p}% - ${half}px)`; }
    else if (spot.edge === 'left') { s.left = '16px'; s.top = `calc(${p}% - ${half}px)`; }
    else if (spot.edge === 'top') { s.top = '16px'; s.left = `calc(${p}% - ${half}px)`; }
    else { s.bottom = '16px'; s.left = `calc(${p}% - ${half}px)`; }
    // El panel flotante se abre del mismo lado donde esta el boton.
    const x = spot.edge === 'left' ? 0 : spot.edge === 'right' ? 100 : p, y = spot.edge === 'top' ? 0 : spot.edge === 'bottom' ? 100 : p;
    el['chat-panel'].classList.toggle('at-left', x < 50); el['chat-panel'].classList.toggle('at-top', y < 50);
  }
  function drag(e) {
    if (e.button !== 0) return; const b = el['chat-bubble'], sx = e.clientX, sy = e.clientY; let moved = false;
    b.setPointerCapture(e.pointerId);
    const move = ev => {
      if (!moved && Math.hypot(ev.clientX - sx, ev.clientY - sy) < 6) return;
      moved = true; b.style.transition = 'none'; const h = b.offsetWidth / 2;
      b.style.right = b.style.bottom = 'auto'; b.style.left = Math.max(0, Math.min(innerWidth - b.offsetWidth, ev.clientX - h)) + 'px'; b.style.top = Math.max(0, Math.min(innerHeight - b.offsetHeight, ev.clientY - h)) + 'px';
    };
    const end = ev => {
      b.removeEventListener('pointermove', move); b.removeEventListener('pointerup', end); b.removeEventListener('pointercancel', end); b.style.transition = '';
      if (moved) {
        const x = ev.clientX, y = ev.clientY, d = { left: x, right: innerWidth - x, top: y, bottom: innerHeight - y };
        const edge = Object.keys(d).reduce((a, k) => d[k] < d[a] ? k : a, 'right');
        spot = { edge, pos: Math.round(Math.max(5, Math.min(95, (edge === 'left' || edge === 'right' ? y / innerHeight : x / innerWidth) * 100))) };
        localStorage.setItem('tecChatSpot', JSON.stringify(spot)); place(); b.dataset.moved = '1'; setTimeout(() => delete b.dataset.moved, 0);
      }
    };
    b.addEventListener('pointermove', move); b.addEventListener('pointerup', end); b.addEventListener('pointercancel', end);
  }
  function setOpen(value) {
    open = value; el['chat-panel'].hidden = !open; el['chat-bubble'].setAttribute('aria-expanded', open); el['chat-bubble'].setAttribute('aria-label', open ? 'Cerrar el chat de la sala' : 'Abrir el chat de la sala');
    document.body.classList.toggle('chat-open', open); api.onOpen?.(open);
    if (open) { unread = 0; badge(); el['chat-input'].focus(); scroll(); listDevices(); } else el['chat-bubble'].focus();
  }
  const badge = () => { el['chat-badge'].hidden = !unread; el['chat-badge'].textContent = unread > 9 ? '9+' : unread; el['chat-bubble'].classList.toggle('alert', unread > 0); };
  const scroll = () => { el['chat-log'].scrollTop = el['chat-log'].scrollHeight; };

  // ---- Mensajes ----
  function addMessage(m) {
    const li = document.createElement('li'), body = document.createElement('div'), text = document.createElement('span'), time = document.createElement('small');
    const who = m.mine ? 'Tú' : m.from || 'Otra PC', first = lastFrom !== who; lastFrom = who;
    li.className = (m.mine ? 'mine' : 'theirs') + (first ? ' first' : '');
    if (first) { const av = document.createElement('i'); av.className = 'avatar'; av.textContent = (m.mine ? state?.machineName || 'Tú' : who).slice(0, 2).toUpperCase(); av.setAttribute('aria-hidden', 'true'); const name = document.createElement('strong'); name.textContent = who; li.append(av); body.append(name); }
    text.textContent = m.text; time.textContent = new Date(m.at * 1000).toLocaleTimeString('es', { hour: '2-digit', minute: '2-digit' });
    body.className = 'msg'; body.append(text, time); li.append(body); el['chat-log'].append(li); scroll();
  }
  function incoming(from, text, at) {
    addMessage({ from, text, at: at || Date.now() / 1000, mine: false });
    if (!open) { unread++; badge(); }
  }
  function system(text) {
    const li = document.createElement('li'); li.className = 'system'; li.textContent = text; el['chat-log'].append(li); lastFrom = null; scroll();
    if (!open) { unread++; badge(); }
  }
  function reset() { el['chat-log'].replaceChildren(); unread = 0; lastFrom = null; badge(); }
  function sendText(e) {
    e.preventDefault(); const text = el['chat-input'].value.trim(); if (!text) return;
    if (!state?.connected) { api.toast('Todavía no hay nadie conectado para recibir el mensaje.'); return; }
    api.send({ type: 'chat', text }); addMessage({ text, at: Date.now() / 1000, mine: true }); el['chat-input'].value = '';
  }

  // ---- Microfono: captura, baja a 16 kHz mono y envia en pedazos de ~40 ms ----
  async function listDevices() {
    try {
      const devices = (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'audioinput');
      const keep = el['mic-device'].value || mic.device; el['mic-device'].replaceChildren(new Option('Predeterminado de Windows', ''));
      devices.forEach((d, i) => el['mic-device'].append(new Option(d.label || `Micrófono ${i + 1}`, d.deviceId)));
      el['mic-device'].value = devices.some(d => d.deviceId === keep) ? keep : '';
    } catch { /* sin permiso todavia: la lista se completa al encender el micrófono */ }
  }
  const note = text => { el['voice-note'].textContent = text; };
  async function setMic(on) {
    if (on === mic.on) return;
    if (!on) { stopMic(); return; }
    if (!navigator.mediaDevices?.getUserMedia) { note('Este equipo no ofrece micrófono al panel.'); api.toast('Este equipo no ofrece micrófono al panel.'); return; }
    try {
      note('Abriendo micrófono…');
      mic.stream = await navigator.mediaDevices.getUserMedia({ audio: { deviceId: mic.device ? { exact: mic.device } : undefined, echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false });
      mic.ctx = new AudioContext(); mic.source = mic.ctx.createMediaStreamSource(mic.stream);
      mic.node = mic.ctx.createScriptProcessor(2048, 1, 1); const ratio = mic.ctx.sampleRate / RATE;
      mic.node.onaudioprocess = e => {
        const input = e.inputBuffer.getChannelData(0); let sum = 0; for (let i = 0; i < input.length; i++) sum += input[i] * input[i];
        mic.level = Math.min(1, Math.sqrt(sum / input.length) * 4);
        if (!talking()) return;
        const n = Math.floor(input.length / ratio), pcm = new Int16Array(n);
        for (let i = 0; i < n; i++) { let acc = 0, count = 0; for (let j = Math.floor(i * ratio); j < Math.floor((i + 1) * ratio) && j < input.length; j++) { acc += input[j]; count++; } const v = Math.max(-1, Math.min(1, acc / Math.max(1, count))); pcm[i] = v < 0 ? v * 32768 : v * 32767; }
        let bin = ''; const bytes = new Uint8Array(pcm.buffer); for (let i = 0; i < bytes.length; i += 8192) bin += String.fromCharCode(...bytes.subarray(i, i + 8192));
        api.send({ type: 'voice', data: btoa(bin) });
      };
      mic.source.connect(mic.node); mic.node.connect(mic.ctx.destination); // el nodo debe estar conectado para procesar; no se escucha: el buffer de salida queda en silencio
      mic.on = true; note(''); listDevices(); meter(); sync();
      mic.stream.getAudioTracks()[0].addEventListener('ended', () => setMic(false));
    } catch (e) { mic.on = false; const text = e.name === 'NotAllowedError' ? 'El micrófono está bloqueado. Permítelo en Windows (Privacidad > Micrófono).' : 'No se pudo abrir el micrófono: ' + (e.message || e.name); note(text); api.toast(text); sync(); }
  }
  function stopMic() {
    mic.on = false; mic.holding = false; mic.node?.disconnect(); mic.source?.disconnect(); mic.stream?.getTracks().forEach(t => t.stop()); mic.ctx?.close().catch(() => {});
    mic.node = mic.source = mic.stream = mic.ctx = null; mic.level = 0; note(''); sync();
  }
  const talking = () => mic.on && state?.connected && (!mic.ptt || mic.holding);
  function meter() {
    const mini = document.querySelectorAll('.mini-meter i');
    if (!mic.on) { el['mic-meter'].firstElementChild.style.width = '0%'; mini.forEach(i => i.style.width = '0%'); return; }
    const pct = Math.round(mic.level * 100); el['mic-meter'].firstElementChild.style.width = pct + '%'; mini.forEach(i => i.style.width = pct + '%'); el['mic-meter'].setAttribute('aria-valuenow', pct);
    el['mic-meter'].classList.toggle('live', talking()); requestAnimationFrame(meter);
  }
  function sync() {
    el['mic-toggle'].setAttribute('aria-checked', mic.on); el['mic-toggle'].querySelector('.sw-label').textContent = 'Micrófono: ' + (mic.on ? (mic.ptt ? 'listo (pulsar para hablar)' : 'encendido') : 'apagado');
    el['mic-hold'].hidden = !(mic.on && mic.ptt); el['chat-bubble'].classList.toggle('mic-on', mic.on);
    document.querySelectorAll('[data-mic-switch]').forEach(b => { b.setAttribute('aria-pressed', mic.on); b.classList.toggle('on', mic.on); const l = b.querySelector('span'); if (l) l.textContent = mic.on ? 'Micrófono encendido' : 'Micrófono'; });
  }

  // ---- Voz entrante: se reproduce en cuanto llega, con un colchon de 80 ms ----
  function ensurePlayback() {
    if (!spk.ctx) { spk.ctx = new AudioContext(); spk.gain = spk.ctx.createGain(); spk.gain.gain.value = spk.volume; spk.gain.connect(spk.ctx.destination); }
    if (spk.ctx.state === 'suspended') spk.ctx.resume().catch(() => {});
  }
  function voice(b64) {
    if (!b64) return; ensurePlayback();
    const bin = atob(b64), n = bin.length >> 1; if (!n) return;
    const buf = spk.ctx.createBuffer(1, n, RATE), ch = buf.getChannelData(0);
    for (let i = 0; i < n; i++) { const v = bin.charCodeAt(2 * i) | (bin.charCodeAt(2 * i + 1) << 8); ch[i] = (v << 16 >> 16) / 32768; }
    const src = spk.ctx.createBufferSource(); src.buffer = buf; src.connect(spk.gain);
    const now = spk.ctx.currentTime; if (spk.next < now + 0.02 || spk.next > now + 0.6) spk.next = now + 0.08;
    src.start(spk.next); spk.next += buf.duration; spk.last = Date.now(); people();
  }
  function people() {
    const names = [state?.machineName ? state.machineName + ' (tú)' : 'Tú']; if (state?.connected) names.push((state.remoteName || 'Otra PC') + (Date.now() - spk.last < 700 ? ' · hablando' : ''));
    el['chat-people'].textContent = state?.connected ? names.join(' · ') : 'Nadie más conectado todavía'; el['chat-send'].disabled = !state?.connected;
    // Control del mouse y envio de archivos: los mismos botones de la sala, aqui a mano.
    const m = el['tool-mouse'], label = m.querySelector('span'), name = state?.remoteName || 'la otra PC', mode = state?.mode;
    label.textContent = mode === 'remote' ? 'Volver a mi PC' : mode === 'peer' ? 'Recuperar mi mouse' : 'Controlar a ' + name;
    m.classList.toggle('on', mode === 'remote'); m.disabled = !state?.connected || (mode === 'local' && state.sessionMode && !state.management);
    m.title = m.disabled && state?.connected ? 'El dueño de la sala debe activar la gestión de pantallas.' : '';
    el['tool-files'].disabled = !state?.extras;
    files();
  }
  // Historial compacto de lo compartido, desplegable dentro del panel flotante.
  let filesSig = '';
  function files() {
    const list = state?.shares || [], sig = JSON.stringify(list); if (sig === filesSig) return; filesSig = sig;
    el['chat-files-count'].textContent = list.length;
    if (!list.length) { const li = document.createElement('li'); li.className = 'empty'; li.textContent = 'Todavía no se compartió nada.'; el['chat-share-list'].replaceChildren(li); return; }
    el['chat-share-list'].replaceChildren(...list.slice().reverse().map(f => {
      const li = document.createElement('li'), info = document.createElement('div'), name = document.createElement('strong'), meta = document.createElement('small'), acts = document.createElement('div');
      name.textContent = f.name; name.title = f.name; meta.textContent = new Date(f.at * 1000).toLocaleTimeString('es', { hour: '2-digit', minute: '2-digit' }) + ' · ' + (f.mine ? 'Tú' : f.sender); info.append(name, meta); acts.className = 'acts';
      const add = (path, label, action) => { const b = document.createElement('button'); b.type = 'button'; b.className = 'secondary'; b.title = label; b.setAttribute('aria-label', label + ': ' + f.name); b.innerHTML = icon(path); b.onclick = () => api.send({ type: 'shareAction', id: f.id, action }); acts.append(b); };
      add('<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>', 'Ver', 'view'); add('<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8"/>', 'Copiar al escritorio', 'desktop');
      li.append(info, acts); return li;
    }));
  }
  setInterval(() => { if (state) people(); }, 700);

  function init(options) {
    api = options; build(); place();
    el['chat-bubble'].addEventListener('pointerdown', drag);
    el['chat-bubble'].addEventListener('click', () => { if (el['chat-bubble'].dataset.moved) return; setOpen(!open); });
    el['chat-close'].onclick = () => setOpen(false);
    el['chat-files-btn'].onclick = () => { el['chat-files'].open = !el['chat-files'].open; if (el['chat-files'].open) el['chat-files'].scrollIntoView({ block: 'nearest' }); };
    el['chat-gear-btn'].onclick = () => document.getElementById('settings-open').click();
    el['chat-skin-btn'].onclick = () => { document.getElementById('settings-open').click(); document.getElementById('skin-title')?.scrollIntoView({ block: 'start' }); };
    el['chat-form'].onsubmit = sendText;
    el['chat-panel'].addEventListener('keydown', e => { if (e.key === 'Escape') setOpen(false); });
    document.querySelectorAll('[data-mic-switch]').forEach(b => b.onclick = () => setMic(!mic.on));
    el['mic-toggle'].onclick = () => setMic(!mic.on);
    el['tool-mouse'].onclick = () => api.send({ type: state?.mode === 'peer' ? 'freeInput' : 'toggleControl' });
    el['tool-files'].onclick = () => api.send({ type: 'pickFiles' });
    el['mic-device'].onchange = async () => { mic.device = el['mic-device'].value; localStorage.setItem('tecMicDevice', mic.device); if (mic.on) { stopMic(); await setMic(true); } };
    el['mic-ptt'].onchange = () => { mic.ptt = el['mic-ptt'].checked; mic.holding = false; sync(); };
    const hold = v => () => { mic.holding = v; el['mic-hold'].classList.toggle('holding', v); };
    el['mic-hold'].onpointerdown = hold(true); el['mic-hold'].onpointerup = el['mic-hold'].onpointerleave = el['mic-hold'].onpointercancel = hold(false);
    el['mic-hold'].onkeydown = e => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); mic.holding = true; el['mic-hold'].classList.add('holding'); } };
    el['mic-hold'].onkeyup = () => { mic.holding = false; el['mic-hold'].classList.remove('holding'); };
    el['voice-volume'].value = spk.volume; el['voice-volume'].oninput = () => { spk.volume = Number(el['voice-volume'].value); localStorage.setItem('tecVoiceVolume', spk.volume); if (spk.gain) spk.gain.gain.value = spk.volume; };
    addEventListener('pointerdown', () => { if (spk.ctx?.state === 'suspended') spk.ctx.resume().catch(() => {}); }, { passive: true });
    navigator.mediaDevices?.addEventListener?.('devicechange', listDevices);
    sync(); people();
    // El boton cambia de tamano en pantalla completa: se vuelve a colocar con su medida real.
    new MutationObserver(place).observe(document.body, { attributes: true, attributeFilter: ['class'] });
    addEventListener('resize', place);
  }
  function render(newState) { state = newState; if (el['chat-bubble']) people(); }
  window.TecChat = { init, render, incoming, voice, system, reset, isOpen: () => open, open: () => setOpen(true) };
})();
