// Sala: cada pantalla es una tarjeta que se mueve y cambia de tamano, y las camaras son
// burbujas moviles que recortan el fondo (misma tecnologia que TEC Capture: MediaPipe
// Selfie Segmentation). La camara solo se abre cuando el usuario la enciende.
(() => {
  const $ = id => document.getElementById(id);
  const MIN = { card: [200, 130], bubble: [90, 90] };
  let api = {}, state, layout = readLayout(), top = 10, armed = null, armTimer;
  const cam = { on: false, cutout: true, mirror: true, stream: null, seg: null, segLoading: null, lastSent: 0, loop: 0 };
  const peer = { last: 0, cutout: false, url: '' };
  const video = document.createElement('video'); video.muted = true; video.playsInline = true;
  const cut = document.createElement('canvas'), cutx = cut.getContext('2d'), out = document.createElement('canvas'), outx = out.getContext('2d');
  const own = document.createElement('canvas'); // lo que ve el usuario en su burbuja
  const peerImg = new Image();

  function readLayout() { try { return JSON.parse(localStorage.getItem('tecRoom') || '{}'); } catch { return {}; } }
  const saveLayout = () => localStorage.setItem('tecRoom', JSON.stringify(layout));
  const room = () => $('room');
  const msg = text => { $('cam-msg').textContent = text || ''; };

  function items() {
    const hidden = (state.placements || []).filter(p => p.hidden);
    const list = [];
    const add = (arr, remote, pcName, row) => (arr || []).forEach((s, col) => {
      if (hidden.some(p => p.id === s.id && !!p.remote === remote)) return;
      list.push({ key: 's:' + api.key({ id: s.id, remote }), kind: 'screen', remote, screen: s, pc: pcName, row, col, n: col + 1 });
    });
    add(state.local, false, state.machineName, 0);
    if (state.connected) add(state.remote, true, state.remoteName || 'Otra PC', 1);
    if (cam.on) list.push({ key: 'cam:me', kind: 'bubble', mine: true });
    if (peer.url && Date.now() - peer.last < 4000) list.push({ key: 'cam:peer', kind: 'bubble', mine: false });
    return list;
  }

  function place(list) {
    const W = room().clientWidth; if (!W) return false;
    const cols = Math.max(1, ...list.filter(i => i.kind === 'screen').map(i => i.col + 1));
    const w = Math.round(Math.min(420, Math.max(200, (W - 12 * (cols + 1)) / cols))), h = Math.round(w * 0.5625) + 78;
    let bubbles = 0;
    for (const item of list) {
      if (layout[item.key]) continue;
      if (item.kind === 'screen') layout[item.key] = { x: 12 + item.col * (w + 12), y: 12 + item.row * (h + 12), w, h, z: ++top };
      else layout[item.key] = { x: Math.max(12, W - 190 - bubbles * 170), y: 12 + 2 * (h + 12), w: 160, h: 160, z: ++top }, bubbles++;
    }
    return true;
  }

  function apply(node, key) {
    const l = layout[key]; if (!l) return;
    Object.assign(node.style, { left: l.x + 'px', top: l.y + 'px', width: l.w + 'px', height: l.h + 'px', zIndex: l.z });
  }
  function fitRoom() {
    const bottom = Math.max(420, ...Object.entries(layout).filter(([k]) => document.querySelector(`[data-key="${CSS.escape(k)}"]`)).map(([, l]) => l.y + l.h + 24));
    room().style.minHeight = bottom + 'px';
  }

  function makeCard(item) {
    const node = document.createElement('div'); node.className = 'room-card ' + item.kind; node.dataset.key = item.key; node.tabIndex = 0;
    if (item.kind === 'screen') {
      node.setAttribute('role', 'group');
      node.innerHTML = '<div class="rc-head"><div class="rc-title"><strong></strong><small></small></div><button class="rc-x secondary" type="button"></button></div><div class="rc-body"><div class="rc-screen"><b></b></div><button class="rc-live secondary" type="button">Ver en vivo</button></div>';
      node.querySelector('.rc-x').onclick = e => { e.stopPropagation(); removeClick(item.key, node.querySelector('.rc-x')); };
      node.querySelector('.rc-live').onclick = e => { e.stopPropagation(); api.viewScreen(); };
      node.querySelector('.rc-head').onpointerdown = e => startDrag(e, node, item.key, 'move');
      node.querySelector('.rc-screen').onclick = () => api.selectScreen(api.key({ id: item.screen.id, remote: item.remote }));
    } else {
      node.innerHTML = '<div class="bubble-media"></div><div class="bubble-bar"><span class="bubble-name"></span><button class="b-minus secondary" type="button" aria-label="Más pequeña">−</button><button class="b-plus secondary" type="button" aria-label="Más grande">+</button>' + (item.mine ? '<button class="b-off secondary" type="button" aria-label="Apagar cámara">✕</button>' : '') + '</div>';
      const media = node.querySelector('.bubble-media');
      if (item.mine) { own.className = 'bubble-canvas'; video.className = 'bubble-video'; media.append(video, own); } else { peerImg.className = 'bubble-canvas'; media.append(peerImg); }
      node.onpointerdown = e => { if (!e.target.closest('button,.rc-resize')) startDrag(e, node, item.key, 'move'); };
      node.querySelector('.b-minus').onclick = () => scaleBubble(item.key, node, .85);
      node.querySelector('.b-plus').onclick = () => scaleBubble(item.key, node, 1.18);
      node.querySelector('.b-off')?.addEventListener('click', () => setCamera(false));
      node.addEventListener('wheel', e => { e.preventDefault(); scaleBubble(item.key, node, e.deltaY < 0 ? 1.08 : .92); }, { passive: false });
    }
    const handle = document.createElement('div'); handle.className = 'rc-resize'; handle.setAttribute('role', 'separator'); handle.setAttribute('aria-label', 'Cambiar tamaño');
    handle.onpointerdown = e => startDrag(e, node, item.key, 'resize'); node.append(handle);
    node.addEventListener('keydown', e => {
      const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key]; if (!d) return;
      e.preventDefault(); const l = layout[item.key], step = 16;
      if (e.shiftKey) { l.w = Math.max(min(item)[0], l.w + d[0] * step); l.h = item.kind === 'bubble' ? l.w : Math.max(min(item)[1], l.h + d[1] * step); }
      else { l.x = Math.max(0, l.x + d[0] * step); l.y = Math.max(0, l.y + d[1] * step); }
      apply(node, item.key); saveLayout(); fitRoom();
    });
    return node;
  }
  const min = item => item.kind === 'bubble' ? MIN.bubble : MIN.card;

  function scaleBubble(key, node, k) { const l = layout[key], s = Math.round(Math.min(480, Math.max(MIN.bubble[0], l.w * k))); l.w = l.h = s; apply(node, key); saveLayout(); fitRoom(); }

  function startDrag(e, node, key, mode) {
    if (e.button !== 0 || e.target.closest('.rc-x,.rc-live')) return;
    e.preventDefault(); e.stopPropagation();
    const l = layout[key], start = { x: e.clientX, y: e.clientY, l: { ...l } }, bubble = node.classList.contains('bubble'), id = e.pointerId;
    layout[key].z = ++top; node.style.zIndex = top;
    node.setPointerCapture?.(id); const target = e.currentTarget;
    const moveHandler = ev => {
      const dx = ev.clientX - start.x, dy = ev.clientY - start.y, W = room().clientWidth;
      if (mode === 'move') { l.x = Math.max(0, Math.min(W - 40, start.l.x + dx)); l.y = Math.max(0, start.l.y + dy); }
      else if (bubble) { const s = Math.max(MIN.bubble[0], Math.min(480, start.l.w + Math.max(dx, dy))); l.w = l.h = s; }
      else { l.w = Math.max(MIN.card[0], start.l.w + dx); l.h = Math.max(MIN.card[1], start.l.h + dy); }
      apply(node, key); fitRoom();
    };
    const end = () => { target.removeEventListener('pointermove', moveHandler); target.removeEventListener('pointerup', end); target.removeEventListener('pointercancel', end); saveLayout(); };
    target.setPointerCapture?.(id); target.addEventListener('pointermove', moveHandler); target.addEventListener('pointerup', end); target.addEventListener('pointercancel', end);
  }

  function removeClick(key, button) {
    if (armed !== key) { armed = key; clearTimeout(armTimer); armTimer = setTimeout(() => { armed = null; button.textContent = '✕'; button.setAttribute('aria-label', 'Quitar esta pantalla'); }, 3500); button.textContent = '¿Quitar?'; button.setAttribute('aria-label', 'Confirmar: quitar esta pantalla'); return; }
    armed = null; api.removeScreen(key.slice(2));
  }

  let signature = '';
  function render(newState, force) {
    if (newState) state = newState; if (!state || !room()) return;
    updateToolbar();
    const list = items(); if (!place(list)) return;
    const sig = list.map(i => i.key).join('|');
    if (sig !== signature || force) {
      signature = sig; room().replaceChildren();
      for (const item of list) { const node = makeCard(item); room().append(node); apply(node, item.key); }
    }
    for (const item of list) {
      const node = room().querySelector(`[data-key="${CSS.escape(item.key)}"]`); if (!node) continue;
      if (item.kind === 'screen') {
        node.setAttribute('aria-label', `${item.pc}, pantalla ${item.n}`);
        node.querySelector('strong').textContent = `${item.screen.label || 'Pantalla ' + item.n}`;
        node.querySelector('small').textContent = `${item.pc} · ${item.screen.width} × ${item.screen.height}`;
        node.querySelector('.rc-screen').style.aspectRatio = `${item.screen.width} / ${item.screen.height}`; node.querySelector('.rc-screen b').textContent = item.n;
        node.querySelector('.rc-live').hidden = !item.remote; node.querySelector('.rc-live').disabled = !state.extras || !state.management && state.sessionMode;
        const x = node.querySelector('.rc-x'); if (armed !== item.key) { x.textContent = '✕'; x.setAttribute('aria-label', 'Quitar esta pantalla'); }
      } else {
        node.setAttribute('aria-label', item.mine ? 'Tu cámara' : 'Cámara de ' + (state.remoteName || 'la otra PC'));
        node.querySelector('.bubble-name').textContent = item.mine ? 'Tú' : state.remoteName || 'Otra PC';
        const cutout = item.mine ? cam.cutout : peer.cutout; node.classList.toggle('cutout', cutout);
        node.classList.toggle('mirror', item.mine && cam.mirror);
        if (item.mine) { video.hidden = cam.cutout && cam.seg; own.hidden = !(cam.cutout && cam.seg); }
      }
    }
    fitRoom();
  }

  function updateToolbar() {
    const set = (id, on, label) => { $(id).setAttribute('aria-checked', on); const l = $(id).querySelector('.sw-label'); if (l) l.textContent = label; };
    set('cam-toggle', cam.on, `Mi cámara: ${cam.on ? 'encendida' : 'apagada'}`);
    set('cam-cutout', cam.cutout, `Quitar fondo: ${cam.cutout ? 'sí' : 'no'}`);
    set('cam-mirror', cam.mirror, `Espejo: ${cam.mirror ? 'sí' : 'no'}`);
    $('cam-cutout').disabled = $('cam-mirror').disabled = !cam.on;
    $('view-screen').disabled = !(state.extras && (state.management || !state.sessionMode));
  }

  // ---- Camara y recorte de fondo ----
  function loadSegmenter() {
    return cam.segLoading ??= new Promise((resolve, reject) => {
      const s = document.createElement('script'); s.src = 'vendor/selfie_segmentation/selfie_segmentation.js';
      s.onerror = () => reject(Error('No se encontró el módulo de quitar fondo.'));
      s.onload = async () => {
        try {
          const seg = new SelfieSegmentation({ locateFile: f => 'vendor/selfie_segmentation/' + f });
          seg.setOptions({ modelSelection: 0, selfieMode: false }); seg.onResults(onSegment); await seg.initialize(); resolve(seg);
        } catch (e) { reject(e); }
      };
      document.head.append(s);
    }).catch(e => { cam.segLoading = null; throw e; });
  }
  function onSegment(r) {
    const w = video.videoWidth, h = video.videoHeight; if (!w) return;
    if (cut.width !== w || cut.height !== h) { cut.width = own.width = w; cut.height = own.height = h; }
    cutx.save(); cutx.clearRect(0, 0, w, h); cutx.filter = 'blur(1px)'; for (let i = 0; i < 3; i++) cutx.drawImage(r.segmentationMask, 0, 0, w, h); cutx.filter = 'none';
    cutx.globalCompositeOperation = 'source-in'; cutx.drawImage(video, 0, 0, w, h); cutx.restore();
    const o = own.getContext('2d'); o.clearRect(0, 0, w, h); o.drawImage(cut, 0, 0);
  }
  async function setCamera(on) {
    if (on === cam.on) return;
    if (!on) { cam.on = false; cam.stream?.getTracks().forEach(t => t.stop()); cam.stream = null; video.srcObject = null; msg(''); signature = ''; render(); api.send({ type: 'cameraFrame', data: '' }); return; }
    if (!navigator.mediaDevices?.getUserMedia) { msg('Este equipo no ofrece cámara al panel.'); return; }
    try {
      msg('Abriendo cámara…');
      cam.stream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 640 }, height: { ideal: 480 }, frameRate: { ideal: 24 } }, audio: false });
      video.srcObject = cam.stream; await video.play(); cam.on = true; msg(''); signature = ''; render();
      cam.stream.getVideoTracks()[0].addEventListener('ended', () => setCamera(false));
      if (cam.cutout) prepareCutout();
      pump();
    } catch (e) { cam.on = false; msg(e.name === 'NotAllowedError' ? 'La cámara está bloqueada. Permítela en Windows (Privacidad > Cámara).' : 'No se pudo abrir la cámara: ' + (e.message || e.name)); updateToolbar(); }
  }
  async function prepareCutout() {
    if (cam.seg || !cam.cutout) return;
    try { msg('Preparando quitar fondo…'); cam.seg = await loadSegmenter(); msg(''); render(); }
    catch (e) { cam.cutout = false; msg('No se pudo quitar el fondo: ' + e.message); render(); }
  }
  async function pump() {
    const token = ++cam.loop;
    while (cam.on && token === cam.loop) {
      try {
        if (video.readyState >= 2 && video.videoWidth) {
          if (cam.cutout && cam.seg) await cam.seg.send({ image: video });
          sendFrame();
        }
      } catch (e) { msg('Cámara: ' + e.message); }
      await new Promise(r => setTimeout(r, 40));
    }
  }
  function sendFrame() {
    const now = performance.now(); if (now - cam.lastSent < 90 || !state?.connected) return; cam.lastSent = now;
    const useCut = cam.cutout && cam.seg && cut.width, src = useCut ? cut : video, sw = useCut ? cut.width : video.videoWidth, sh = useCut ? cut.height : video.videoHeight;
    const w = 320, h = Math.round(w * sh / sw); if (out.width !== w || out.height !== h) { out.width = w; out.height = h; }
    outx.clearRect(0, 0, w, h); outx.drawImage(src, 0, 0, w, h);
    out.toBlob(blob => {
      if (!blob) return; const reader = new FileReader();
      reader.onload = () => api.send({ type: 'cameraFrame', data: String(reader.result).split(',')[1] });
      reader.readAsDataURL(new Blob([new Uint8Array([useCut ? 1 : 0]), blob]));
    }, 'image/webp', 0.7);
  }
  function peerFrame(b64) {
    if (!b64) { peer.last = 0; render(); return; }
    const bin = atob(b64), bytes = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    if (bytes.length < 2) return;
    const url = URL.createObjectURL(new Blob([bytes.subarray(1)], { type: 'image/webp' }));
    const old = peer.url; peerImg.src = url; peer.url = url; peer.cutout = bytes[0] === 1; if (old) URL.revokeObjectURL(old);
    const wasShown = peer.last && Date.now() - peer.last < 4000; peer.last = Date.now();
    if (!wasShown) render(); else room().querySelector('[data-key="cam:peer"]')?.classList.toggle('cutout', peer.cutout);
  }
  setInterval(() => { if (state && peer.last && Date.now() - peer.last >= 4000 && signature.includes('cam:peer')) render(); }, 1500);

  function init(options) {
    api = options;
    $('cam-toggle').onclick = () => setCamera(!cam.on);
    $('cam-cutout').onclick = () => { cam.cutout = !cam.cutout; if (cam.cutout) prepareCutout(); render(); };
    $('cam-mirror').onclick = () => { cam.mirror = !cam.mirror; render(); };
    $('room-reset').onclick = () => { layout = {}; saveLayout(); signature = ''; render(undefined, true); };
    $('view-screen').onclick = () => api.viewScreen();
    new ResizeObserver(() => { if (state) render(); }).observe(room());
  }
  window.TecRoom = { init, render, peerFrame };
})();
