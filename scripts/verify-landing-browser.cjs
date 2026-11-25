'use strict';
const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});
  try{
    for(const viewport of[{width:1440,height:1000},{width:390,height:844}]){
      const context=await browser.newContext({viewport,reducedMotion:'reduce'});
      const page=await context.newPage(),errors=[];
      page.on('pageerror',e=>errors.push(e.name));
      page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
      const response=await page.goto(process.argv[2]||'http://127.0.0.1:18084',{waitUntil:'networkidle'});
      assert.ok(response.headers()['content-security-policy'],'Missing landing CSP');
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Landing overflows');
      const shots=page.locator('.shot img');assert.equal(await shots.count(),2);
      assert.ok(await shots.evaluateAll(xs=>xs.every(x=>x.complete&&x.naturalWidth>0)),'Screenshot missing');
      assert.ok(await shots.evaluateAll(xs=>xs.every(x=>x.src.includes('pwa-')&&x.src.includes('-current.png'))));
      assert.equal(await page.locator('[data-app-link]').first().getAttribute('href'),'https://app.svault.ir');
      if(viewport.width<600){
        const menu=page.locator('.menu-button');await menu.click();
        assert.equal(await menu.getAttribute('aria-expanded'),'true');
        await menu.click();assert.equal(await menu.getAttribute('aria-expanded'),'false');
      }
      await page.screenshot({path:'artifacts/release-review/landing-'+viewport.width+'-current.png',fullPage:true});
      assert.deepEqual(errors,[],'Landing browser/CSP errors');
      console.log('PASS: landing '+viewport.width+'px, current PWA images, reduced motion, layout, menu, CTA, CSP and console.');
      await context.close();
    }
  }finally{await browser.close();}
})().catch(error=>{console.error(error.message);process.exitCode=1;});
