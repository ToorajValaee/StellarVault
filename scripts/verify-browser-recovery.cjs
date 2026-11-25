'use strict';
// Real Edge + IndexedDB crash/CAS acceptance with generated synthetic metadata.
// Starts and kills ONLY its own isolated Edge process tree. No ledger writes.
const {chromium}=require('playwright');
const {spawn}=require('node:child_process');
const fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {randomUUID}=require('node:crypto');
const assert=require('node:assert/strict');
const ROOT='G'+'A'.repeat(55),NET='Synthetic browser recovery acceptance',HASH='ab'.repeat(32);
const envelope=Buffer.from('Synthetic signed envelope; no real transaction').toString('base64');
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
(async()=>{
  assert.equal(process.platform,'win32','This crash harness currently targets Windows Edge.');
  const profile=path.resolve('artifacts/release-review/browser-recovery-'+randomUUID());
  fs.mkdirSync(profile,{recursive:true});
  const edge=path.join(process.env['ProgramFiles(x86)'],'Microsoft/Edge/Application/msedge.exe');
  let broadcasts=0,found=false,child,browser;
  const resources={
    '/db.js':path.resolve('src/StellarVault.Pwa/wwwroot/js/vault-v2-indexeddb.js'),
    '/recovery.js':path.resolve('src/StellarVault.Pwa/wwwroot/js/vault-transaction-recovery.js')};
  const server=http.createServer((req,res)=>{
    res.setHeader('Cache-Control','no-store');
    if(resources[req.url]){res.setHeader('Content-Type','text/javascript');res.end(fs.readFileSync(resources[req.url]));}
    else if(req.url==='/synthetic-submit'){broadcasts++;res.end('synthetic accepted, response deliberately lost');}
    else if(req.url==='/transactions/'+HASH){
      res.writeHead(found?200:404,{'Content-Type':'application/json'});
      res.end(JSON.stringify({hash:HASH,successful:true,ledger:42,envelope_xdr:envelope}));
    }else if(req.headers.accept==='application/json'){
      res.setHeader('Content-Type','application/json');res.end(JSON.stringify({network_passphrase:NET}));
    }else{res.setHeader('Content-Type','text/html');res.end('<!doctype html><title>Isolated recovery acceptance</title><script src="/db.js"></script><script src="/recovery.js"></script>');}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const origin='http://127.0.0.1:'+server.address().port;
  async function launch(){
    const activeFile=path.join(profile,'DevToolsActivePort');
    if(fs.existsSync(activeFile))fs.unlinkSync(activeFile);
    child=spawn(edge,['--headless=new','--no-first-run','--no-default-browser-check',
      '--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'],{stdio:'ignore',windowsHide:true});
    for(let i=0;i<200&&!fs.existsSync(activeFile);i++)await delay(100);
    assert.ok(fs.existsSync(activeFile),'Isolated Edge did not start');
    const port=fs.readFileSync(activeFile,'utf8').split('\n')[0];
    browser=await chromium.connectOverCDP('http://127.0.0.1:'+port);
    return browser.contexts()[0];
  }
  async function crash(){
    const pid=child?.pid;
    if(!pid)return;
    await new Promise((resolve,reject)=>{
      const killer=spawn('taskkill',['/PID',String(pid),'/T','/F'],{stdio:'ignore',windowsHide:true});
      killer.once('error',reject);killer.once('exit',code=>code===0?resolve():reject(Error('Failed to stop isolated Edge')));
    });
    child=null;browser=null;
    await delay(500);
  }
  async function page(context,seed=7,network=NET){
    const p=await context.newPage();await p.goto(origin);
    await p.evaluate(async({root,network,seed,envelope})=>{
      const api=window.stellarVaultTransactionRecovery;
      const transport={submitTransaction:async()=>{
        await fetch('/synthetic-submit',{method:'POST'});
        throw Error('Synthetic response lost');
      }};
      await api.attach('test','skey',root,network,location.origin,
        {rawSecretKey:()=>new Uint8Array(32).fill(seed)},transport);
      window.testSubmit=()=>transport.submitTransaction({hash:()=>new Uint8Array(32).fill(0xab),toXDR:()=>envelope});
    },{root:ROOT,network,seed,envelope});return p;
  }
  try{
    let context=await launch(),a=await page(context),b=await page(context);
    await a.evaluate(()=>window.testSubmit().then(()=>{throw Error('Unexpected success');},error=>{
      if(!error.message.includes('Allow the encrypted'))throw error;
    }));assert.equal(broadcasts,0);
    for(const p of[a,b])await p.evaluate(({root,net})=>window.stellarVaultTransactionRecovery.consent('skey',root,net,true),{root:ROOT,net:NET});
    await Promise.all([a,b].map(p=>p.evaluate(()=>window.testSubmit().catch(()=>{}))));
    assert.equal(broadcasts,1,'Two tabs broadcast despite exclusive durable CAS');
    const original=await a.evaluate(()=>window.stellarVaultTransactionRecovery.inspectSession('skey'));
    assert.equal(original.PendingHash,HASH);
    await crash();
    context=await launch();a=await page(context);
    const reopened=await a.evaluate(()=>window.stellarVaultTransactionRecovery.inspectSession('skey'));
    assert.equal(reopened.PendingHash,HASH);assert.equal(reopened.Consent,false);
    await a.evaluate(({root,net})=>window.stellarVaultTransactionRecovery.consent('skey',root,net,true),{root:ROOT,net:NET});
    await a.evaluate(()=>window.testSubmit().then(()=>{throw Error('Unexpected retry');},()=>{}));
    assert.equal(broadcasts,1,'Crash restart replayed an uncertain submission');
    const wrong=await page(context,8);
    assert.equal(await wrong.evaluate(()=>window.stellarVaultTransactionRecovery.inspectSession('skey').then(()=>false,()=>true)),true);
    const other=await page(context,7,'Other network');
    assert.equal((await other.evaluate(()=>window.stellarVaultTransactionRecovery.inspectSession('skey'))).PendingHash,null);
    found=true;
    await a.evaluate(()=>window.stellarVaultTransactionRecovery.verifySession('test'));
    assert.equal((await a.evaluate(()=>window.stellarVaultTransactionRecovery.inspectSession('skey'))).PendingHash,null);
    assert.equal(broadcasts,1,'Read-only recovery transmitted a transaction');
    // Abort a real IndexedDB write transaction: request success is insufficient.
    await a.evaluate(()=>{
      const original=IDBDatabase.prototype.transaction;
      IDBDatabase.prototype.transaction=function(stores,mode,...args){
        const tx=original.call(this,stores,mode,...args);
        if(mode==='readwrite')queueMicrotask(()=>tx.abort());
        return tx;
      };
    });
    await a.evaluate(()=>window.testSubmit().then(()=>{throw Error('Aborted storage permitted broadcast');},()=>{}));
    assert.equal(broadcasts,1,'Aborted durable checkpoint reached transport');
    console.log('PASS: actual Edge process termination, encrypted IndexedDB restart recovery, per-session consent, two-tab CAS, wrong-key/network isolation and read-only exact-envelope receipt release; zero real ledger writes.');
  }finally{
    await crash();await new Promise(resolve=>server.close(resolve));
  }
})().catch(error=>{console.error(error.message);process.exitCode=1;});
