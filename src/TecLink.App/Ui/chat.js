// Burbuja flotante de chat: se pega al lado derecho o izquierdo, abre un panel de chat en
// vivo y ofrece microfono para hablar. El microfono solo se abre cuando el usuario lo enciende.
(() => {
  const RATE = 16000;
  let api = {}, state, open = false, unread = 0, side = localStorage.getItem('tecChatSide') || 'right', topPct = Number(localStorage.getItem('tecChatTop')) || 55;
  const history = [];
  const mic = { on: false, ptt: false, holding: false, stream: null, ctx: null, node: null, source: null, device: localStorage.getItem('tecMicDevice') || '', level: 0 };
  const spk = { ctx: null, gain: null, next: 0, volume: Number(localStorage.getItem('tecVoiceVolume') || 1), muted: false, last: 0 };
  const el = {};

  function build() {
    const root = document.createElement('div'); root.id = 'chat-root';
    root.innerHTML = `
      <button id="chat-bubble" type="button" aria-label="Abrir el chat" aria-expanded="false"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16a1 1 0 011 1v10a1 1 0 01-1 1h-9l-5 4v-4H4a1 1 0 01-1-1V6a1 1 0 011-1z"/></svg><span id="chat-badge" hidden>0</span></button>
      <section id="chat-panel" role="dialog" aria-modal="false" aria-labelledby="chat-title" hidden>
        <header class="chat-head"><div><h2 id="chat-title">Chat en vivo</h2><small id="chat-people"></small></div><button id="chat-side" class="secondary" type="button" aria-label="Pasar el chat al otro lado">⇄</button><button id="chat-close" class="secondary" type="button" aria-label="Cerrar el chat">✕</button></header>
        <ul id="chat-log" class="chat-log" aria-live="polite"></ul>
        <form id="chat-form" class="chat-form"><label class="sr" for="chat-input">Mensaje</label><input id="chat-input" type="text" autocomplete="off" maxlength="2000" placeholder="Escribe un mensaje…"><button id="chat-send" class="secondary" type="submit">Enviar</button></form>
        <div class="chat-voice">
          <div class="voice-row"><button id="mic-toggle" class="ctl-switch" role="switch" aria-checked="false" type="button"><span class="track" aria-hidden="true"><span class="thumb"></span></span><span class="sw-label">Micrófono: apagado</span></button><div id="mic-meter" class="meter" role="meter" aria-label="Nivel del micrófono" aria-valuemin="0" aria-valuemax="100" aria-valuenow="0"><i></i></div></div>
          <label class="field-label" for="mic-device">Micrófono</label><select id="mic-device" aria-label="Elegir micrófono"><option value="">Predeterminado de Windows</option></select>
          <label class="check"><input id="mic-ptt" type="checkbox"> Pulsar para hablar</label>
          <button id="mic-hold" class="secondary" type="button" hidden>Mantén pulsado para hablar</button>
          <label class="field-label" for="voice-volume">Volumen de la otra persona</label><input id="voice-volume" type="range" min="0" max="1" step="0.05" value="1">
          <p id="voice-note" class="map-note" role="status"></p>
        </div>
      </section>`;
    document.body.append(root);
    for (const id of ['chat-bubble', 'chat-badge', 'chat-panel', 'chat-side', 'chat-close', 'chat-send', 'chat-log', 'chat-form', 'chat-input', 'chat-people', 'mic-toggle', 'mic-meter', 'mic-device', 'mic-ptt', 'mic-hold', 'voice-volume', 'voice-note']) el[id] = document.getElementById(id);
  }

  // ---- Burbuja: se arrastra y se pega al lado mas cercano ----
  function place() {
    const b = el['chat-bubble'], p = el['chat-panel'];
    b.style.top = `calc(${topPct}% - 28px)`; b.classList.toggle('left', side === 'left'); p.classList.toggle('left', side === 'left');
  }
  function drag(e) {
    if (e.button !== 0) return; const b = el['chat-bubble'], start = { x: e.clientX, y: e.clientY }; let moved = false;
    b.setPointerCapture(e.pointerId);
    const move = ev => {
      if (!moved && Math.hypot(ev.clientX - start.x, ev.clientY - start.y) < 6) return; moved = true;
      b.style.transition = 'none'; b.style.left = ev.clientX - 28 + 'px'; b.style.right = 'auto'; b.style.top = ev.clientY - 28 + 'px';
    };
    const end = ev => {
      b.removeEventListener('pointermove', move); b.removeEventListener('pointerup', end); b.removeEventListener('pointercancel', end);
      b.style.transition = ''; b.style.left = b.style.right = '';
      if (moved) { side = ev.clientX < innerWidth / 2 ? 'left' : 'right'; topPct = Math.max(8, Math.min(88, ev.clientY / innerHeight * 100)); localStorage.setItem('tecChatSide', side); localStorage.setItem('tecChatTop', String(Math.round(topPct))); place(); b.dataset.moved = '1'; setTimeout(() => delete b.dataset.moved, 0); }
    };
    b.addEventListener('pointermove', move); b.addEventListener('pointerup', end); b.addEventListener('pointercancel', end);
  }
  function setOpen(value) {
    open = value; el['chat-panel'].hidden = !open; el['chat-bubble'].setAttribute('aria-expanded', open); el['chat-bubble'].setAttribute('aria-label', open ? 'Cerrar el chat' : 'Abrir el chat');
    if (open) { unread = 0; badge(); el['chat-input'].focus(); scroll(); listDevices(); } else el['chat-bubble'].focus();
  }
  const badge = () => { el['chat-badge'].hidden = !unread; el['chat-badge'].textContent = unread > 9 ? '9+' : unread; el['chat-bubble'].classList.toggle('alert', unread > 0); };
  const scroll = () => { el['chat-log'].scrollTop = el['chat-log'].scrollHeight; };

  // ---- Mensajes ----
  function addMessage(m) {
    history.push(m); if (history.length > 200) history.shift();
    const li = document.createElement('li'), who = document.createElement('strong'), text = document.createElement('span'), time = document.createElement('small');
    li.className = m.mine ? 'mine' : 'theirs'; who.textContent = m.mine ? 'Tú' : m.from; text.textContent = m.text; time.textContent = new Date(m.at * 1000).toLocaleTimeString('es', { hour: '2-digit', minute: '2-digit' });
    li.append(who, text, time); el['chat-log'].append(li); scroll();
  }
  function incoming(from, text, at) {
    addMessage({ from, text, at: at || Date.now() / 1000, mine: false });
    if (!open) { unread++; badge(); }
  }
  function system(text) {
    const li = document.createElement('li'); li.className = 'system'; li.textContent = text; el['chat-log'].append(li); scroll();
    if (!open) { unread++; badge(); }
  }
  function reset() { history.length = 0; el['chat-log'].replaceChildren(); unread = 0; badge(); }
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
  async function setMic(on) {
    if (on === mic.on) return;
    if (!on) { stopMic(); return; }
    if (!navigator.mediaDevices?.getUserMedia) { el['voice-note'].textContent = 'Este equipo no ofrece micrófono al panel.'; return; }
    try {
      el['voice-note'].textContent = 'Abriendo micrófono…';
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
      mic.on = true; el['voice-note'].textContent = ''; listDevices(); meter(); sync();
      mic.stream.getAudioTracks()[0].addEventListener('ended', () => setMic(false));
    } catch (e) { mic.on = false; el['voice-note'].textContent = e.name === 'NotAllowedError' ? 'El micrófono está bloqueado. Permítelo en Windows (Privacidad > Micrófono).' : 'No se pudo abrir el micrófono: ' + (e.message || e.name); sync(); }
  }
  function stopMic() {
    mic.on = false; mic.holding = false; mic.node?.disconnect(); mic.source?.disconnect(); mic.stream?.getTracks().forEach(t => t.stop()); mic.ctx?.close().catch(() => {});
    mic.node = mic.source = mic.stream = mic.ctx = null; mic.level = 0; el['voice-note'].textContent = ''; sync();
  }
  const talking = () => mic.on && state?.connected && (!mic.ptt || mic.holding);
  function meter() {
    if (!mic.on) { el['mic-meter'].firstElementChild.style.width = '0%'; return; }
    const pct = Math.round(mic.level * 100); el['mic-meter'].firstElementChild.style.width = pct + '%'; el['mic-meter'].setAttribute('aria-valuenow', pct);
    el['mic-meter'].classList.toggle('live', talking()); requestAnimationFrame(meter);
  }
  function sync() {
    el['mic-toggle'].setAttribute('aria-checked', mic.on); el['mic-toggle'].querySelector('.sw-label').textContent = 'Micrófono: ' + (mic.on ? (mic.ptt ? 'listo (pulsar para hablar)' : 'encendido') : 'apagado');
    el['mic-hold'].hidden = !(mic.on && mic.ptt); el['chat-bubble'].classList.toggle('mic-on', mic.on);
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
    el['chat-people'].textContent = names.join(' · '); el['chat-send'].disabled = !state?.connected;
  }
  setInterval(() => { if (state) people(); }, 700);

  function init(options) {
    api = options; build(); place();
    el['chat-bubble'].addEventListener('pointerdown', drag);
    el['chat-bubble'].addEventListener('click', () => { if (el['chat-bubble'].dataset.moved) return; setOpen(!open); });
    el['chat-close'].onclick = () => setOpen(false);
    el['chat-side'].onclick = () => { side = side === 'left' ? 'right' : 'left'; localStorage.setItem('tecChatSide', side); place(); };
    el['chat-form'].onsubmit = sendText;
    el['chat-panel'].addEventListener('keydown', e => { if (e.key === 'Escape') setOpen(false); });
    el['mic-toggle'].onclick = () => setMic(!mic.on);
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
  }
  function render(newState) { state = newState; if (el['chat-bubble']) people(); }
  window.TecChat = { init, render, incoming, voice, system, reset };
})();
