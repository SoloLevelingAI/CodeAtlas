const atlasUrl=(route='/')=>{const url=new URL(route,process.env.ATLAS_URL||'http://127.0.0.1:5278');if(process.env.ATLAS_REPORT)url.searchParams.set('report',process.env.ATLAS_REPORT);return url.href;};
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
(async () => {
  const browser = await chromium.launch({headless:true, executablePath:process.env.BROWSER_EXECUTABLE || undefined});
  const page = await browser.newPage({viewport:{width:1600,height:1000}});
  page.setDefaultTimeout(20000);
  let sent = 0, received = 0;
  page.on('websocket', socket => {socket.on('framesent', () => sent++);socket.on('framereceived', () => received++);});
  const results = {};
  try {
    await page.goto(atlasUrl(), {waitUntil:'networkidle'});
    await page.locator('#diagram-mode').click();
    await page.locator('[data-feature="atlas.analysis"]').click();
    await page.waitForSelector('[data-child="atlas.analysis.inventory"]');
    const canvas = await page.locator('#feature-map').boundingBox();
    await page.mouse.move(canvas.x + 20, canvas.y + 20);
    await page.waitForTimeout(300);
    let start = performance.now(), beforeSent = sent, beforeReceived = received;
    for (let i=0;i<40;i++) await page.mouse.move(canvas.x + 30 + i*2,canvas.y + 25);
    await page.waitForTimeout(300);
    results.hover = {events:40,sent:sent-beforeSent,received:received-beforeReceived,elapsedMs:Math.round(performance.now()-start)};
    const clickTimes = [];
    for(let i=0;i<6;i++) {
      const feature = i%2 ? 'atlas.analysis' : 'atlas.reports';
      const child = i%2 ? 'atlas.analysis.inventory' : 'atlas.reports.store';
      start = performance.now();
      await page.locator(`[data-feature="${feature}"]`).click();
      await page.waitForSelector(`[data-child="${child}"]`);
      clickTimes.push(Math.round(performance.now()-start));
    }
    results.clickMs = clickTimes;
    await page.mouse.move(canvas.x + 8,canvas.y + 8);
    await page.waitForTimeout(300);
    start = performance.now(); beforeSent=sent; beforeReceived=received;
    await page.mouse.down();
    for(let i=0;i<40;i++) await page.mouse.move(canvas.x + 8 + i*2,canvas.y + 8+i);
    await page.mouse.up();
    await page.waitForTimeout(400);
    results.drag = {events:40,sent:sent-beforeSent,received:received-beforeReceived,elapsedMs:Math.round(performance.now()-start)};
    results.transform = await page.locator('#map-viewport').getAttribute('transform');
    if (process.env.ATLAS_PERF_VERIFY === '1') {
      assert.equal(results.hover.sent,0,'hover must not dispatch server events');
      const previous = results.transform;
      await page.mouse.move(canvas.x+8,canvas.y+8);
      await page.mouse.down();
      beforeSent = sent;
      await page.mouse.move(canvas.x+28,canvas.y+18,{steps:10});
      await page.waitForFunction(old => document.querySelector('#map-viewport').getAttribute('transform') !== old,previous);
      assert.equal(sent,beforeSent,'drag must paint before any server communication');
      const localTransform = await page.locator('#map-viewport').getAttribute('transform');
      await page.mouse.up();
      await page.evaluate(() => window.atlasView.capture());
      assert.equal(await page.locator('#map-viewport').getAttribute('transform'),localTransform,'server commit retains local pan');
      await page.locator('[data-feature="atlas.reports"]').click();
      await page.waitForSelector('[data-child="atlas.reports.store"]');
      await page.locator('#back').click();
      await page.waitForSelector('[data-child="atlas.analysis.inventory"]');
      assert.equal(await page.locator('#map-viewport').getAttribute('transform'),localTransform,'navigation and back retain pan');
      await page.locator('#quality-panel').click();
      await page.waitForSelector('[data-rule="AT012"]');
      assert.equal(await page.locator('[data-finding]').count(),0,'collapsed diagnostic groups do not render hidden rows');
      const rule = page.locator('[data-rule="AT012"]');
      const total = Number(await rule.locator('summary b').innerText());
      await rule.locator('summary').click();
      await page.waitForFunction(count => document.querySelectorAll('[data-rule="AT012"] [data-finding]').length === count,Math.min(total,50));
      if(total>50) {
        await rule.locator('.source-toggle').click();
        await page.waitForFunction(count => document.querySelectorAll('[data-rule="AT012"] [data-finding]').length === count,Math.min(total,100));
      }
      results.interactionChecks = 'passed: idle traffic, local drag, pan commit, navigation return, lazy diagnostics and incremental loading';
    }
    fs.mkdirSync('artifacts',{recursive:true});
    fs.writeFileSync(path.join('artifacts',`performance-${process.env.ATLAS_PERF_LABEL || 'current'}.json`),JSON.stringify(results,null,2));
    console.log(JSON.stringify(results,null,2));
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
