// Sala: aqui se ven EN VIVO las pantallas de todos (las tuyas y las de la otra PC) como tarjetas que se
// mueven, cambian de tamano, se ordenan en cuadricula, se fijan con candado, se ocultan o se abren completas.
// Las camaras son burbujas moviles que recortan el fondo (MediaPipe Selfie Segmentation); con el chat
// abierto pasan a la cuadricula de camaras del chat. La camara solo se abre cuando el usuario la enciende.
(() => {
  const $ = id => document.getElementById(id);
  const MIN = { card: [220, 150], bubble: [90, 90] };
  let api = {}, state, store = readStore(), top = 10, signature = '', camSignature = '', active = false, docked = false, full = null, control = null, lastWatch = '', lastMove = 0;
  const cam = { on: false, cutout: true, mirror: true, stream: null, seg: null, segLoading: null, lastSent: 0, loop: 0 };
  const peer = { last: 0, cutout: false, url: '' };
  const pictures = {}; // key -> object URL of the latest live picture
  const video = document.createElement('video'); video.muted = true; video.playsInline = true;
  const cut = document.createElement('canvas'), cutx = cut.getContext('2d'), out = document.createElement('canvas'), outx = out.getContext('2d');
  const own = document.createElement('canvas'); // lo que ve el usuario en su burbuja
  const peerImg = new Image();

  function readStore() { try { const s = JSON.parse(localStorage.getItem('tecRoom2') || '{}'); return { items: s.items || {}, locked: !!s.locked, cols: s.cols || 0 }; } catch { return { items: {}, locked: false, cols: 0 }; } }
  const save = () => localStorage.setItem('tecRoom2', JSON.stringify(store));
  const layout = () => store.items;
  const room = () => $('room');
  const msg = text => { document.querySelectorAll('[data-cam-msg]').forEach(n => n.textContent = text || ''); };
  const peerCamShown = () => !!peer.url && Date.now() - peer.last < 4000;
  const canSee = () => !!state && state.connected && state.extras && (state.management || !state.sessionMode);

  function screens() {
    const removed = (state.placements || []).filter(p => p.hidden), list = [];
    const add = (arr, remote, pcName) => (arr || []).forEach((s, n) => {
      if (removed.some(p => p.id === s.id && !!p.remote === remote)) return;
      list.push({ key: 's:' + api.key({ id: s.id, remote }), kind: 'screen', remote, screen: s, pc: pcName, n: n + 1, offline: remote && !state.connected });
    });
    add(state.local, false, state.machineName);
    if (state.connected || (state.remote || []).length) add(state.remote, true, state.remoteName || 'Otra PC');
    return list;
  }
  function items() {
    const list = screens().filter(i => !layout()[i.key]?.hidden);
    if (!docked) { if (cam.on) list.push({ key: 'cam:me', kind: 'bubble', mine: true }); if (peerCamShown()) list.push({ key: 'cam:peer', kind: 'bubble', mine: false }); }
    return list;
  }

  function gridSlots(count, cols) {
    const W = room().clientWidth, n = Math.max(1, cols || Math.ceil(Math.sqrt(Math.max(1, count)))), gap = 12;
    const w = Math.max(MIN.card[0], Math.floor((W - gap * (n + 1)) / n)), h = Math.round(w * 0.5625) + 40;
    return i => ({ x: gap + (i % n) * (w + gap), y: gap + Math.floor(i / n) * (h + gap), w, h });
  }
  function place(list) {
    const W = room().clientWidth; if (!W) return false;
    const shown = list.filter(i => i.kind === 'screen'), slot = gridSlots(shown.length, store.cols);
    let bubbles = 0;
    list.forEach(item => {
      if (layout()[item.key]) return;
      if (item.kind === 'screen') layout()[item.key] = { ...slot(shown.indexOf(item)), z: ++top };
      else layout()[item.key] = { x: Math.max(12, W - 190 - bubbles++ * 170), y: 24, w: 160, h: 160, z: ++top + 1000 };
    });
    return true;
  }
  function arrange(cols) {
    store.cols = cols; const shown = items().filter(i => i.kind === 'screen'), slot = gridSlots(shown.length, cols);
    shown.forEach((item, i) => { layout()[item.key] = { ...layout()[item.key], ...slot(i) }; });
    save(); signature = ''; render();
  }

  function apply(node, key) {
    const l = layout()[key]; if (!l) return;
    Object.assign(node.style, { left: l.x + 'px', top: l.y + 'px', width: l.w + 'px', height: l.h + 'px', zIndex: l.z });
  }
  function fitRoom() {
    const bottom = Math.max(420, ...[...room().children].map(n => { const l = layout()[n.dataset.key]; return l ? l.y + l.h + 24 : 0; }));
    room().style.minHeight = (full ? Math.max(bottom, Math.round(innerHeight * .62)) : bottom) + 'px';
  }

  function makeCard(item) {
    const node = document.createElement('div'); node.className = 'room-card ' + item.kind; node.dataset.key = item.key; node.tabIndex = 0;
    if (item.kind === 'screen') {
      node.setAttribute('role', 'group');
      node.innerHTML = '<div class="rc-head"><div class="rc-title"><strong></strong><small></small></div><button class="rc-release primary" type="button" hidden>Soltar control</button><button class="rc-exit secondary" type="button" hidden>Salir de pantalla completa</button></div>' +
        '<div class="rc-view"><img class="rc-img" alt="" draggable="false" hidden><p class="rc-wait"></p><div class="rc-actions"><button class="secondary" data-act="full" type="button">Pantalla completa</button><button class="secondary" data-act="control" type="button">Controlar</button><button class="secondary" data-act="hide" type="button">Ocultar</button><button class="secondary danger" data-act="remove" type="button">Quitar</button></div></div>';
      node.querySelector('.rc-head').onpointerdown = e => { if (!e.target.closest('button')) startDrag(e, node, item.key, 'move'); };
      node.querySelector('.rc-actions').onclick = e => { const act = e.target.closest('button')?.dataset.act; if (act) action(item, act); };
      node.querySelector('.rc-exit').onclick = () => setFull(null);
      node.querySelector('.rc-release').onclick = () => setControl(null);
      node.querySelector('.rc-head').ondblclick = () => setFull(full === item.key ? null : item.key);
      wireControl(node.querySelector('.rc-img'), item);
      if (pictures[item.key]) { const img = node.querySelector('.rc-img'); img.src = pictures[item.key]; img.hidden = false; }
    } else {
      node.innerHTML = '<div class="bubble-media"></div><div class="bubble-bar"><span class="bubble-name"></span><button class="b-minus secondary" type="button" aria-label="Más pequeña">−</button><button class="b-plus secondary" type="button" aria-label="Más grande">+</button>' + (item.mine ? '<button class="b-off secondary" type="button" aria-label="Apagar cámara">✕</button>' : '') + '</div>';
      mountMedia(node.querySelector('.bubble-media'), item.mine);
      node.onpointerdown = e => { if (!e.target.closest('button,.rc-resize')) startDrag(e, node, item.key, 'move'); };
      node.querySelector('.b-minus').onclick = () => scaleBubble(item.key, node, .85);
      node.querySelector('.b-plus').onclick = () => scaleBubble(item.key, node, 1.18);
      node.querySelector('.b-off')?.addEventListener('click', () => setCamera(false));
      node.addEventListener('wheel', e => { e.preventDefault(); scaleBubble(item.key, node, e.deltaY < 0 ? 1.08 : .92); }, { passive: false });
    }
    const handle = document.createElement('div'); handle.className = 'rc-resize'; handle.setAttribute('role', 'separator'); handle.setAttribute('aria-label', 'Cambiar tamaño');
    handle.onpointerdown = e => startDrag(e, node, item.key, 'resize'); node.append(handle);
    node.addEventListener('keydown', e => {
      if (e.key === 'Escape' && full === item.key && !control) { setFull(null); return; }
      const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key]; if (!d || store.locked || control || e.target !== node) return;
      e.preventDefault(); const l = layout()[item.key], step = 16, m = item.kind === 'bubble' ? MIN.bubble : MIN.card;
      if (e.shiftKey) { l.w = Math.max(m[0], l.w + d[0] * step); l.h = item.kind === 'bubble' ? l.w : Math.max(m[1], l.h + d[1] * step); }
      else { l.x = Math.max(0, l.x + d[0] * step); l.y = Math.max(0, l.y + d[1] * step); }
      apply(node, item.key); save(); fitRoom();
    });
    return node;
  }
  function mountMedia(media, mine) {
    if (mine) { own.className = 'bubble-canvas'; video.className = 'bubble-video'; media.append(video, own); if (cam.on) video.play().catch(() => {}); }
    else { peerImg.className = 'bubble-canvas'; media.append(peerImg); }
  }

  function action(item, act) {
    if (act === 'full') setFull(full === item.key ? null : item.key);
    else if (act === 'hide') { layout()[item.key].hidden = true; if (full === item.key) full = null; save(); signature = ''; render(); api.toast('Pantalla oculta solo para ti. Vuelve a mostrarla con «Mostrar ocultas».'); }
    else if (act === 'control') { if (!canSee()) { api.toast('Activa «Gestión de pantallas» para controlar la otra PC.'); return; } setFull(item.key); setControl(item.key); }
    else if (act === 'remove') { api.send({ type: 'removeAsk', id: item.screen.id }); }
  }
  function setFull(key) {
    if (control && key !== control) setControl(null);
    full = key; signature = ''; lastWatch = ''; render();
    if (key) room().querySelector(`[data-key="${CSS.escape(key)}"]`)?.focus();
  }
  function setControl(key) {
    if (control === key) return;
    control = key; api.send({ type: 'tileControl', on: !!key });
    if (key) api.toast('Controlas la otra PC con el mouse sobre su imagen y con el teclado. Pulsa «Soltar control» para terminar.');
    render();
  }
  function controlOff() { if (!control) return; control = null; render(); }

  // El mouse sobre la imagen de una pantalla de la otra PC, mientras se controla, va a esa PC.
  function wireControl(img, item) {
    const point = e => {
      const r = img.getBoundingClientRect(), nw = img.naturalWidth || 16, nh = img.naturalHeight || 9, k = Math.min(r.width / nw, r.height / nh), w = nw * k, h = nh * k;
      return { x: Math.min(1, Math.max(0, (e.clientX - r.left - (r.width - w) / 2) / w)), y: Math.min(1, Math.max(0, (e.clientY - r.top - (r.height - h) / 2) / h)) };
    };
    const on = () => control === item.key;
    img.addEventListener('pointermove', e => { if (!on()) return; const now = performance.now(); if (now - lastMove < 14) return; lastMove = now; api.send({ type: 'tileMove', id: item.screen.id, ...point(e) }); });
    img.addEventListener('pointerdown', e => { if (!on()) return; e.preventDefault(); img.setPointerCapture(e.pointerId); api.send({ type: 'tileMove', id: item.screen.id, ...point(e) }); api.send({ type: 'tileButton', button: e.button, down: true }); });
    img.addEventListener('pointerup', e => { if (!on()) return; e.preventDefault(); api.send({ type: 'tileButton', button: e.button, down: false }); });
    img.addEventListener('contextmenu', e => { if (on()) e.preventDefault(); });
    img.addEventListener('wheel', e => { if (!on()) return; e.preventDefault(); api.send({ type: 'tileWheel', delta: e.deltaY < 0 ? 120 : -120 }); }, { passive: false });
  }

  function scaleBubble(key, node, k) { const l = layout()[key], s = Math.round(Math.min(480, Math.max(MIN.bubble[0], l.w * k))); l.w = l.h = s; apply(node, key); save(); fitRoom(); }

  function startDrag(e, node, key, mode) {
    if (e.button !== 0 || store.locked || full) return;
    e.preventDefault(); e.stopPropagation();
    const l = layout()[key], start = { x: e.clientX, y: e.clientY, l: { ...l } }, bubble = node.classList.contains('bubble'), id = e.pointerId;
    l.z = ++top + (bubble ? 1000 : 0); node.style.zIndex = l.z;
    const target = e.currentTarget;
    const moveHandler = ev => {
      const dx = ev.clientX - start.x, dy = ev.clientY - start.y, W = room().clientWidth;
      if (mode === 'move') { l.x = Math.max(0, Math.min(W - 40, start.l.x + dx)); l.y = Math.max(0, start.l.y + dy); }
      else if (bubble) { const s = Math.max(MIN.bubble[0], Math.min(480, start.l.w + Math.max(dx, dy))); l.w = l.h = s; }
      else { l.w = Math.max(MIN.card[0], start.l.w + dx); l.h = Math.max(MIN.card[1], start.l.h + dy); }
      apply(node, key); fitRoom();
    };
    const end = () => { target.removeEventListener('pointermove', moveHandler); target.removeEventListener('pointerup', end); target.removeEventListener('pointercancel', end); save(); };
    target.setPointerCapture?.(id); target.addEventListener('pointermove', moveHandler); target.addEventListener('pointerup', end); target.addEventListener('pointercancel', end);
  }

  function render(newState, force) {
    if (newState) state = newState; if (!state || !room()) return;
    updateToolbar(); renderCams();
    if (full && !screens().some(i => i.key === full)) full = null;
    if (control && (!canSee() || !screens().some(i => i.key === control))) { control = null; api.send({ type: 'tileControl', on: false }); }
    const list = items(); if (!place(list)) { syncWatch(); return; }
    const sig = list.map(i => i.key).join('|') + '#' + (full || '') + '#' + docked;
    if (sig !== signature || force) {
      signature = sig; room().replaceChildren();
      for (const item of list) { const node = makeCard(item); room().append(node); apply(node, item.key); }
    }
    room().classList.toggle('locked', store.locked); room().classList.toggle('has-full', !!full);
    const admin = state.role === 'Server';
    for (const item of list) {
      const node = room().querySelector(`[data-key="${CSS.escape(item.key)}"]`); if (!node) continue;
      if (item.kind === 'screen') {
        const isFull = full === item.key, controlling = control === item.key, img = node.querySelector('.rc-img'), wait = node.querySelector('.rc-wait');
        node.setAttribute('aria-label', `${item.pc}, pantalla ${item.n}`);
        node.querySelector('strong').textContent = `${item.pc} · Pantalla ${item.n}`;
        node.querySelector('small').textContent = `${item.remote ? 'Otra PC' : 'Esta PC'} · ${item.screen.width} × ${item.screen.height}${item.offline ? ' · Desconectada' : ''}`;
        node.classList.toggle('offline', !!item.offline); node.classList.toggle('remote', item.remote); node.classList.toggle('full', isFull); node.classList.toggle('controlling', controlling);
        const blocked = item.remote && !item.offline && !canSee();
        if (item.offline || blocked) { img.hidden = true; }
        wait.hidden = !img.hidden; wait.textContent = item.offline ? 'Desconectada' : blocked ? (state.role === 'Server' ? 'Activa «Gestión de pantallas» (barra de arriba) para ver esta pantalla.' : 'El dueño de la sala debe activar la gestión de pantallas.') : 'Cargando imagen en vivo…';
        node.querySelector('.rc-exit').hidden = !isFull || controlling; node.querySelector('.rc-release').hidden = !controlling;
        const b = act => node.querySelector(`[data-act=${act}]`);
        b('full').textContent = isFull ? 'Salir de pantalla completa' : 'Pantalla completa';
        b('control').hidden = !item.remote || controlling; b('control').disabled = item.offline || !canSee();
        b('remove').hidden = !(item.remote && admin); b('remove').disabled = item.offline;
        b('hide').hidden = controlling;
      } else {
        node.setAttribute('aria-label', item.mine ? 'Tu cámara' : 'Cámara de ' + (state.remoteName || 'la otra PC'));
        node.querySelector('.bubble-name').textContent = item.mine ? 'Tú' : state.remoteName || 'Otra PC';
        node.classList.toggle('cutout', item.mine ? cam.cutout : peer.cutout); node.classList.toggle('mirror', item.mine && cam.mirror);
      }
    }
    video.hidden = !!(cam.cutout && cam.seg); own.hidden = !(cam.cutout && cam.seg);
    fitRoom(); syncWatch();
  }

  // Camaras dentro del chat acoplado: una celda por persona, siempre visibles.
  function renderCams() {
    const host = $('chat-cams'); if (!host) return;
    const people = docked ? [{ who: 'me', name: 'Tú', on: cam.on }].concat(state.connected ? [{ who: 'peer', name: state.remoteName || 'Otra PC', on: peerCamShown() }] : []) : [];
    const sig = people.map(p => p.who + p.on).join('|');
    if (sig !== camSignature) {
      camSignature = sig; host.replaceChildren(); host.style.setProperty('--cams', Math.max(1, Math.min(3, people.length)));
      for (const p of people) {
        const cell = document.createElement('div'); cell.className = 'cam-cell'; cell.dataset.who = p.who;
        cell.innerHTML = '<div class="cam-media"></div><span class="cam-name"></span>';
        if (p.on) mountMedia(cell.querySelector('.cam-media'), p.who === 'me');
        else { const off = document.createElement('span'); off.className = 'cam-off'; off.textContent = (p.name === 'Tú' ? state.machineName || 'Tú' : p.name).slice(0, 2).toUpperCase(); cell.querySelector('.cam-media').append(off); }
        host.append(cell);
      }
    }
    for (const p of people) {
      const cell = host.querySelector(`[data-who=${p.who}]`); if (!cell) continue;
      cell.querySelector('.cam-name').textContent = p.name + (p.on ? '' : ' · cámara apagada');
      cell.classList.toggle('cutout', p.who === 'me' ? cam.cutout && !!cam.seg : peer.cutout); cell.classList.toggle('mirror', p.who === 'me' && cam.mirror);
    }
  }

  function updateToolbar() {
    const set = (id, on, label) => { const b = $(id); if (!b) return; b.setAttribute('aria-checked', on); const l = b.querySelector('.sw-label'); if (l) l.textContent = label; };
    set('cam-toggle', cam.on, `Mi cámara: ${cam.on ? 'encendida' : 'apagada'}`);
    set('cam-cutout', cam.cutout, `Quitar fondo: ${cam.cutout ? 'sí' : 'no'}`);
    set('cam-mirror', cam.mirror, `Espejo: ${cam.mirror ? 'sí' : 'no'}`);
    $('cam-cutout').disabled = $('cam-mirror').disabled = !cam.on;
    document.querySelectorAll('[data-cam-switch]').forEach(b => { b.setAttribute('aria-pressed', cam.on); b.classList.toggle('on', cam.on); const l = b.querySelector('span'); if (l) l.textContent = cam.on ? 'Cámara encendida' : 'Cámara'; });
    const hidden = state ? screens().filter(i => layout()[i.key]?.hidden).length : 0;
    $('room-show').hidden = !hidden; $('room-show').textContent = `Mostrar ocultas (${hidden})`;
    $('room-lock').setAttribute('aria-pressed', store.locked); $('room-lock').querySelector('span').textContent = store.locked ? 'Fijas: toca para liberar' : 'Fijar tarjetas';
    document.querySelectorAll('[data-cols]').forEach(b => b.setAttribute('aria-pressed', String(store.cols) === b.dataset.cols));
  }

  // Pide (o detiene) las imagenes en vivo: solo mientras la Sala esta a la vista.
  function syncWatch() {
    if (!state) return;
    const want = active && !document.hidden;
    const focus = want && full ? screens().find(i => i.key === full) : null;
    const message = want ? { type: 'watch', on: true, width: focus ? 1600 : 520, ...(focus ? { focusRemote: focus.remote, focusId: focus.screen.id } : {}) } : { type: 'watch', on: false };
    const sig = JSON.stringify(message) + '|' + canSee() + '|' + (state.remote || []).length + '|' + (state.local || []).length;
    if (sig === lastWatch) return; lastWatch = sig; api.send(message);
  }
  function tile(m) {
    const key = 's:' + api.key({ id: m.id, remote: m.remote });
    const bin = atob(m.data), bytes = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    const url = URL.createObjectURL(new Blob([bytes], { type: 'image/jpeg' })), old = pictures[key]; pictures[key] = url;
    const node = room().querySelector(`[data-key="${CSS.escape(key)}"]`);
    if (node && !node.classList.contains('offline')) { const img = node.querySelector('.rc-img'); img.src = url; if (img.hidden) { img.hidden = false; node.querySelector('.rc-wait').hidden = true; } }
    if (old) setTimeout(() => URL.revokeObjectURL(old), 1500);
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
  function refresh() { signature = ''; camSignature = ''; render(); api.changed?.(); }
  async function setCamera(on) {
    if (on === cam.on) return;
    if (!on) { cam.on = false; cam.stream?.getTracks().forEach(t => t.stop()); cam.stream = null; video.srcObject = null; msg(''); refresh(); api.send({ type: 'cameraFrame', data: '' }); return; }
    if (!navigator.mediaDevices?.getUserMedia) { msg('Este equipo no ofrece cámara al panel.'); api.toast('Este equipo no ofrece cámara al panel.'); return; }
    try {
      msg('Abriendo cámara…');
      cam.stream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 640 }, height: { ideal: 480 }, frameRate: { ideal: 24 } }, audio: false });
      video.srcObject = cam.stream; await video.play(); cam.on = true; msg(''); refresh();
      cam.stream.getVideoTracks()[0].addEventListener('ended', () => setCamera(false));
      if (cam.cutout) prepareCutout();
      pump();
    } catch (e) { cam.on = false; const text = e.name === 'NotAllowedError' ? 'La cámara está bloqueada. Permítela en Windows (Privacidad > Cámara).' : 'No se pudo abrir la cámara: ' + (e.message || e.name); msg(text); api.toast(text); updateToolbar(); }
  }
  async function prepareCutout() {
    if (cam.seg || !cam.cutout) return;
    try { msg('Preparando quitar fondo…'); cam.seg = await loadSegmenter(); msg(''); refresh(); }
    catch (e) { cam.cutout = false; msg('No se pudo quitar el fondo: ' + e.message); refresh(); }
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
    if (!b64) { peer.last = 0; refresh(); return; }
    const bin = atob(b64), bytes = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    if (bytes.length < 2) return;
    const url = URL.createObjectURL(new Blob([bytes.subarray(1)], { type: 'image/webp' }));
    const old = peer.url, wasShown = peerCamShown(), wasCut = peer.cutout; peerImg.src = url; peer.url = url; peer.cutout = bytes[0] === 1; peer.last = Date.now(); if (old) URL.revokeObjectURL(old);
    if (!wasShown || wasCut !== peer.cutout) render();
  }
  setInterval(() => { if (state && peer.last && !peerCamShown() && (signature.includes('cam:peer') || camSignature.includes('peertrue'))) render(); }, 1500);

  function init(options) {
    api = options;
    $('cam-toggle').onclick = () => setCamera(!cam.on);
    $('cam-cutout').onclick = () => { cam.cutout = !cam.cutout; if (cam.cutout) prepareCutout(); refresh(); };
    $('cam-mirror').onclick = () => { cam.mirror = !cam.mirror; render(); };
    document.querySelectorAll('[data-cam-switch]').forEach(b => b.onclick = () => setCamera(!cam.on));
    document.querySelectorAll('[data-cols]').forEach(b => b.onclick = () => arrange(Number(b.dataset.cols)));
    $('room-lock').onclick = () => { store.locked = !store.locked; save(); render(); api.toast(store.locked ? 'Tarjetas fijas: no se mueven hasta que toques el candado otra vez.' : 'Tarjetas liberadas: ya puedes moverlas.'); };
    $('room-show').onclick = () => { Object.values(layout()).forEach(l => delete l.hidden); save(); signature = ''; render(); };
    addEventListener('keydown', e => { if (e.key === 'Escape' && full && !control) setFull(null); });
    document.addEventListener('visibilitychange', () => { lastWatch = ''; syncWatch(); });
    let lastW = 0;
    new ResizeObserver(() => {
      if (!state) return;
      const W = room().clientWidth;
      const overflow = W && Object.entries(layout()).some(([k, l]) => !l.hidden && !k.startsWith('cam:') && l.x + l.w > W + 4);
      if (W) lastW = W;
      // La sala se hizo mas angosta (por ejemplo al abrir el chat): las tarjetas se reacomodan para que ninguna quede fuera de vista.
      if (overflow && !store.locked && !full) { arrange(store.cols); return; }
      render();
    }).observe(room());
  }
  window.TecRoom = {
    init, render, peerFrame, tile, controlOff,
    setActive(value) { active = value; lastWatch = ''; if (!value && control) setControl(null); if (state) render(undefined, true); },
    setDocked(value) { docked = value; signature = ''; camSignature = ''; if (state) render(); },
    shown() { lastWatch = ''; syncWatch(); },
    toggleCamera: () => setCamera(!cam.on), camOn: () => cam.on,
  };
})();
