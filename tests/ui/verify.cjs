const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
(async () => {
  const root = path.resolve(__dirname, '../..');
  const url = pathToFileURL(path.join(root, 'src/TecLink.App/Ui/index.html')).href + '?preview=1';
  const imageDir = 'C:/Users/jenav/Copias_Rafael/1 - Contenido Multimedia/Imágenes/Trabajos realizados/TecLink';
  const reportDir = 'C:/Users/jenav/Copias_Rafael/5 - Programas descargados con Claude/2026-11-12_1855_teclink-panel_codex/revision';
  fs.mkdirSync(imageDir, { recursive: true }); fs.mkdirSync(reportDir, { recursive: true });
  const auditor = fs.readFileSync('C:/Users/jenav/02_Documentos/Codex/Entrenamiento_Jiron_Diseno_UI/auditor/auditar_ui.js', 'utf8');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = []; page.on('pageerror', error => errors.push(String(error)));
  const audits = [];
  try {
    await page.goto(url);
    await page.locator('#local-screens .screen-card').nth(1).locator('.screen-select').click();
    await page.locator('#exit').selectOption('top');
    await page.locator('#entry').selectOption('bottom');
    await page.locator('#move-before').click();
    let draft = await page.evaluate(() => window.__tecTest.draft());
    if (draft.order[0] !== 'screen2' || draft.routes[1].exit !== 'top') throw Error('No se actualizó el orden o la salida');
    await page.evaluate(() => { document.getElementById('save').click(); document.getElementById('save').click(); });
    await page.getByRole('button', { name: 'Mapa guardado' }).waitFor();
    if (await page.evaluate(() => window.__tecMessages.filter(m => m.type === 'save').length) !== 1) throw Error('Guardado duplicado');
    if (await page.locator('#automatic').isDisabled() || await page.locator('#theme').isDisabled()) throw Error('Controles quedaron bloqueados al guardar');
    await page.evaluate(() => window.__tecTest.receive({ type: 'error', message: 'Validación de prueba' }));
    await page.getByRole('button', { name: 'Guardar mapa' }).waitFor();
    await page.getByRole('tab', { name: 'Conexión' }).click();
    if (!await page.getByRole('heading', { name: 'Tus equipos están conectados' }).isVisible()) throw Error('Pestaña de conexión no responde');
    await page.getByRole('tab', { name: 'Pantallas' }).click();
    await page.locator('#local-screens .screen-card').nth(0).dragTo(page.locator('#local-screens .screen-card').nth(1));
    draft = await page.evaluate(() => window.__tecTest.draft());
    if (draft.order[0] !== 'screen1') throw Error('Arrastrar tarjetas no cambió el orden');
    for (const width of [390, 1280]) for (const theme of ['dark', 'light']) {
      await page.setViewportSize({ width, height: 900 }); await page.goto(url);
      await page.locator('#theme').selectOption(theme);
      await page.waitForTimeout(220);
      const report = await page.evaluate(auditor);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
      audits.push({ width, theme, overflow, ...report });
      await page.screenshot({ path: path.join(imageDir, `2026-11-12_1855_panel-${theme}-${width}_codex.png`), fullPage: true });
    }
    fs.writeFileSync(path.join(reportDir, '2026-11-12_1855_auditoria-ui_codex.json'), JSON.stringify({ errors, audits }, null, 2));
    console.log(JSON.stringify({ functional: 'PASS: selección, cuatro bordes, orden por botones y arrastre, guardado único, recuperación de error y pestañas', errors,
      audits: audits.map(a => ({ width: a.width, theme: a.theme, overflow: a.overflow, ...a.resumen, findings: a.hallazgos.filter(h => h.prioridad === 'A').slice(0, 12) })) }, null, 2));
    if (errors.length || audits.some(a => a.overflow || a.resumen.prioridadA)) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
