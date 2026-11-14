// Diseños (skins): carrusel en Configuración. Se guarda en esta PC (localStorage) y se aplica al abrir.
(() => {
  const $ = id => document.getElementById(id);
  const SKINS = [
    { id: 'original', name: 'Original' }, { id: 'retro', name: 'Retro' }, { id: 'futurista', name: 'Futurista' }, { id: 'neon', name: 'Neón' },
    { id: 'oxidado', name: 'Oxidado' }, { id: 'metal', name: 'Metal' }, { id: 'madera', name: 'Madera' }, { id: 'piedra', name: 'Piedra' },
  ];
  const COLOR_VARS = ['--bg', '--panel', '--raised', '--line', '--grid', '--brand', '--accent', '--text'];
  const read = () => { try { const s = JSON.parse(localStorage.getItem('tecSkin') || '{}'); return { id: SKINS.some(k => k.id === s.id) ? s.id : 'original', tone: +s.tone || 0, light: +s.light || 0 }; } catch { return { id: 'original', tone: 0, light: 0 }; } };
  const write = s => localStorage.setItem('tecSkin', JSON.stringify(s));
  let skin = read();

  function apply(announce) {
    const root = document.documentElement;
    if (skin.id === 'original') { delete root.dataset.skin; root.style.removeProperty('--tone'); root.style.removeProperty('--lx'); }
    else { root.dataset.skin = skin.id; root.style.setProperty('--tone', String(skin.tone)); root.style.setProperty('--lx', skin.light + '%'); }
    const original = skin.id === 'original';
    $('skin-tone').disabled = $('skin-light').disabled = $('skin-reset').disabled = original;
    $('skin-tone').value = skin.tone; $('skin-light').value = skin.light; $('skin-tone-val').textContent = skin.tone + '°'; $('skin-light-val').textContent = (skin.light > 0 ? '+' : '') + skin.light;
    $('skin-name').textContent = SKINS.find(k => k.id === skin.id).name;
    document.querySelectorAll('.skin-card').forEach(c => c.setAttribute('aria-checked', c.dataset.id === skin.id));
    if (announce) dispatchEvent(new Event('tecskin'));
  }
  function choose(id) {
    // Un diseño nuevo parte limpio: los colores sueltos elegidos antes taparían su paleta.
    skin = { id, tone: 0, light: 0 }; write(skin); localStorage.removeItem('tecColors');
    COLOR_VARS.forEach(v => document.documentElement.style.removeProperty(v)); apply(true);
  }
  function build() {
    $('skin-track').replaceChildren(...SKINS.map(k => {
      const b = document.createElement('button'); b.type = 'button'; b.className = 'skin-card secondary'; b.setAttribute('role', 'radio'); b.dataset.id = k.id; b.setAttribute('aria-checked', 'false');
      b.innerHTML = `<div class="skin-preview" data-skin="${k.id}"><i class="sp-bar"></i><i class="sp-panel"><b></b><b></b></i></div><span>${k.name}</span>`;
      b.onclick = () => choose(k.id); return b;
    }));
  }
  function init() {
    build();
    $('skin-tone').oninput = e => { skin.tone = +e.target.value; write(skin); apply(false); };
    $('skin-light').oninput = e => { skin.light = +e.target.value; write(skin); apply(false); };
    $('skin-tone').onchange = $('skin-light').onchange = () => dispatchEvent(new Event('tecskin'));
    $('skin-reset').onclick = () => { skin.tone = 0; skin.light = 0; write(skin); apply(true); };
    const step = dir => $('skin-track').scrollBy({ left: dir * 296, behavior: 'smooth' });
    $('skin-prev').onclick = () => step(-1); $('skin-next').onclick = () => step(1);
    apply(false);
    $('skin-track').querySelector(`[data-id=${skin.id}]`)?.scrollIntoView({ block: 'nearest', inline: 'center' });
  }
  // Se aplica de inmediato (antes de pintar el resto) para que no parpadee el diseño original.
  if (skin.id !== 'original') { document.documentElement.dataset.skin = skin.id; document.documentElement.style.setProperty('--tone', String(skin.tone)); document.documentElement.style.setProperty('--lx', skin.light + '%'); }
  document.addEventListener('DOMContentLoaded', init);
  if (document.readyState !== 'loading') init();
})();
