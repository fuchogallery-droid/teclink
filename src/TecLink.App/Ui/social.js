// Perfil propio (nombre, foto, nombre de la computadora), notificaciones (campana) y salas guardadas.
// Todo se guarda en esta PC; nada va a ningun servidor.
(() => {
  const $ = id => document.getElementById(id);
  let api = {}, state, photo = null, sig = '', presence = {}, lastRequest = '';

  const ago = at => { const m = Math.max(0, Math.round((Date.now() / 1000 - at) / 60)); return m < 1 ? 'ahora' : m < 60 ? `hace ${m} min` : `hace ${Math.round(m / 60)} h`; };
  const initials = name => (name || '?').trim().slice(0, 2).toUpperCase();
  const paint = (el, avatar, name) => { el.style.backgroundImage = avatar ? `url("${avatar}")` : ''; el.textContent = avatar ? '' : initials(name); };

  // ---- Perfil ----
  function openProfile(open) {
    $('profile').hidden = $('profile-scrim').hidden = !open;
    if (!open) return;
    const p = state?.profile || {}; photo = null;
    $('profile-name').value = p.name || ''; $('profile-pc').value = p.pc || '';
    paint($('profile-avatar'), p.avatar, p.shown || p.pc); $('profile-name').focus();
  }
  function pickPhoto(file) {
    if (!file) return;
    const img = new Image(), url = URL.createObjectURL(file);
    img.onload = () => {
      const size = 160, c = document.createElement('canvas'); c.width = c.height = size;
      const k = Math.max(size / img.width, size / img.height), w = img.width * k, h = img.height * k;
      c.getContext('2d').drawImage(img, (size - w) / 2, (size - h) / 2, w, h);
      URL.revokeObjectURL(url);
      photo = c.toDataURL('image/jpeg', .82); paint($('profile-avatar'), photo, $('profile-name').value);
    };
    img.onerror = () => { URL.revokeObjectURL(url); api.toast('Esa imagen no se pudo abrir. Prueba con una foto JPG o PNG.'); };
    img.src = url;
  }
  function saveProfile() {
    const message = { type: 'setProfile', name: $('profile-name').value, pc: $('profile-pc').value };
    if (photo !== null) message.avatar = photo;
    api.send(message); openProfile(false); api.toast('Perfil guardado en esta PC.');
  }

  // ---- Notificaciones ----
  function renderNotifications() {
    const n = state.notifications || {}, req = n.request, recent = n.recent || [];
    $('notif-badge').hidden = !req; $('notif-badge').textContent = req ? '1' : '0'; $('notif-open').classList.toggle('ring', !!req);
    const key = JSON.stringify(n); if (key === sig) return; sig = key;
    const list = [];
    if (req) {
      const box = document.createElement('div'); box.className = 'notif-item request';
      box.innerHTML = '<strong></strong><span class="notif-empty">Quiere volver a entrar a tu sala. Tú decides.</span><div class="file-actions"><button class="primary" type="button">Aceptar</button><button class="secondary" type="button">Rechazar</button></div>';
      box.querySelector('strong').textContent = `${req.name} pide entrar a tu sala`;
      const [yes, no] = box.querySelectorAll('button');
      yes.onclick = () => { api.send({ type: 'joinAnswer', id: req.id, accept: true }); };
      no.onclick = () => { api.send({ type: 'joinAnswer', id: req.id, accept: false }); };
      list.push(box);
    }
    for (const e of recent) { const box = document.createElement('div'); box.className = 'notif-item'; box.innerHTML = '<span></span><time></time>'; box.firstChild.textContent = e.text; box.lastChild.textContent = ago(e.at); list.push(box); }
    if (!list.length) { const p = document.createElement('p'); p.className = 'notif-empty'; p.textContent = 'No tienes notificaciones. Aquí te llegan las solicitudes de quienes quieran volver a entrar a tu sala.'; list.push(p); }
    $('notif-list').replaceChildren(...list);
    if (req && req.id !== lastRequest) { lastRequest = req.id; openNotif(true); }
  }
  function openNotif(open) { $('notif-pop').hidden = !open; $('notif-open').setAttribute('aria-expanded', open); }

  // ---- Salas guardadas ----
  function renderRooms() {
    const rooms = state.savedRooms || [], list = $('rooms-list');
    $('save-room').hidden = !state.canSaveRoom || rooms.some(r => r.raw === (state.joinCode || '').replace('-', ''));
    $('rooms-check').disabled = !rooms.length;
    const key = JSON.stringify(rooms) + JSON.stringify(presence) + state.role + state.netMode + state.connected;
    if (key === list.dataset.sig) return; list.dataset.sig = key;
    if (!rooms.length) { const li = document.createElement('li'); li.textContent = 'Todavía no guardaste ninguna sala.'; list.replaceChildren(li); return; }
    list.replaceChildren(...rooms.map(r => {
      const li = document.createElement('li'), info = document.createElement('div'), name = document.createElement('strong'), sub = document.createElement('small'), tag = document.createElement('span'), enter = document.createElement('button'), forget = document.createElement('button');
      info.className = 'room-info'; name.textContent = r.name || 'Sala'; sub.textContent = `Código ${r.code}`; info.append(name, sub);
      const found = presence[r.raw]; tag.className = 'presence' + (found ? ' on' : ''); tag.textContent = found === undefined ? 'Sin revisar' : found ? 'Activa ahora' : 'No la encuentro';
      enter.className = found ? 'primary' : 'secondary'; enter.type = 'button'; enter.textContent = 'Pedir entrada';
      enter.disabled = state.role === 'Server' && state.netMode !== 'idle' && state.connected;
      enter.onclick = () => { api.send({ type: 'enterRoom', code: r.raw }); api.toast('Solicitud enviada. Espera a que te acepten.'); };
      forget.className = 'secondary'; forget.type = 'button'; forget.textContent = 'Olvidar'; forget.onclick = () => api.send({ type: 'forgetRoom', code: r.raw });
      li.append(info, tag, enter, forget); return li;
    }));
  }

  function init(options) {
    api = options;
    $('profile-pick').onclick = () => $('profile-file').click();
    $('profile-file').onchange = e => { pickPhoto(e.target.files[0]); e.target.value = ''; };
    $('profile-remove').onclick = () => { photo = ''; paint($('profile-avatar'), '', $('profile-name').value); };
    $('profile-save').onclick = saveProfile; $('profile-cancel').onclick = () => openProfile(false); $('profile-scrim').onclick = () => openProfile(false);
    $('profile-name').onkeydown = $('profile-pc').onkeydown = e => { if (e.key === 'Enter') saveProfile(); };
    $('notif-open').onclick = () => openNotif($('notif-pop').hidden); $('notif-close').onclick = () => openNotif(false);
    $('save-room').onclick = () => api.send({ type: 'saveRoom' });
    $('rooms-check').onclick = () => { $('rooms-check').disabled = true; $('rooms-check').textContent = 'Buscando…'; api.send({ type: 'checkRooms' }); };
    addEventListener('keydown', e => { if (e.key !== 'Escape') return; if (!$('profile').hidden) openProfile(false); else if (!$('notif-pop').hidden) openNotif(false); });
  }
  function render(newState) { state = newState; renderNotifications(); renderRooms(); }
  function roomsPresence(found) { presence = found || {}; $('rooms-check').textContent = 'Ver cuáles están activas'; $('rooms-check').disabled = false; $('rooms-list').dataset.sig = ''; if (state) renderRooms(); }
  window.TecSocial = { init, render, roomsPresence, openProfile, paint };
})();
