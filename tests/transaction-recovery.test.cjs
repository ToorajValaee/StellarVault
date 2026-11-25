'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
const {webcrypto}=require('node:crypto');
const source=fs.readFileSync(path.join(__dirname,'../src/StellarVault.Pwa/wwwroot/js/vault-transaction-recovery.js'),'utf8');
const ROOT='G'+'A'.repeat(55),OTHER='G'+'B'.repeat(55),HASH='ab'.repeat(32),NET='synthetic network';
const tx={hash:()=>new Uint8Array(32).fill(0xab),toXDR:()=>Buffer.from('synthetic signed envelope').toString('base64')};
function dbHarness(){
  const entries=new Map();let reject=false;
  return {entries,setReject:v=>reject=v,
    readQueue:async k=>entries.has(k)?new Uint8Array(entries.get(k)):null,
    replaceQueue:async(k,old,next)=>{
      if(reject)throw Error('transaction aborted');
      const actual=entries.get(k);
      if(old===null?actual!==undefined:!actual||!Buffer.from(actual).equals(Buffer.from(old)))return false;
      entries.set(k,new Uint8Array(next));return true;
    },
    deleteQueueIfMatch:async(k,old)=>{
      if(reject)throw Error('transaction aborted');
      if(!entries.has(k)||!Buffer.from(entries.get(k)).equals(Buffer.from(old)))return false;
      entries.delete(k);return true;
    }};
}
function runtime(db=dbHarness()){
  let calls=0,respond=true,network=NET,receipt={hash:HASH,successful:true,ledger:42,envelope_xdr:tx.toXDR()};
  const context={window:{stellarVaultV2Db:db},crypto:webcrypto,TextEncoder,TextDecoder,Uint8Array,URL,
    setTimeout,clearTimeout,AbortController,
    fetch:async url=>new Response(JSON.stringify(url.endsWith('/')?{network_passphrase:network}:receipt),{status:respond?200:404})};
  vm.runInNewContext(source,context);
  const api=context.window.stellarVaultTransactionRecovery;
  return {api,db,setReceipt:r=>receipt=r,setNetwork:v=>network=v,setFound:v=>respond=v,get calls(){return calls;},
    async open(token='one',root=ROOT,passphrase=NET,seedByte=7){
      const server={submitTransaction:async()=>{calls++;return {hash:HASH,successful:true};}};
      await api.attach(token,'skey',root,passphrase,'https://horizon.example',
        {rawSecretKey:()=>new Uint8Array(32).fill(seedByte)},server);
      return server;
    },allow:()=>api.consent('skey',ROOT,NET,true)};
}
test('recovery requires explicit per-session consent before any storage or transport',async()=>{
  const h=runtime(),server=await h.open();
  await assert.rejects(server.submitTransaction(tx),/Allow the encrypted/);
  assert.equal(h.calls,0);assert.equal(h.db.entries.size,0);
  assert.equal((await h.api.inspectSession('skey')).Consent,false);
});
test('accepted exact envelope releases transport checkpoint without queue confirmation',async()=>{
  const h=runtime(),server=await h.open();h.allow();
  assert.equal((await server.submitTransaction(tx)).hash,HASH);
  assert.equal(h.calls,1);assert.equal(h.db.entries.size,0);
});
test('unknown receipt survives runtime restart encrypted and never replays',async()=>{
  const h=runtime(),server=await h.open();h.allow();h.setFound(false);
  await assert.rejects(server.submitTransaction(tx));assert.equal(h.calls,1);
  const bytes=[...h.db.entries.values()][0];
  assert.equal(Buffer.from(bytes).includes(Buffer.from(HASH)),false);
  assert.equal(Buffer.from(bytes).includes(Buffer.from(tx.toXDR())),false);
  const reopened=runtime(h.db),second=await reopened.open();reopened.allow();
  assert.equal((await reopened.api.inspectSession('skey')).PendingHash,HASH);
  await assert.rejects(second.submitTransaction(tx),/earlier signed/);
  assert.equal(reopened.calls,0);
  await reopened.api.verifySession('one');assert.equal(reopened.calls,0);
  assert.equal(h.db.entries.size,0);
});
test('wrong signer, corrupt ciphertext and endpoint mismatch fail closed',async()=>{
  const h=runtime(),server=await h.open();h.allow();h.setFound(false);
  await assert.rejects(server.submitTransaction(tx));
  const wrong=runtime(h.db);await wrong.open('wrong',ROOT,NET,8);
  await assert.rejects(wrong.api.inspectSession('skey'),/authenticated/);
  const key=[...h.db.entries.keys()][0],value=h.db.entries.get(key);value[value.length-1]^=1;
  await assert.rejects(h.api.inspectSession('skey'),/authenticated/);
  assert.equal(h.db.entries.size,1);
});
test('network and root recovery namespaces are separate',async()=>{
  const h=runtime(),server=await h.open();h.allow();h.setFound(false);await assert.rejects(server.submitTransaction(tx));
  const other=runtime(h.db);await other.open('other',OTHER);
  assert.equal((await other.api.inspectSession('skey')).PendingHash,null);
  const network=runtime(h.db);await network.open('other',ROOT,'other network');
  assert.equal((await network.api.inspectSession('skey')).PendingHash,null);
});
test('two tabs atomically reserve one broadcast slot',async()=>{
  const db=dbHarness(),a=runtime(db),b=runtime(db),as=await a.open(),bs=await b.open();
  a.allow();b.allow();a.setFound(false);b.setFound(false);
  const results=await Promise.allSettled([as.submitTransaction(tx),bs.submitTransaction(tx)]);
  assert.equal(results.filter(r=>r.status==='fulfilled').length,0);
  assert.equal(a.calls+b.calls,1);assert.equal(db.entries.size,1);
});
test('storage rejection prevents transport; stale CAS cannot release newer recovery state',async()=>{
  const h=runtime(),server=await h.open();h.allow();h.db.setReject(true);
  await assert.rejects(server.submitTransaction(tx));assert.equal(h.calls,0);
  h.db.setReject(false);h.setFound(false);await assert.rejects(server.submitTransaction(tx));
  h.setFound(true);const original=h.db.deleteQueueIfMatch;
  h.db.deleteQueueIfMatch=async()=>false;
  await assert.rejects(h.api.verifySession('one'),/changed/);assert.equal(h.db.entries.size,1);
  h.db.deleteQueueIfMatch=original;
});
test('failed, wrong hash, wrong envelope or invalid ledger receipts never release quarantine',async()=>{
  for(const change of [{successful:false},{hash:'cd'.repeat(32)},{envelope_xdr:'other'},{ledger:0}]){
    const h=runtime(),server=await h.open();h.allow();
    h.setReceipt({hash:HASH,successful:true,ledger:42,envelope_xdr:tx.toXDR(),...change});
    await assert.rejects(server.submitTransaction(tx));assert.equal(h.db.entries.size,1);
  }
});
test('lock revokes consent and an old transport cannot write after reconnect',async()=>{
  const h=runtime(),server=await h.open();h.allow();h.api.detach('one');
  await assert.rejects(server.submitTransaction(tx),/revoked/);assert.equal(h.calls,0);
  await h.open();assert.equal((await h.api.inspectSession('skey')).Consent,false);
});
test('only an explicit read-only review can release an exactly matching failed receipt',async()=>{
  const h=runtime(),server=await h.open();h.allow();
  h.setReceipt({hash:HASH,successful:false,ledger:42,envelope_xdr:tx.toXDR()});
  await assert.rejects(server.submitTransaction(tx));assert.equal(h.db.entries.size,1);
  const observation=await h.api.verifySession('one');
  assert.equal(observation.Successful,false);assert.equal(h.calls,1);assert.equal(h.db.entries.size,0);
});
test('endpoint network mismatch cannot release a signed transaction checkpoint',async()=>{
  const h=runtime(),server=await h.open();h.allow();h.setNetwork('unexpected network');
  await assert.rejects(server.submitTransaction(tx),/different network/);
  await assert.rejects(h.api.verifySession('one'),/different network/);
  assert.equal(h.calls,1);assert.equal(h.db.entries.size,1);
});
