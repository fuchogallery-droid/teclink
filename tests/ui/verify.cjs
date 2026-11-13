const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
(async () => {
  const root = path.resolve(__dirname, '../..');
  const url = pathToFileURL(path.join(root, 'src/TecLink.App/Ui/index.html')).href + '?preview=1';
  const imageDir = 'C:/Users/jenav/Copias_Rafael/1 - Contenido Multimedia/Imágenes/Trabajos realizados/TecLink';
  const reportDir = 'C:/Users/jenav/Copias_Rafael/5 - Programas descargados con Claude/2026-11-12_1941_teclink-mapa_codex/revision';
  fs.mkdirSync(imageDir, { recursive: true }); fs.mkdirSync(reportDir, { recursive: true });
  const auditor = fs.readFileSync('C:/Users/jenav/02_Documentos/Codex/Entrenamiento_Jiron_Diseno_UI/auditor/auditar_ui.js', 'utf8');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = []; page.on('pageerror', error => errors.push(String(error)));
  const audits = [];
  try {
    await page.goto(url);
    if (await page.locator('.monitor').count() !== 3) throw Error('Faltan monitores');
    await page.evaluate(() => window.__tecTest.select('r:screen1'));
    await page.locator('#position-x').fill('2240'); await page.locator('#position-x').press('Tab');
    await page.locator('#position-y').fill('0'); await page.locator('#position-y').press('Tab');
    let draft = await page.evaluate(() => window.__tecTest.draft());
    if (draft.placements.find(p => p.remote).x !== 2240) throw Error('No se actualizó la posición');
    await page.evaluate(() => { document.getElementById('save').click(); document.getElementById('save').click(); });
    await page.getByRole('button', { name: 'Mapa guardado' }).waitFor();
    if (await page.evaluate(() => window.__tecMessages.filter(m => m.type === 'save').length) !== 1) throw Error('Guardado duplicado');
    if (await page.locator('#automatic').isDisabled() || await page.locator('#theme').isDisabled()) throw Error('Controles quedaron bloqueados al guardar');
    await page.evaluate(() => window.__tecTest.receive({ type: 'error', message: 'Validación de prueba' }));
    await page.getByRole('button', { name: 'Guardar mapa' }).waitFor();
    await page.getByRole('tab', { name: 'Conexión' }).click();
    if (!await page.getByRole('heading', { name: 'Tus equipos están conectados' }).isVisible()) throw Error('Pestaña de conexión no responde');
    await page.getByRole('tab', { name: 'Pantallas' }).click();
    const node = page.locator('.remote-monitor'), box = await node.boundingBox();
    const before = (await page.evaluate(() => window.__tecTest.draft())).placements.find(p => p.remote).y;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2); await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2 + 60, { steps: 12 }); await page.mouse.up();
    draft = await page.evaluate(() => window.__tecTest.draft());
    if (draft.placements.find(p => p.remote).y === before) throw Error('El arrastre no cambió la posición');
    await page.locator('#zoom-in').click(); await page.locator('#fit-map').click();
    for (const width of [390, 1280]) for (const theme of ['dark', 'light']) {
      await page.setViewportSize({ width, height: 900 }); await page.goto(url);
      await page.locator('#theme').selectOption(theme);
      await page.waitForTimeout(220);
      const report = await page.evaluate(auditor);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
      audits.push({ width, theme, overflow, ...report });
      await page.screenshot({ path: path.join(imageDir, `2026-11-12_1941_mapa-${theme}-${width}_codex.png`), fullPage: true });
    }
    fs.writeFileSync(path.join(reportDir, '2026-11-12_1941_auditoria-ui_codex.json'), JSON.stringify({ errors, audits }, null, 2));
    console.log(JSON.stringify({ functional: 'PASS: tres monitores, posición, arrastre, zoom, guardado único, recuperación de error y pestañas', errors,
      audits: audits.map(a => ({ width: a.width, theme: a.theme, overflow: a.overflow, ...a.resumen, findings: a.hallazgos.filter(h => h.prioridad === 'A').slice(0, 12) })) }, null, 2));
    if (errors.length || audits.some(a => a.overflow || a.resumen.prioridadA)) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
