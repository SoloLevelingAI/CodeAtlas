const atlasUrl = (route = '/') => {
  const url = new URL(route, process.env.ATLAS_URL || 'http://127.0.0.1:5278');
  if (process.env.ATLAS_REPORT) url.searchParams.set('report', process.env.ATLAS_REPORT);
  return url.href;
};
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.BROWSER_EXECUTABLE || undefined });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  const passed = [];
  page.on('pageerror', error => errors.push(error.message));
  const check = (ok, label) => { assert(ok, label); passed.push(label); console.log('PASS ' + label); };
  try {
    await page.goto(atlasUrl('/architecture'), { waitUntil: 'networkidle' });
    await page.locator('#architecture-page').waitFor();
    check(await page.locator('#architecture-page').getAttribute('data-c4-level') === 'system', 'opens on the system level');
    const diagram = page.locator('#c4-diagram');
    await diagram.waitFor();
    const systemSrc = await diagram.getAttribute('src');
    check(systemSrc && systemSrc.includes('architecture/context.png'), 'system view uses the LikeC4 context export: ' + systemSrc);
    const natural = await diagram.evaluate(image => image.naturalWidth);
    check(natural > 200, 'LikeC4 context image actually loaded');
    await page.screenshot({ path: 'artifacts/CodeAtlas-c4-system.png', fullPage: true });

    await page.locator('[data-host="desktop"]').click();
    await page.locator('#architecture-page[data-c4-level="host"]').waitFor();
    const hostSrc = await page.locator('#c4-diagram').getAttribute('src');
    check(hostSrc && hostSrc.includes('architecture/host-desktop.png'), 'desktop host uses its LikeC4 view: ' + hostSrc);
    await page.screenshot({ path: 'artifacts/CodeAtlas-c4-host.png', fullPage: true });

    await page.locator('[data-module="atlas.explore"]').click();
    await page.locator('#architecture-page[data-c4-level="module"]').waitFor();
    const moduleSrc = await page.locator('#c4-diagram').getAttribute('src');
    check(moduleSrc && moduleSrc.includes('architecture/component.png'), 'module level uses the LikeC4 component export: ' + moduleSrc);
    check(await page.locator('#c4-drift-empty, [data-drift-rule]').count() > 0, 'drift list or the conservative empty note is visible');
    await page.screenshot({ path: 'artifacts/CodeAtlas-c4-module.png', fullPage: true });

    await page.locator('#c4-code').click();
    await page.locator('#architecture-page[data-c4-level="code"]').waitFor();
    const link = page.locator('#c4-code-link');
    await link.waitFor();
    const href = await link.getAttribute('href');
    check(href && (href.includes('node=') || href.includes('file=')), 'code level links to code-map evidence: ' + href);
    await page.screenshot({ path: 'artifacts/CodeAtlas-c4-code.png', fullPage: true });
    await link.click();
    await page.waitForURL(/node=|file=/);
    await page.locator('#detail-body, .inspector h1').first().waitFor();
    check(errors.length === 0, 'no browser errors: ' + errors.join(' | '));
    fs.mkdirSync('artifacts', { recursive: true });
    fs.writeFileSync('artifacts/architecture-check.json', JSON.stringify({ status: 'passed', assertions: passed.length, passed }, null, 2));
  } catch (error) {
    fs.mkdirSync('artifacts', { recursive: true });
    await page.screenshot({ path: 'artifacts/architecture-failure.png', fullPage: true }).catch(() => {});
    throw error;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
