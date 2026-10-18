const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.BROWSER_EXECUTABLE||undefined});
 const page=await browser.newPage({viewport:{width:1600,height:1050}});
 const errors=[],passed=[];
 page.on('pageerror',error=>errors.push(error.message));
 const check=(ok,label)=>{assert(ok,label);passed.push(label);console.log('PASS '+label);};
 const expand=async key=>{await page.locator(`[data-tree-expand="${key}"]`).click();};
 try{
  await page.goto(new URL('/?report=self',process.env.ATLAS_URL || 'http://127.0.0.1:5278').href,{waitUntil:'networkidle'});
  await page.locator('#runtime-modules').waitFor();
  await expand('f:atlas.analysis');
  await page.locator('[data-children-of="f:atlas.analysis"]').waitFor();
  const sourceBranches=await page.locator('[data-branch]').evaluateAll(nodes=>nodes.map(node=>node.dataset.branch));
  await page.locator('#runtime-modules').click();
  await page.locator('#runtime-module-details').waitFor();
  check(await page.locator('#structure-tree > [data-branch]').count()===3,'runtime roots come from resolved profile rather than source report');
  check(await page.locator('[data-runtime-root]').count()===3 && await page.locator('#overview-map').count()===0,'runtime root index uses the active catalog and keeps source navigation separate');
  const root='f:atlas.explore', nav=root+'/f:atlas.explore.navigation', history=nav+'/f:atlas.explore.navigation.history';
  await expand(root);
  await page.locator(`[data-children-of="${root}"]`).waitFor();
  check(await page.locator(`[data-children-of="${root}"] > [data-branch]`).count()===3,'registered exploration module has map, navigation and index children');
  await expand(nav);
  await page.locator(`[data-branch="${history}"]`).waitFor();
  await page.locator('[data-tree-select="f:atlas.explore.navigation.history"]').click();
  await page.locator('#runtime-module-details[data-module-id="atlas.explore.navigation.history"]').waitFor();
  check(await page.locator(`[data-branch="${history}"].is-selected`).count()===1,'deep logical module selection updates existing nested view and inspector');
  await page.screenshot({path:'artifacts/CodeAtlas-runtime-nested.png',fullPage:true});
  await page.locator('[data-tree-select="f:atlas.explore.map"]').click();
  await page.locator('[data-view-contribution="atlas.explore.map"]').waitFor();
  check(await page.locator('[data-required-capability="atlas.workspace"]').count()===1,'map contribution declares the real workspace data contract');
  check((await page.locator('#runtime-module-details').innerText()).includes('atlas.explorer'),'registered renderer is shown from ViewContribution');
  await page.locator(`[data-branch="${root}"]`).evaluate(node=>node.scrollIntoView({block:'start'}));
  await page.screenshot({path:'artifacts/CodeAtlas-runtime-map.png',fullPage:true});
  await page.locator('[data-tree-select="f:atlas.explore"]').click();
  await page.locator('[data-provided-capability="atlas.workspace"]').waitFor();
  check((await page.locator('#runtime-module-details').innerText()).includes('IExplorerWorkspace'),'real exploration registration exposes workspace capability');
  await expand(root); await expand(root);
  await page.locator(`[data-branch="${history}"]`).waitFor();
  check(await page.locator(`[data-branch="${history}"]`).count()===1,'runtime collapse and reopen preserve nested expansion');
  await page.locator('#diagram-mode').click();
  await page.locator('#feature-map').waitFor({state:'visible'});
  check(await page.locator('#feature-map .map-node').count()===7,'same runtime module data is connected to existing diagram renderer');
  check(await page.locator('#feature-map .map-edge').count()===0,'runtime declarations do not masquerade as static source call evidence');
  await page.locator('#structure-mode').click();
  await page.locator('#source-modules').click();
  await page.locator('#runtime-module-details').waitFor({state:'detached'});
  check(JSON.stringify(await page.locator('[data-branch]').evaluateAll(nodes=>nodes.map(node=>node.dataset.branch)))===JSON.stringify(sourceBranches),'source expansion survives switching runtime catalog');
  await page.locator('#workspace-action').click();
  await page.waitForFunction(()=>!document.querySelector('#workspace-action')?.disabled,{},{timeout:60000});
  check(await page.locator('.error-banner').count()===0,'real registered workspace executes reanalysis successfully');
  if(await page.locator('#report-select option[value="launchdesk-reading"]').count()){
   await page.locator('#report-select').selectOption('launchdesk-reading');
   await page.waitForFunction(()=>document.querySelector('#report-select')?.value==='launchdesk-reading' && document.querySelector('.status')?.textContent?.includes('762'));
   check(await page.locator('.error-banner').count()===0,'legacy LaunchDesk report opens through unchanged workspace and nested source view');
   await page.screenshot({path:'artifacts/CodeAtlas-legacy-report.png',fullPage:true});
  }
  await page.locator('#runtime-modules').click();
  await page.locator('#runtime-module-details').waitFor();
  await page.setViewportSize({width:1200,height:900});
  check(await page.locator('#structure-tree').evaluate(node=>node.scrollWidth<=node.clientWidth+1),'runtime nesting remains bounded at narrower viewport');
  await page.screenshot({path:'artifacts/CodeAtlas-runtime-1200.png',fullPage:true});
  if(process.env.ATLAS_HEADLESS_URL){
   await page.goto(process.env.ATLAS_HEADLESS_URL,{waitUntil:'networkidle'});
   await page.locator('#profile-without-view').waitFor();
   check(await page.locator('#runtime-modules').count()===0,'headless profile actually omits explorer component');
   const response=await page.request.get(process.env.ATLAS_HEADLESS_URL+'/standards');
   check(response.status()===404,'disabled profile routes return unavailable instead of missing-service failure');
  }
  check(errors.length===0,'no browser page errors');
  fs.writeFileSync('artifacts/module-browser-results.json',JSON.stringify({status:'passed',assertions:passed.length,passed},null,2));
 }catch(error){await page.screenshot({path:'artifacts/module-browser-failure.png',fullPage:true});throw error;}
 finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
