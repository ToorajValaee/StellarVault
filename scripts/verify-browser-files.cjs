'use strict';
// Browser-native writable-file streams, generated data, no network/ledger writes.
const {chromium}=require('playwright');
const fs=require('node:fs'),http=require('node:http'),crypto=require('node:crypto');
const assert=require('node:assert/strict');
(async()=>{
  const source=fs.readFileSync('src/StellarVault.Pwa/wwwroot/js/vault-v2-download.js');
  const server=http.createServer((req,res)=>{
    if(req.url==='/download.js'){res.setHeader('Content-Type','text/javascript');res.end(source);}
    else{res.setHeader('Content-Type','text/html');res.end('<!doctype html><script src="/download.js"></script>');}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({channel:'msedge',headless:true});
  try{
    const page=await browser.newPage();await page.goto('http://127.0.0.1:'+server.address().port);
    const size=16*1024*1024;
    const expected=crypto.createHash('sha256');
    for(let offset=0;offset<size;offset+=65536){const bytes=Buffer.alloc(65536);for(let i=0;i<bytes.length;i++)bytes[i]=(offset+i)%251;expected.update(bytes);}
    const result=await page.evaluate(async size=>{
      const root=await navigator.storage.getDirectory();
      const handle=await root.getFileHandle('synthetic-stream-acceptance.bin',{create:true});
      const names=[];
      window.showSaveFilePicker=async options=>{names.push(options.suggestedName);return handle;};
      const digest=async()=>{
        const file=await handle.getFile();
        return {size:file.size,hash:[...new Uint8Array(await crypto.subtle.digest('SHA-256',await file.arrayBuffer()))].map(x=>x.toString(16).padStart(2,'0')).join('')};
      };
      const destination=await window.stellarVaultV2Download.selectSaveDestination('file-x0-17.bin');
      let offset=0;
      await destination.writeStream({stream:async()=>new ReadableStream({pull(controller){
        if(offset===size){controller.close();return;}
        const block=new Uint8Array(Math.min(65536,size-offset));
        for(let i=0;i<block.length;i++)block[i]=(offset+i)%251;
        offset+=block.length;controller.enqueue(block);
      }})});
      const complete=await digest();
      let produced=0,rejected=false;
      try{
        await window.stellarVaultV2Download.saveStream('file-x0-17.bin',{stream:async()=>new ReadableStream({pull(controller){
          if(produced>=131072){controller.error(Error('Synthetic interrupted export'));return;}
          produced+=65536;controller.enqueue(new Uint8Array(65536).fill(99));
        }})});
      }catch{rejected=true;}
      const afterAbort=await digest();
      let pickerCanceled=false;
      window.showSaveFilePicker=async()=>{throw new DOMException('Synthetic cancellation','AbortError');};
      try{await window.stellarVaultV2Download.selectSaveDestination('cancel.bin');}catch{pickerCanceled=true;}
      await root.removeEntry('synthetic-stream-acceptance.bin');
      return {complete,afterAbort,rejected,pickerCanceled,names};
    },size);
    assert.equal(result.complete.size,size);assert.equal(result.complete.hash,expected.digest('hex'));
    assert.deepEqual(result.afterAbort,result.complete,'Interrupted export changed the committed file');
    assert.equal(result.rejected,true);assert.equal(result.pickerCanceled,true);
    assert.deepEqual(result.names,['file-x0-17.bin','file-x0-17.bin']);
    console.log('PASS: real browser file streams exported 16 MiB with matching SHA-256; interrupted export aborted and preserved the committed file; picker cancellation wrote nothing. System picker itself was simulated using an isolated browser OPFS handle.');
  }finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error.message);process.exitCode=1;});
