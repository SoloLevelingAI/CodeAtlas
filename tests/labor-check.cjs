const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.BROWSER_EXECUTABLE||undefined});
 const page=await browser.newPage({viewport:{width:1440,height:1000}});
 const errors=[],passed=[];
 page.on('pageerror',e=>errors.push(e.message));
 const check=(ok,label)=>{assert(ok,label);passed.push(label);console.log('PASS '+label);};
 try{
  await page.goto('http://127.0.0.1:5278/standards',{waitUntil:'networkidle'});
  await page.locator('#standards-page').waitFor();
  const rules=await page.locator('#rule-list [data-rule]').evaluateAll(nodes=>nodes.map(node=>node.dataset.rule));
  check(rules.join(',')==='A01,A02,A03,A04,A05,A06,A07,A08,A09,A10','standards lists A01 through A10');
  const status=await page.locator('#rule-status').innerText();
  check(['尚未分析仓库','需要人工验证 / 自动检查未实现','存在问题，需逐项审阅','需要独立回归证据，不能凭本次源码图判为通过','证据不足，不能判为符合','未发现已知问题，仍需人工确认'].includes(status),'status stays conservative: '+status);
  check(!status.includes('认证通过') && status!=='通过','status is not a pass');
  await page.locator('[data-rule="A10"]').click();
  await page.locator('[data-rule="A10"].selected').waitFor();
  await page.waitForFunction(()=>document.querySelector('#rule-status')?.textContent?.includes('独立回归'));
  check((await page.locator('#rule-status').innerText()).includes('独立回归'),'A10 still requires independent regression');
  check((await page.locator('#rule-detail').innerText()).includes('映射的规则'),'detail shows mapped rules');
  await page.screenshot({path:'artifacts/CodeAtlas-standards.png',fullPage:true});
  for (const rule of ['A03','A06','A07','A04','A01']) {
   await page.locator(`[data-rule="${rule}"]`).click();
   await page.locator(`[data-rule="${rule}"].selected`).waitFor();
   if (await page.locator('#rule-detail .evidence-link').count()) break;
  }
  const evidence=page.locator('#rule-detail .evidence-link');
  if(await evidence.count()){
   const evidenceHref=await evidence.first().getAttribute('href');
   check(evidenceHref && !evidenceHref.includes('workspace.'), 'standards evidence names a saved report: '+(evidenceHref||''));
   await evidence.first().click();
   await page.waitForURL(/node=|file=/);
   await page.locator('#detail-body').waitFor();
   check(await page.locator('#detail-body').isVisible(),'evidence link opens the code map');
   await page.screenshot({path:'artifacts/CodeAtlas-evidence-link.png',fullPage:true});
  }else{
   passed.push('selected rule has no located evidence in this report');
  }
  await page.goto('http://127.0.0.1:5278/division',{waitUntil:'networkidle'});
  await page.locator('#division-page').waitFor();
  const banner=await page.locator('#division-banner').innerText();
  check(banner.includes('待整理') || banner.includes('仍需人工确认') || banner.includes('证据不足') || banner.includes('交接受阻') || banner.includes('不是认证通过'),'division banner stays conservative');
  check(!/可以独立交付|认证通过/.test(banner) || banner.includes('不能') || banner.includes('不是'),'banner does not certify delivery');
  check(await page.locator('#inbox').innerText().then(text=>text.includes('待整理')),'inbox bucket is visible');
  const claimable=Number(await page.locator('#claimable-count').innerText());
  const blocked=Number(await page.locator('#blocked-count').innerText());
  check(Number.isInteger(claimable) && Number.isInteger(blocked), 'slice counts are numeric');
  await page.screenshot({path:'artifacts/CodeAtlas-division.png',fullPage:true});
  const risk=page.locator('#division-page .risk-link');
  if(await risk.count()){
   const href=await risk.first().getAttribute('href');
   check(href && !href.includes('workspace.Report'),'handoff link names a saved report');
   await risk.first().click();
   await page.waitForURL(/node=|file=/);
   await page.locator('.inspector h1').waitFor();
   check((await page.locator('.inspector h1').innerText()).length>0,'handoff risk opens source evidence');
   await page.screenshot({path:'artifacts/CodeAtlas-division-evidence.png',fullPage:true});
  }
  await page.goto('http://127.0.0.1:5278/',{waitUntil:'networkidle'});
  check(await page.locator('#standards-link').isVisible() && await page.locator('#division-link').isVisible() && await page.locator('#structure-tree').isVisible(),'map keeps structure and links to both pages');
  check(errors.length===0,'no browser errors: '+errors.join(' | '));
  fs.mkdirSync('artifacts',{recursive:true});
  fs.writeFileSync('artifacts/labor-check.json',JSON.stringify({status:'passed',assertions:passed.length,claimable,blocked,banner,passed},null,2));
 }catch(e){
  fs.mkdirSync('artifacts',{recursive:true});
  await page.screenshot({path:'artifacts/labor-failure.png',fullPage:true}).catch(()=>{});
  throw e;
 }
 finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
