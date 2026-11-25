// Рендер BPMN-схем в PNG через bpmn-js в headless-Chromium.
//
//   cd Task4.1
//   npm install --no-save bpmn-js@18 playwright@1.64.0
//   node render-diagram.mjs
//
// «Проверки» на основной схеме — свёрнутый подпроцесс, его содержимое рисуется отдельной картинкой.

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, 'node_modules', 'bpmn-js', 'dist');
const images = [
  { bpmn: 'payment-process.bpmn', png: 'payment-process.png' },
  { bpmn: 'payment-process.bpmn', png: 'payment-process-checks.png', plane: 'SubProcess_Checks_plane' },
  { bpmn: 'post-factum-refund.bpmn', png: 'post-factum-refund.png' },
];
const padding = 24;

const browser = await chromium.launch({ headless: true });
try {
  for (const { bpmn, png, plane } of images) {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    await page.setContent('<div id="canvas" style="width:1600px;height:1000px"></div>');
    await page.addStyleTag({ path: join(dist, 'assets', 'diagram-js.css') });
    await page.addStyleTag({ path: join(dist, 'assets', 'bpmn-font', 'css', 'bpmn-embedded.css') });
    await page.addStyleTag({ path: join(dist, 'assets', 'bpmn-js.css') });
    await page.addScriptTag({ path: join(dist, 'bpmn-viewer.production.min.js') });
    const svg = await page.evaluate(async ({ xml, plane }) => {
      const viewer = new BpmnJS({ container: '#canvas' });
      await viewer.importXML(xml);
      if (plane) {
        const canvas = viewer.get('canvas');
        canvas.setRootElement(canvas.findRoot(plane));
      }
      return (await viewer.saveSVG()).svg;
    }, { xml: readFileSync(join(here, bpmn), 'utf8'), plane });
    await page.close();

    // белый фон и поля вокруг схемы, растеризация с deviceScaleFactor 2
    const [vx, vy, vw, vh] = /viewBox="([^"]+)"/.exec(svg)[1].split(/\s+/).map(Number);
    const width = Math.ceil(vw + padding * 2);
    const height = Math.ceil(vh + padding * 2);
    const sized = svg
      .replace(/<svg /, `<svg width="${width}" height="${height}" style="background:#ffffff" `)
      .replace(/viewBox="[^"]+"/, `viewBox="${vx - padding} ${vy - padding} ${width} ${height}"`);
    const shot = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 2 });
    await shot.setContent(`<body style="margin:0">${sized}</body>`, { waitUntil: 'load' });
    await shot.screenshot({ path: join(here, png) });
    await shot.close();
    console.log(`${png}: ${width}x${height}`);
  }
} finally {
  await browser.close();
}
