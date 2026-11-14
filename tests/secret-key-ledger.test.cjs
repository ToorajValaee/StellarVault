'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const { TextEncoder, TextDecoder } = require('node:util');

const source = fs.readFileSync(path.join(__dirname,'..','src','StellarVault.Pwa',
  'wwwroot','js','vault-secret-ledger.js'),'utf8');
const ROOT='G'+'A'.repeat(55),SECRET='S'+'C'.repeat(55);
const TEST='Test SDF Network ; September 2015';
const MAIN='Public Global Stellar Network ; September 2015';
const recordId='0123456789abcdef';

function harness(passphrase=TEST) {
  const accounts=new Map();
  accounts.set(ROOT,{
    account_id:ROOT, balances:[{asset_type:'native',balance:'500.0000000',
      selling_liabilities:'0.0000000'}], data_attr:{},
    signers:[{type:'ed25519_public_key',key:ROOT,weight:1}],
    thresholds:{low_threshold:1,med_threshold:1,high_threshold:1},
    subentry_count:0,num_sponsoring:0,num_sponsored:0
  });
  let childCounter=0,submitted=0,failSubmission=false;
  const format=v=>Number(v).toFixed(7);
  class Server {
    async loadAccount(id) {
      if(!accounts.has(id))throw Error('Account not found');
      return structuredClone(accounts.get(id));
    }
    async submitTransaction(tx) {
      submitted++;
      if(failSubmission)throw Error('Simulated Horizon timeout after submission attempt');
      assert.ok(tx.signed.includes(ROOT),'All writes require root signature');
      for(const op of tx.operations) {
        const sender=accounts.get(op.source||ROOT);
        if(op.type==='createAccount') {
          assert.ok(!accounts.has(op.destination));
          accounts.set(op.destination,{account_id:op.destination,
            balances:[{asset_type:'native',balance:op.startingBalance,
              selling_liabilities:'0.0000000'}],data_attr:{},
            signers:[{type:'ed25519_public_key',key:op.destination,weight:1}],
            thresholds:{low_threshold:1,med_threshold:1,high_threshold:1},
            subentry_count:0,num_sponsoring:0,num_sponsored:0});
          sender.balances[0].balance=format(Number(sender.balances[0].balance)-Number(op.startingBalance));
        } else if(op.type==='setOptions') {
          const child=accounts.get(op.source);
          child.signers.push({type:'ed25519_public_key',key:op.signer.ed25519PublicKey,
            weight:op.signer.weight});
          child.signers[0].weight=op.masterWeight;
          child.thresholds={low_threshold:op.lowThreshold,
            med_threshold:op.medThreshold,high_threshold:op.highThreshold};
          child.subentry_count++;
        } else if(op.type==='payment') {
          const dst=accounts.get(op.destination);
          sender.balances[0].balance=format(Number(sender.balances[0].balance)-Number(op.amount));
          dst.balances[0].balance=format(Number(dst.balances[0].balance)+Number(op.amount));
        } else if(op.type==='manageData') {
          const data=sender.data_attr,existed=Object.hasOwn(data,op.name);
          if(op.value===null) {
            if(existed) {delete data[op.name];sender.subentry_count--;}
          } else {
            data[op.name]=Buffer.from(op.value).toString('base64');
            if(!existed)sender.subentry_count++;
          }
        }
      }
      return {successful:true,hash:submitted.toString(16).padStart(64,'0')};
    }
  }
  class TransactionBuilder {
    constructor(account,options) {this.operations=[];this.signed=[];this.options=options;}
    addOperation(op){this.operations.push(op);return this;}
    setTimeout(_){return this;}
    build(){return this;}
    sign(pair){this.signed.push(pair.publicKey());}
  }
  function pair(id) {
    return { publicKey:()=>id,
      rawSecretKey:()=>new Uint8Array(32).fill(7) };
  }
  const sdk={
    Keypair:{
      fromSecret:secret=>{assert.equal(secret,SECRET);return pair(ROOT);},
      random:()=>pair('G'+(++childCounter).toString(36).toUpperCase().padStart(55,'B'))
    },
    StrKey:{isValidEd25519SecretSeed:x=>x===SECRET,
      isValidEd25519PublicKey:x=>typeof x==='string'&&x.length===56&&x[0]==='G'},
    Horizon:{Server},
    TransactionBuilder,
    Asset:{native:()=>({type:'native'})},
    Operation:{
      createAccount:x=>({type:'createAccount',...x}),
      setOptions:x=>({type:'setOptions',...x}),
      payment:x=>({type:'payment',...x}),
      manageData:x=>({type:'manageData',...x})
    }
  };
  const sandbox={
    window:{StellarSdk:sdk},
    crypto:webcrypto,atob,btoa,TextEncoder,TextDecoder,URL,
    fetch:async url=>{
      if(url.endsWith('/fee_stats'))return {ok:true,json:async()=>({
        fee_charged:{p90:'100'},last_ledger_base_fee:'100'
      })};
      if(url.includes('/ledgers?'))return {ok:true,json:async()=>({_embedded:{records:[{
        base_reserve_in_stroops:5000000
      }]}})};
      return {ok:true,json:async()=>({network_passphrase:passphrase})};
    }
  };
  vm.runInNewContext(source,sandbox,{filename:'vault-secret-ledger.js'});
  const api=sandbox.window.stellarVaultSecretLedger,session='fixture';
  return {
    accounts,api,
    open:()=>api.open(session,SECRET,'https://horizon-testnet.stellar.org',passphrase),
    read:()=>api.read(session),
    plan:(kind,id,draft)=>api.plan(session,kind,id,draft),
    submit:id=>api.submit(session,id),
    inspectUncertain:()=>api.inspectUncertain(session),
    close:()=>api.close(session),
    submissions:()=>submitted,
    failNextSubmission:()=>{failSubmission=true;},
    session
  };
}
const record={Title:'My Record',Content:'Test secret',FolderId:null,Tags:['vault'],
  IsFolder:false,UserName:'example',Url:'https://example.org',Notes:'private'};

test('encrypted create, recover, update and delete have verified manifest/chunk receipts',async()=>{
  const h=harness();await h.open();
  let q=await h.plan('create',recordId,record);
  assert.equal(q.Transactions,1);
  assert.equal(h.submissions(),0);
  assert.match(await h.submit(q.QuoteId),/^[0-9a-f]{64}$/);
  let items=await h.read();
  assert.equal(items.length,1);
  assert.equal(items[0].Content,'Test secret');
  assert.ok(!JSON.stringify(h.accounts.get(ROOT).data_attr).includes('Test secret'));
  q=await h.plan('update',recordId,{...record,Content:'Edited secret'});
  await h.submit(q.QuoteId);
  items=await h.read();
  assert.equal(items[0].Content,'Edited secret');
  q=await h.plan('delete',recordId,null);
  await h.submit(q.QuoteId);
  assert.equal((await h.read()).length,0);
  h.close();
});

test('new independent child is authorized for root signer, linked and funded during write',async()=>{
  const h=harness();await h.open();
  let q=await h.plan('expand',null,null);
  assert.equal(q.CreatesChildNode,true);
  assert.equal(q.Operations,3);
  const childId=q.TargetAccountId;
  await h.submit(q.QuoteId);
  assert.equal(h.accounts.get(childId).signers.some(x=>x.key===ROOT&&x.weight===1),true);
  assert.equal(h.accounts.get(childId).signers[0].weight,0);
  q=await h.plan('create',recordId,record);
  assert.equal(q.TargetAccountId,childId);
  assert.notEqual(q.FundingTopUpXlm,'0.0000000');
  assert.ok(q.Operations>2);
  await h.submit(q.QuoteId);
  assert.equal((await h.read())[0].Content,record.Content);
  assert.ok(Number(h.accounts.get(childId).balances[0].balance)>1.5);
  h.close();
});

test('no network write without a specific approved, session-scoped review quote',async()=>{
  const h=harness();await h.open();
  await assert.rejects(h.submit('abcdef'),/matching reviewed quote/);
  const q=await h.plan('create',recordId,record);
  h.api.cancelQuote(h.session,q.QuoteId);
  await assert.rejects(h.submit(q.QuoteId),/matching reviewed quote/);
  assert.equal(h.submissions(),0);
  h.close();
});

test('mainnet read is possible but account changes are blocked',async()=>{
  const h=harness(MAIN);await h.open();
  assert.equal((await h.read()).length,0);
  await assert.rejects(h.plan('create',recordId,record),/Testnet-only/);
  await assert.rejects(h.plan('expand',null,null),/Testnet-only/);
  assert.equal(h.submissions(),0);
  h.close();
});

test('encrypted manifest or ciphertext tampering halts recovery',async()=>{
  const h=harness();await h.open();
  const q=await h.plan('create',recordId,record);await h.submit(q.QuoteId);
  const acct=h.accounts.get(ROOT);
  const chunk=Object.keys(acct.data_attr).find(x=>x.startsWith('skc:'));
  acct.data_attr[chunk]=Buffer.from('tampered').toString('base64');
  await assert.rejects(h.read(),/chunk length mismatch|checksum mismatch/);
  h.close();
});

test('HD-v2 root registries are not silently interpreted as S-key vaults',async()=>{
  const h=harness();
  h.accounts.get(ROOT).data_attr['sv:registry:v2']=Buffer.from('hd').toString('base64');
  await assert.rejects(h.open(),/HD-v2 vault root/);
  assert.equal(h.submissions(),0);
});

test('Secret Key adapter exists in both hosts and never substitutes the HD writer',()=>{
  const r=path.join(__dirname,'..');
  const pwa=fs.readFileSync(path.join(r,'src/StellarVault.Pwa/Program.cs'),'utf8');
  const native=fs.readFileSync(path.join(r,'src/StellarVault.Maui/MauiProgram.cs'),'utf8');
  const csproj=fs.readFileSync(path.join(r,'src/StellarVault.Maui/StellarVault.Maui.csproj'),'utf8');
  const home=fs.readFileSync(path.join(r,'src/StellarVault.UI/Components/VaultHome.razor'),'utf8');
  const nativeUI=fs.readFileSync(path.join(r,'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'),'utf8');
  assert.match(pwa,/IStellarSecretKeyLedger/);
  assert.match(native,/IStellarSecretKeyLedger/);
  assert.match(csproj,/BrowserSecretKeyLedger.cs/);
  assert.match(home,/SecretLedger.ConnectAsync\(_secretKey/);
  assert.match(nativeUI,/SecretLedger.ConnectAsync\(_ledgerSecretKey/);
  assert.match(nativeUI,/await SecretLedger.DisconnectAsync\(\)/);
});

test('root secret recovers encrypted records after complete session closure',async()=>{
  const h=harness();await h.open();
  const quote=await h.plan('create',recordId,record);
  await h.submit(quote.QuoteId);
  h.close();
  await h.open();
  assert.equal((await h.read())[0].Content,'Test secret');
  assert.equal(h.submissions(),1);
  h.close();
});

test('linked child with an undisclosed additional signer fails closed',async()=>{
  const h=harness();await h.open();
  const quote=await h.plan('expand',null,null);
  const child=quote.TargetAccountId;
  await h.submit(quote.QuoteId);
  h.accounts.get(child).signers.push({
    type:'ed25519_public_key',key:'G'+'E'.repeat(55),weight:1
  });
  await assert.rejects(h.read(),/another active signer/);
  h.close();
});

test('historical record names are never silently interpreted as empty new-format storage',async()=>{
  const h=harness();
  h.accounts.get(ROOT).data_attr['svh:0123456789abcdef']=Buffer.from('legacy').toString('base64');
  await assert.rejects(h.open(),/historical vault record format/);
  assert.equal(h.submissions(),0);
});

test('already full tail fails closed before storage-driven expansion',async()=>{
  const h=harness();await h.open();
  h.accounts.get(ROOT).subentry_count=1000;
  await assert.rejects(h.plan('create',recordId,record),/Manual recovery is required/);
  assert.equal(h.submissions(),0);
  h.close();
});


test('existing-record update never allocates another account on subentry ceiling',async()=>{
  const h=harness();await h.open();
  const q=await h.plan('create',recordId,record);
  await h.submit(q.QuoteId);
  h.accounts.get(ROOT).subentry_count=1000;
  await assert.rejects(h.plan('update',recordId,{...record,Content:'new content'}),
    /updating it requires a separate migration protocol/);
  assert.equal(h.submissions(),1);
  h.close();
});

test('uncertain network submission quarantines active session',async()=>{
  const h=harness();await h.open();
  const q=await h.plan('create',recordId,record);
  h.failNextSubmission();
  await assert.rejects(h.submit(q.QuoteId),/uncertain/);
  await assert.rejects(h.plan('create','fedcba9876543210',record),/unresolved/);
  await assert.rejects(h.plan('expand',null,null),/unresolved/);
  assert.equal(h.submissions(),1);
  h.close();
});


test('tail link slot is reserved before a new record exhausts the account',async()=>{
  const h=harness();await h.open();
  const root=h.accounts.get(ROOT);
  root.subentry_count=999;
  root.balances[0].balance='2000.0000000';
  await assert.rejects(h.plan('create',recordId,record),/SV_ACCOUNT_CAPACITY_REQUIRED/);
  assert.equal(h.submissions(),0);
  const expansion=await h.plan('expand',null,null);
  assert.equal(expansion.CreatesChildNode,true);
  await h.submit(expansion.QuoteId);
  const create=await h.plan('create',recordId,record);
  assert.equal(create.TargetAccountId,expansion.TargetAccountId);
  await h.submit(create.QuoteId);
  assert.equal(h.submissions(),2);
  h.close();
});

test('already exhausted tail cannot be extended blindly',async()=>{
  const h=harness();await h.open();
  h.accounts.get(ROOT).subentry_count=1000;
  await assert.rejects(h.plan('expand',null,null),/Manual recovery is required/);
  assert.equal(h.submissions(),0);
  h.close();
});


test('uncertain submission exposes only public diagnostics and never authorizes retry',async()=>{
  const h=harness();await h.open();
  const q=await h.plan('create',recordId,record);
  assert.equal(h.inspectUncertain(),null);
  h.failNextSubmission();
  await assert.rejects(h.submit(q.QuoteId),/uncertain/);
  const finding=h.inspectUncertain();
  assert.equal(finding.Kind,'create');
  assert.equal(finding.RootAccountId,ROOT);
  assert.equal(finding.TargetAccountId,ROOT);
  assert.equal(finding.RecordId,recordId);
  assert.equal(finding.TransactionHash,null);
  assert.equal(finding.NetworkPassphrase,TEST);
  assert.equal(finding.HorizonUrl,'https://horizon-testnet.stellar.org');
  assert.ok(!JSON.stringify(finding).includes(record.Content));
  finding.RecordId='attacker-edited'; // The diagnostic is a defensive copy.
  assert.equal(h.inspectUncertain().RecordId,recordId);
  await assert.rejects(h.plan('expand',null,null),/unresolved/);
  h.close();
});

test('success clears the read-only uncertain snapshot',async()=>{
  const h=harness();await h.open();
  const quote=await h.plan('create',recordId,record);
  await h.submit(quote.QuoteId);
  assert.equal(h.inspectUncertain(),null);
  h.close();
});


test('uncertain write quarantine and diagnostic survive lock then reopen in same runtime',async()=>{
  const h=harness();await h.open();
  const quote=await h.plan('create',recordId,record);
  h.failNextSubmission();
  await assert.rejects(h.submit(quote.QuoteId),/uncertain/);
  const original=h.inspectUncertain();
  h.close();
  await h.open();
  const reopened=h.inspectUncertain();
  assert.equal(reopened.Kind,'create');
  assert.equal(reopened.RootAccountId,ROOT);
  assert.equal(reopened.RecordId,recordId);
  assert.equal(reopened.TransactionHash,null);
  assert.equal(reopened.HorizonUrl,'https://horizon-testnet.stellar.org');
  assert.deepEqual(JSON.parse(JSON.stringify(reopened)),JSON.parse(JSON.stringify(original)));
  assert.equal((await h.read()).length,0,'Read-only inventory remains available');
  await assert.rejects(h.plan('create','fedcba9876543210',record),/unresolved/);
  await assert.rejects(h.plan('expand',null,null),/unresolved/);
  assert.equal(h.submissions(),1);
  h.close();
});


test('historical receipt adapter is strictly read-only and registered in both hosts',()=>{
  const root=path.join(__dirname,'..');
  const inspector=fs.readFileSync(path.join(root,'src','StellarVault.Pwa','Services',
    'HorizonVaultLinkedReceiptEvidence.cs'),'utf8');
  const pwa=fs.readFileSync(path.join(root,'src','StellarVault.Pwa','Program.cs'),'utf8');
  const maui=fs.readFileSync(path.join(root,'src','StellarVault.Maui','MauiProgram.cs'),'utf8');
  const csproj=fs.readFileSync(path.join(root,'src','StellarVault.Maui','StellarVault.Maui.csproj'),'utf8');
  assert.match(inspector,/network\.InspectAsync/);
  assert.match(inspector,/transactions\.InspectAsync/);
  assert.match(inspector,/receipt\.SourceAccount != diagnostic\.RootAccountId/);
  assert.doesNotMatch(inspector,/SubmitApprovedAsync|submitTransaction|QuoteCreateAsync/);
  assert.match(pwa,/IVaultLinkedReceiptEvidence, HorizonVaultLinkedReceiptEvidence/);
  assert.match(maui,/IVaultLinkedReceiptEvidence, HorizonVaultLinkedReceiptEvidence/);
  assert.match(csproj,/HorizonVaultLinkedReceiptEvidence\.cs/);
});


test('uncertain write blocks a second previously opened session on the same vault',async()=>{
  const h=harness();await h.open();
  const other='parallel-session';
  await h.api.open(other,SECRET,'https://horizon-testnet.stellar.org',TEST);
  const first=await h.plan('create',recordId,record);
  const second=await h.api.plan(other,'create','fedcba9876543210',record);
  h.failNextSubmission();
  await assert.rejects(h.submit(first.QuoteId),/uncertain/);
  const siblingFinding=h.api.inspectUncertain(other);
  assert.equal(siblingFinding.Kind,'create');
  assert.equal(siblingFinding.RootAccountId,ROOT);
  assert.equal(siblingFinding.RecordId,recordId);
  await assert.rejects(
    h.api.plan(other,'expand',null,null),/unresolved for this vault/);
  await assert.rejects(
    h.api.submit(other,second.QuoteId),/requires reconciliation/);
  assert.equal(h.submissions(),1,'No second broadcast must occur');
  h.api.close(other);h.close();
});


test('simultaneous approved submissions in one runtime cannot both reach Horizon after uncertainty',async()=>{
  const h=harness();await h.open();
  const other='second-approved-session';
  await h.api.open(other,SECRET,'https://horizon-testnet.stellar.org',TEST);
  const first=await h.plan('create',recordId,record);
  const second=await h.api.plan(other,'create','fedcba9876543210',record);
  h.failNextSubmission();
  const outcomes=await Promise.allSettled([
    h.submit(first.QuoteId),
    h.api.submit(other,second.QuoteId)
  ]);
  assert.equal(outcomes.filter(x=>x.status==='rejected').length,2);
  assert.equal(h.submissions(),1,'Only one signed transaction can reach the transport');
  h.api.close(other);h.close();
});

test('HD and independent linked approval workflows reuse the same scope freshness policy',()=>{
  const root=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const shared=fs.readFileSync(path.join(root,'VaultApprovalScopeGuard.cs'),'utf8');
  const hd=fs.readFileSync(path.join(root,'VaultSyncApprovalGuard.cs'),'utf8');
  const linked=fs.readFileSync(path.join(root,'VaultLinkedAccountExpansion.cs'),'utf8');
  assert.match(shared,/quotedNetworkScope/);
  assert.match(shared,/quotedUtc > now/);
  assert.match(shared,/now - quotedUtc > maxAge/);
  assert.match(hd,/VaultApprovalScopeGuard\.RequireFresh/);
  assert.match(linked,/VaultApprovalScopeGuard\.RequireFresh/);
  assert.match(linked,/reviewedQuote != pending\.Quote/);
  assert.match(linked,/TimeSpan\.FromMinutes\(2\)/);
  assert.match(linked,/ledger\.SubmitApprovedAsync\(pending\.Quote\.QuoteId/);
  assert.doesNotMatch(shared,/secretKey|mnemonic|submitTransaction|SubmitApprovedAsync/);
});

test('both hosts reuse the existing consent-bound queue review without S-key publication',()=>{
  const root=path.join(__dirname,'..');
  const core=path.join(root,'src','StellarVault.Core','Services');
  const shared=fs.readFileSync(path.join(core,'VaultScopedQueueReview.cs'),'utf8');
  const linked=fs.readFileSync(path.join(core,'VaultLinkedQueueInspection.cs'),'utf8');
  const hd=fs.readFileSync(path.join(core,'VaultHybridSyncReviewService.cs'),'utf8');
  const pwa=fs.readFileSync(path.join(root,'src','StellarVault.Pwa','Program.cs'),'utf8');
  const maui=fs.readFileSync(path.join(root,'src','StellarVault.Maui','MauiProgram.cs'),'utf8');
  assert.match(shared,/retention\.RequireLocalPersistence\(\)/);
  assert.match(shared,/sessions\.ActiveAccountId != accountId/);
  assert.match(shared,/VaultPendingMutationReviewPolicy\.Build\(all\)/);
  assert.match(shared,/queue\.ReviewAllAsync/);
  assert.match(shared,/VaultHybridFolderSyncPlan\.Build/);
  assert.match(shared,/sessions\.Epoch != epoch/);
  assert.match(linked,/store\.ListAsync\(root, scope, ct\)/);
  assert.match(linked,/CanPublish/);
  assert.match(linked,/\b0,\s*linkedCount,\s*false,/);
  assert.doesNotMatch(linked,/SubmitApprovedAsync|QuoteCreateAsync|PublishAsync/);
  assert.match(hd,/scopedQueue\.ReadAsync\(accountId, ct\)/);
  for(const host of [pwa,maui]){
    assert.match(host,/AddScoped<VaultScopedQueueReview>/);
    assert.match(host,/AddScoped<VaultLinkedQueueInspection>/);
  }
});


test('encrypted queue origin is versioned, immutable and not implicitly upgraded to S-key',()=>{
  const root=path.join(__dirname,'..','src');
  const core=path.join(root,'StellarVault.Core','Services');
  const pwa=path.join(root,'StellarVault.Pwa','Services');
  const policy=fs.readFileSync(path.join(core,'VaultMutationStrategyPolicy.cs'),'utf8');
  const queue=fs.readFileSync(path.join(core,'VaultPendingMutationQueue.cs'),'utf8');
  const hd=fs.readFileSync(path.join(core,'VaultHybridSyncReviewService.cs'),'utf8');
  const guard=fs.readFileSync(path.join(core,'VaultSyncApprovalGuard.cs'),'utf8');
  const linked=fs.readFileSync(path.join(core,'VaultLinkedQueueInspection.cs'),'utf8');
  const native=fs.readFileSync(path.join(core,'FileSystemVaultPendingMutationStore.cs'),'utf8');
  const browser=fs.readFileSync(path.join(pwa,'IndexedDbVaultPendingMutationStore.cs'),'utf8');
  assert.match(policy,/null or Hd => Hd/,'legacy null tag must remain HD');
  assert.match(policy,/Linked => Linked/);
  assert.match(queue,/string\? OriginStrategyId = null/,'legacy serialized entries must be readable');
  assert.match(queue,/VaultMutationStrategyPolicy\.RequireHd\(entry\)/);
  assert.match(guard,/VaultMutationStrategyPolicy\.RequireHd\(mutation\)/);
  assert.match(guard,/VaultMutationStrategyPolicy\.RequireHd\(current\)/);
  assert.match(guard,/VaultMutationStrategyPolicy\.Resolve\(item\.OriginStrategyId\)/);
  assert.match(hd,/Mixed account-strategy queue/);
  assert.match(linked,/LegacyHdEntries, int LinkedTaggedEntries/);
  assert.match(linked,/0, linkedCount, false/);
  for (const store of [native,browser]) {
    assert.match(store,/previous\.OriginStrategyId != replacement\.OriginStrategyId/);
    assert.match(store,/a\.OriginStrategyId == b\.OriginStrategyId/);
    assert.match(store,/VaultMutationStrategyPolicy\.Validate\(item\.OriginStrategyId\)/);
  }
});


test('existing encrypted queue stores delegate transient key use to the shared session interface',()=>{
  const root=path.join(__dirname,'..','src');
  const core=path.join(root,'StellarVault.Core','Services');
  const pwa=path.join(root,'StellarVault.Pwa','Services');
  const seam=fs.readFileSync(path.join(core,'IVaultQueueKeySession.cs'),'utf8');
  const native=fs.readFileSync(path.join(core,'FileSystemVaultPendingMutationStore.cs'),'utf8');
  const browser=fs.readFileSync(path.join(pwa,'IndexedDbVaultPendingMutationStore.cs'),'utf8');
  const queue=fs.readFileSync(path.join(core,'VaultPendingMutationQueue.cs'),'utf8');
  const pwaHost=fs.readFileSync(path.join(root,'StellarVault.Pwa','Program.cs'),'utf8');
  const mauiHost=fs.readFileSync(path.join(root,'StellarVault.Maui','MauiProgram.cs'),'utf8');
  assert.match(seam,/interface IVaultQueueKeySession/);
  assert.match(seam,/class VaultLocalQueueKeySession\(VaultSessionManager sessions\)/);
  assert.match(seam,/sessions\.ExecuteAsync/);
  assert.match(seam,/sessions\.ActiveAccountId != accountId/);
  assert.match(native,/IVaultQueueKeySession keySession/);
  assert.match(browser,/IVaultQueueKeySession keySession/);
  assert.match(native,/keySession\.ExecuteAsync\(accountId, networkId/);
  assert.match(browser,/keySession\.ExecuteAsync\(accountId, networkId/);
  assert.match(native,/retention\.RequireLocalPersistence\(\)/);
  assert.match(browser,/retention\.RequireLocalPersistence\(\)/);
  assert.match(queue,/VaultMutationStrategyPolicy\.RequireHd\(item\)/);
  assert.match(queue,/a\.OriginStrategyId == b\.OriginStrategyId/);
  assert.match(queue,/VaultMutationStrategyPolicy\.Resolve\(x\.OriginStrategyId\) == VaultMutationStrategyPolicy\.Hd/);
  for (const host of [pwaHost,mauiHost]) {
    assert.match(host,/AddScoped<IVaultQueueKeySession, VaultLocalQueueKeySession>/);
  }
});


test('queue readiness and folder graph use one authenticated mutation snapshot',()=>{
  const root=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const scoped=fs.readFileSync(path.join(root,'VaultScopedQueueReview.cs'),'utf8');
  const queue=fs.readFileSync(path.join(root,'VaultPendingMutationQueue.cs'),'utf8');
  const policy=fs.readFileSync(path.join(root,'VaultPendingMutationReviewPolicy.cs'),'utf8');
  assert.match(scoped,/var all = await queue\.ReviewAllAsync\(accountId, scope, ct\)/);
  assert.match(scoped,/VaultPendingMutationReviewPolicy\.Build\(all\)/);
  assert.match(scoped,/VaultHybridFolderSyncPlan\.Build\(all, review\)/);
  assert.doesNotMatch(scoped,/queue\.PrepareReviewAsync\(/);
  assert.match(queue,/return VaultPendingMutationReviewPolicy\.Build\(all\)/);
  assert.match(policy,/VaultMutationStrategyPolicy\.Resolve\(item\.OriginStrategyId\)/);
  assert.match(policy,/Independent linked-key queue intent cannot be published/);
  assert.match(policy,/VaultQueuedCreatePolicy\.Problems\(item, all\)/);
});


test('existing tail record update cannot consume the reserved next-account link slot',async()=>{
  const h=harness();await h.open();
  const created=await h.plan('create',recordId,record);
  await h.submit(created.QuoteId);
  h.accounts.get(ROOT).subentry_count=998;
  await assert.rejects(h.plan('update',recordId,{...record,Content:'Changed secret'}),
    /updating it requires a separate migration protocol/);
  assert.equal(h.submissions(),1,'No second network write is broadcast');
  h.close();
});

test('linked expansion preflight cancellation is structurally distinct from transport uncertainty',()=>{
  const root=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const expansion=fs.readFileSync(path.join(root,'VaultLinkedAccountExpansion.cs'),'utf8');
  const ui=fs.readFileSync(path.join(__dirname,'..','src','StellarVault.UI','Components',
    'VaultSecretKeyWorkspace.razor'),'utf8');
  const begin=expansion.indexOf('catch (Exception ex)');
  const submit=expansion.indexOf('var hash = await ledger.SubmitApprovedAsync');
  assert.ok(begin>0 && submit>begin,'Preflight error must precede transport');
  assert.match(expansion,/VaultLinkedExpansionPreflightException/);
  assert.match(expansion,/await TryCancelUnsubmittedAsync\(pending\.Quote\.QuoteId\)/);
  assert.match(expansion,/\/\/ The preflight quote is single-use/);
  assert.match(ui,/catch \(VaultLinkedExpansionPreflightException\)/);
  assert.match(ui,/await Ledger\.InspectUncertainSubmissionAsync\(\)/);
  assert.match(ui,/No expansion transaction was submitted for this review/);
  assert.match(expansion,/public async Task AbandonUnsubmittedAsync\(CancellationToken ct = default\)/);
  assert.match(ui,/await LinkedExpansion\.AbandonUnsubmittedAsync\(\)/);
  assert.match(ui,/catch\s*\{\s*_capacityRolloverPending = false;\s*_submissionUncertain = true;/);
});


test('linked vault cannot silently treat one session sibling uncertainty as safe to unlock',async()=>{
  const h=harness();await h.open();
  const parallel='read-only-parallel';
  await h.api.open(parallel,SECRET,'https://horizon-testnet.stellar.org',TEST);
  const quote=await h.plan('create',recordId,record);
  h.failNextSubmission();
  await assert.rejects(h.submit(quote.QuoteId),/uncertain/);
  const snapshot=h.api.inspectUncertain(parallel);
  assert.equal(snapshot.RootAccountId,ROOT);
  assert.equal(snapshot.RecordId,recordId);
  assert.equal(snapshot.NetworkPassphrase,TEST);
  snapshot.Kind='updated-by-user';
  assert.equal(h.api.inspectUncertain(parallel).Kind,'create');
  await assert.rejects(h.api.plan(parallel,'expand',null,null),/unresolved/);
  h.api.close(parallel);h.close();
});


test('independent S-key local queue key and ciphertext namespaces never alias HD storage',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const browser=path.join(src,'StellarVault.Pwa','Services');
  const scope=fs.readFileSync(path.join(core,'VaultLinkedQueueStorageScope.cs'),'utf8');
  const session=fs.readFileSync(path.join(core,'VaultLinkedQueueKeySession.cs'),'utf8');
  const credentialPwa=fs.readFileSync(path.join(browser,'IndexedDbVaultLinkedPassphraseCredentialStore.cs'),'utf8');
  const credentialNative=fs.readFileSync(path.join(core,'FileSystemVaultLinkedPassphraseCredentialStore.cs'),'utf8');
  const snapshotPwa=fs.readFileSync(path.join(browser,'IndexedDbVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const snapshotNative=fs.readFileSync(path.join(core,'FileSystemVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const inspector=fs.readFileSync(path.join(core,'VaultLinkedQueueInspection.cs'),'utf8');
  const hostPwa=fs.readFileSync(path.join(src,'StellarVault.Pwa','Program.cs'),'utf8');
  const hostNative=fs.readFileSync(path.join(src,'StellarVault.Maui','MauiProgram.cs'),'utf8');
  assert.match(scope,/secret-chain:queue:v1:/);
  assert.match(scope,/interface IVaultLinkedQueueSnapshotStore/);
  assert.match(session,/VaultPassphraseLifecycle _credentials/);
  assert.match(session,/IVaultLinkedPassphraseCredentialStore credentialStore/);
  assert.match(session,/linkedRetention\.DeviceRetention\.RequireLocalPersistence\(\)/);
  assert.match(session,/ledger\.InspectAccountInfoAsync\(ct\)/);
  assert.match(session,/var before = await ledger\.InspectAccountInfoAsync\(ct\)/);
  assert.match(session,/var after = await ledger\.InspectAccountInfoAsync\(ct\)/);
  assert.match(session,/after\.TailAccountId != before\.TailAccountId/);
  assert.match(session,/VaultLinkedQueueStorageScope\.FromNetworkScope\(_scope\)/);
  assert.match(session,/CryptographicOperations\.ZeroMemory\(_key\)/);
  assert.match(session,/CryptographicOperations\.ZeroMemory\(copy\)/);
  assert.match(session,/return entry is not null/);
  assert.match(credentialPwa,/secret-chain:queue-key:v1:/);
  assert.match(credentialPwa,/linkedRetention\.DeviceRetention\.RequireLocalPersistence\(\)/);
  assert.match(credentialNative,/secret-chain:queue-key:v1:/);
  assert.match(credentialNative,/FileSystemVaultPassphraseCredentialStore/);
  assert.match(snapshotPwa,/new\(js, keys, linkedRetention\.DeviceRetention\)/);
  assert.match(snapshotNative,/new\(keys, linkedRetention\.DeviceRetention, directory\)/);
  for(const value of [snapshotPwa,snapshotNative]){
    assert.match(value,/VaultLinkedQueueStorageScope\.FromNetworkScope\(linkedNetworkScope\)/);
    assert.match(value,/VaultMutationStrategyPolicy\.Linked/);
  }
  assert.match(inspector,/IVaultLinkedQueueSnapshotStore store/);
  assert.match(inspector,/store\.ListAsync\(root, scope, ct\)/);
  assert.match(inspector,/0, linkedCount, false/);
  assert.doesNotMatch(inspector,/VaultScopedQueueReview|VaultSessionManager|SubmitApprovedAsync/);
  assert.match(hostPwa,/IndexedDbVaultLinkedPassphraseCredentialStore/);
  assert.match(hostPwa,/IndexedDbVaultLinkedQueueSnapshotStore/);
  assert.match(hostNative,/FileSystemVaultLinkedPassphraseCredentialStore/);
  assert.match(hostNative,/FileSystemVaultLinkedQueueSnapshotStore/);
  assert.match(hostNative,/vault-secret-chain-pending-queue/);
  assert.match(hostNative,/vault-secret-chain-credentials/);
});

test('linked queue key is revoked on PWA and MAUI app lifecycle boundaries',()=>{
  const src=path.join(__dirname,'..','src');
  const pwa=fs.readFileSync(path.join(src,'StellarVault.Pwa','App.razor'),'utf8');
  const native=fs.readFileSync(path.join(src,'StellarVault.UI','Components','NativeVaultWorkspace.razor'),'utf8');
  const linked=fs.readFileSync(path.join(src,'StellarVault.UI','Components','VaultSecretKeyWorkspace.razor'),'utf8');
  const transport=fs.readFileSync(path.join(src,'StellarVault.Pwa','Services','BrowserSecretKeyLedger.cs'),'utf8');
  const interfaceSource=fs.readFileSync(path.join(src,'StellarVault.Core','Services','IStellarSecretKeyLedger.cs'),'utf8');
  assert.match(interfaceSource,/ConnectedNetworkPassphrase/);
  assert.match(transport,/ConnectedNetworkPassphrase => _connected/);
  assert.match(pwa,/await LinkedQueueKeys\.LockAsync\(\)/);
  assert.match(native,/await LinkedQueueKeys\.LockAsync\(\)/);
  assert.match(linked,/await LinkedQueueKeys\.LockAsync\(\)/);
});


test('linked S-key device UI requires consent and separate fee-reviewed publication',()=>{
  const root=path.join(__dirname,'..','src','StellarVault.UI','Components');
  const panel=fs.readFileSync(path.join(root,'VaultLinkedLocalQueuePanel.razor'),'utf8');
  const workspace=fs.readFileSync(path.join(root,'VaultSecretKeyWorkspace.razor'),'utf8');
  const native=fs.readFileSync(path.join(root,'NativeVaultWorkspace.razor'),'utf8');
  assert.match(panel,/I consent to encrypted local storage on this device/);
  assert.match(panel,/LinkedRetention\.DeviceRetention\.AllowsLocalPersistence/);
  assert.match(panel,/LinkedRetention\.GrantExplicitConsent\(\)/);
  assert.match(panel,/LinkedRetention\.Reset\(\)/);
  assert.match(panel,/checked="@LinkedRetention\.AllowsLocalPersistence"/);
  assert.match(panel,/Retention\.Reset\(\)/);
  assert.match(panel,/minlength="12"/);
  assert.match(panel,/QueueKeys\.HasLocalCredentialsAsync\(\)/);
  assert.match(panel,/QueueKeys\.CreateAsync\(_passphrase\)/);
  assert.match(panel,/QueueKeys\.UnlockAsync\(_passphrase\)/);
  assert.match(panel,/QueueKeys\.LockAsync\(\)/);
  assert.match(panel,/QueueInspection\.InspectAsync\(\)/);
  assert.match(panel,/Publisher\.PrepareAsync\(id\)/);
  assert.match(panel,/Publisher\.SubmitAsync\(approved\.Quote, true\)/);
  assert.doesNotMatch(panel,/Ledger\.SubmitApprovedAsync|Ledger\.QuoteCreateAsync/);
  assert.match(workspace,/<VaultLinkedLocalQueuePanel \/>/);
  assert.match(native,/<VaultSecretKeyWorkspace \/>/);
});


test('independent S-key local retention is not granted by mnemonic-HD consent',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const pwa=path.join(src,'StellarVault.Pwa','Services');
  const gate=fs.readFileSync(path.join(core,'VaultLinkedLocalRetentionGate.cs'),'utf8');
  const session=fs.readFileSync(path.join(core,'VaultLinkedQueueKeySession.cs'),'utf8');
  const storePwa=fs.readFileSync(path.join(pwa,'IndexedDbVaultLinkedPassphraseCredentialStore.cs'),'utf8');
  const storeNative=fs.readFileSync(path.join(core,'FileSystemVaultLinkedPassphraseCredentialStore.cs'),'utf8');
  const queuePwa=fs.readFileSync(path.join(pwa,'IndexedDbVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const queueNative=fs.readFileSync(path.join(core,'FileSystemVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const inspector=fs.readFileSync(path.join(core,'VaultLinkedQueueInspection.cs'),'utf8');
  const pwaHost=fs.readFileSync(path.join(src,'StellarVault.Pwa','Program.cs'),'utf8');
  const mauiHost=fs.readFileSync(path.join(src,'StellarVault.Maui','MauiProgram.cs'),'utf8');
  assert.match(gate,/private set/);
  assert.match(gate,/GrantExplicitConsent/);
  assert.match(gate,/RequireLocalPersistence/);
  assert.match(gate,/DeviceRetention \{ get; \} = new\(\)/);
  assert.match(gate,/DeviceRetention\.Select\(VaultDeviceRetentionPolicy\.EncryptedLocalWithManualSync\)/);
  assert.match(gate,/DeviceRetention\.Reset\(\)/);
  assert.doesNotMatch(session,/VaultDeviceRetentionGate retention/);
  assert.doesNotMatch(pwaHost,/AddScoped<IVaultLinkedQueueKeySession>/);
  for(const file of [session,storePwa,storeNative,queuePwa,queueNative,inspector])
    assert.match(file,/linkedRetention\.RequireLocalPersistence\(\)/);
  for(const host of [pwaHost,mauiHost])
    assert.match(host,/AddScoped<VaultLinkedLocalRetentionGate>/);
  assert.match(mauiHost,/GetRequiredService<VaultLinkedLocalRetentionGate>/);
  assert.match(session,/ledger\.ConnectionEpoch/);
  assert.match(session,/current\.Epoch != epoch/);
  const ledger=fs.readFileSync(path.join(pwa,'BrowserSecretKeyLedger.cs'),'utf8');
  assert.match(ledger,/_connectionEpoch\+\+/);
  assert.match(ledger,/ConnectionEpoch => _connectionEpoch/);
});

test('GitHub verification is manual-only and cannot consume Actions minutes on pushes',()=>{
  const root=path.join(__dirname,'..','.github','workflows');
  assert.ok(fs.existsSync(root),'Manual verification workflow is missing');
  const workflows=fs.readdirSync(root).filter(x=>/\.ya?ml$/.test(x));
  assert.deepEqual(workflows,['manual-verify.yml']);
  const content=fs.readFileSync(path.join(root,workflows[0]),'utf8');
  assert.match(content,/workflow_dispatch:/);
  assert.doesNotMatch(content,/^\s*push:/m);
  assert.doesNotMatch(content,/^\s*pull_request:/m);
  assert.doesNotMatch(content,/^\s*-\s*name:\s*(Deploy|Publish release|Broadcast)/mi);
  assert.doesNotMatch(content,/^\s*push:|^\s*pull_request:/m);
});


test('independent linked staging reuses original encrypted stores and parent dependency rules',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const pwa=path.join(src,'StellarVault.Pwa','Services');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const interfaceSource=fs.readFileSync(path.join(core,'VaultLinkedQueueStorageScope.cs'),'utf8');
  const browserStore=fs.readFileSync(path.join(pwa,'IndexedDbVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const nativeStore=fs.readFileSync(path.join(core,'FileSystemVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const panel=fs.readFileSync(path.join(src,'StellarVault.UI','Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  assert.match(interfaceSource,/interface IVaultLinkedDraftStore : IVaultLinkedQueueSnapshotStore/);
  assert.match(workspace,/VaultPendingMutation\.Create\(/);
  assert.match(workspace,/OriginStrategyId = VaultMutationStrategyPolicy\.Linked/);
  assert.match(workspace,/VaultQueuedCreatePolicy\.ParentCandidates/);
  assert.match(workspace,/deps = \[parent\.Id\]/);
  assert.match(workspace,/store\.AddAsync\(item, ct\)/);
  assert.match(workspace,/store\.TryDeleteAsync\(item, ct\)/);
  assert.match(workspace,/Cannot|Discard dependent local drafts before deleting their parent folder/);
  assert.match(workspace,/CryptographicOperations\.ZeroMemory\(item\.EncryptedPayload\)/);
  for(const adapter of [browserStore,nativeStore]){
    assert.match(adapter,/IVaultLinkedDraftStore/);
    assert.match(adapter,/linkedRetention\.RequireLocalPersistence\(\)/);
    assert.match(adapter,/VaultLinkedQueueStorageScope\.FromNetworkScope/);
    assert.match(adapter,/ExistingStore\(\)\.AddAsync\(entry, ct\)/);
    assert.match(adapter,/ExistingStore\(\)\.TryDeleteAsync\(expected, ct\)/);
    assert.match(adapter,/ExistingStore\(\)\.TryReplaceAsync\(previous, next, ct\)/);
  }
  assert.match(panel,/DraftWorkspace\.CreateAsync\(draft\)/);
  assert.match(panel,/DraftWorkspace\.ListAsync\(\)/);
  assert.match(panel,/DraftWorkspace\.DiscardAsync\(id\)/);
  assert.match(panel,/Save encrypted local draft/);
});

test('manual linked draft publication reserves durable queue state before network broadcast',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const panel=fs.readFileSync(path.join(src,'StellarVault.UI','Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const pwa=fs.readFileSync(path.join(src,'StellarVault.Pwa','Program.cs'),'utf8');
  const native=fs.readFileSync(path.join(src,'StellarVault.Maui','MauiProgram.cs'),'utf8');
  const reserve=publisher.indexOf('store.TryReplaceAsync(original, reserved, ct)');
  const broadcast=publisher.indexOf('ledger.SubmitApprovedAsync(pending.Quote.QuoteId, ct)');
  assert.ok(reserve>=0 && broadcast>reserve);
  assert.match(publisher,/pending\.Quote, approvedQuote/);
  assert.match(publisher,/VaultQueuedCreatePolicy\.Problems/);
  assert.match(publisher,/CryptographicOperations\.FixedTimeEquals/);
  assert.match(publisher,/Fingerprint\(original\)/);
  assert.match(publisher,/State = VaultSyncState\.Submitting/);
  assert.match(publisher,/State = VaultSyncState\.Uncertain/);
  assert.doesNotMatch(publisher,/State = VaultSyncState\.Confirmed/);
  assert.match(publisher,/Current account snapshot does not prove|matching CURRENT account snapshot/);
  assert.match(publisher,/ledger\.ReadAsync\(ct\)/);
  assert.match(publisher,/store\.TryReplaceAsync\(reserved, uncertain, CancellationToken\.None\)/);
  assert.doesNotMatch(publisher,/VaultSessionManager|MnemonicHd|StellarHdAccountStrategy/);
  assert.match(panel,/Publisher\.PrepareAsync\(id\)/);
  assert.match(panel,/Publisher\.SubmitAsync\(approved\.Quote, true\)/);
  assert.match(panel,/Publisher\.CancelAsync\(\)/);
  assert.match(panel,/I approve this specific Stellar transaction/);
  for(const host of [pwa,native]){
    assert.match(host,/AddScoped<VaultLinkedDraftWorkspace>/);
    assert.match(host,/AddScoped<VaultLinkedDraftPublication>/);
  }
});


test('native Core smoke suite includes independent linked encrypted staging and uncertain network scenario',()=>{
  const root=path.join(__dirname,'..','tests','StellarVault.Core.CryptoTests');
  const smoke=fs.readFileSync(path.join(root,'LinkedDraftSmoke.cs'),'utf8');
  const program=fs.readFileSync(path.join(root,'Program.cs'),'utf8');
  assert.match(program,/await LinkedDraftSmoke\.RunAsync\(\)/);
  assert.match(smoke,/FileSystemVaultLinkedQueueSnapshotStore/);
  assert.match(smoke,/VaultLinkedDraftWorkspace/);
  assert.match(smoke,/VaultLinkedDraftPublication/);
  assert.match(smoke,/VaultLinkedLocalRetentionGate/);
  assert.match(smoke,/publisher\.SubmitAsync\(folderReview\.Quote, true\)/);
  assert.match(smoke,/publisher\.SubmitAsync\(recordReview\.Quote, true\)/);
  assert.match(smoke,/VaultSyncState\.Confirmed/);
  assert.match(smoke,/VaultSyncState\.Uncertain or VaultSyncState\.Submitting/);
  assert.match(smoke,/MustRejectAsync\(\(\) => publisher\.PrepareAsync\(pending\.MutationId\)/);
  assert.match(smoke,/keys\.LockAsync\(\)/);
});


test('independent S-key local storage never turns on the HD device-retention gate',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const panel=fs.readFileSync(path.join(src,'StellarVault.UI','Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const gate=fs.readFileSync(path.join(core,'VaultLinkedLocalRetentionGate.cs'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,'..','tests','StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(panel,/LinkedRetention\.GrantExplicitConsent\(\)/);
  assert.match(panel,/LinkedRetention\.Reset\(\)/);
  assert.doesNotMatch(panel,/@inject VaultDeviceRetentionGate/);
  assert.doesNotMatch(panel,/Retention\.Select\(VaultDeviceRetentionPolicy/);
  assert.match(gate,/public VaultDeviceRetentionGate DeviceRetention \{ get; \} = new\(\)/);
  assert.match(smoke,/hdRetention\.Policy == VaultDeviceRetentionPolicy\.OnChainOnly/);
});


test('linked draft recovery remains read-only even when current record or receipt is visible',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const recovery=fs.readFileSync(path.join(core,'VaultLinkedDraftRecoveryReview.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const panel=fs.readFileSync(path.join(src,'StellarVault.UI','Components',
    'VaultLinkedLocalQueuePanel.razor'),'utf8');
  const test=fs.readFileSync(path.join(__dirname,'..','tests',
    'StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(recovery,/VaultSyncState\.Submitting or VaultSyncState\.Uncertain/);
  assert.match(recovery,/CurrentRecordMatches/);
  assert.match(recovery,/HistoricalReceiptObserved/);
  assert.match(recovery,/receipts\.InspectAsync\(diagnostic, ct\)/);
  assert.match(recovery,/CryptographicOperations\.ZeroMemory\(entry\.EncryptedPayload\)/);
  assert.doesNotMatch(recovery,/State\s*=\s*VaultSyncState\.Confirmed|store\.TryReplaceAsync|SubmitApprovedAsync/);
  assert.match(panel,/RecoveryReview\.InspectAsync\(id\)/);
  assert.match(panel,/Assess unresolved \(read-only\)/);
  assert.match(publisher,/if \(!Equals\(pending\.Quote, approvedQuote\)\)/);
  assert.match(publisher,/CryptographicOperations\.ZeroMemory\(pending\.Fingerprint\)/);
  assert.match(publisher,/await ledger\.CancelQuoteAsync\(quote\.QuoteId\)/);
  assert.match(test,/new VaultLinkedDraftRecoveryReview\(/);
  assert.match(test,/finding\.CurrentRecordMatches is false/);
  assert.match(test,/NetworkFeeXlm = "999\.0000000"/);
  for(const file of ['StellarVault.Pwa/Program.cs','StellarVault.Maui/MauiProgram.cs']){
    const host=fs.readFileSync(path.join(src,...file.split('/')),'utf8');
    assert.match(host,/AddScoped<VaultLinkedDraftRecoveryReview>/);
  }
});


test('existing S-key credentials unlock local encrypted drafts without Horizon or signing authority',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const pwaPage=fs.readFileSync(path.join(src,'StellarVault.Pwa','Pages','SecretVault.razor'),'utf8');
  const native=fs.readFileSync(path.join(src,'StellarVault.UI','Components','NativeVaultWorkspace.razor'),'utf8');
  const panel=fs.readFileSync(path.join(src,'StellarVault.UI','Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const session=fs.readFileSync(path.join(core,'VaultLinkedQueueKeySession.cs'),'utf8');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const inspector=fs.readFileSync(path.join(core,'VaultLinkedQueueInspection.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,'..','tests','StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(session,/public async Task UnlockOfflineAsync\(string publicRoot, string localPassphrase/);
  assert.match(session,/public async Task<bool> HasLocalCredentialsOfflineAsync\(string publicRoot/);
  assert.match(session,/public bool IsOfflineOnly => IsUnlocked && _offlineOnly/);
  assert.match(session,/public string\? ActiveRootAccountId => IsUnlocked \? _root : null/);
  assert.match(session,/if \(_offlineOnly\)/);
  assert.match(session,/MatchesSelectedNetwork\(\)/);
  assert.match(session,/public bool CanPublish => IsUnlocked && !_offlineOnly/);
  assert.match(session,/localResult = await operation\(copy, ct\)/);
  assert.match(workspace,/IReadOnlyList<LedgerRecordItem> remote = keys\.IsOfflineOnly/);
  assert.match(workspace,/keys\.ActiveRootAccountId/);
  assert.match(inspector,/if \(!offline\)/);
  assert.match(panel,/QueueKeys\.UnlockOfflineAsync\(_offlineRoot\.Trim\(\), _passphrase\)/);
  assert.match(panel,/QueueKeys\.HasLocalCredentialsOfflineAsync\(_offlineRoot\.Trim\(\)\)/);
  assert.match(panel,/QueueKeys\.IsOfflineOnly/);
  assert.match(pwaPage,/<VaultLinkedLocalQueuePanel \/>/);
  assert.match(native,/<VaultLinkedLocalQueuePanel \/>/);
  assert.match(publisher,/!ledger\.IsConnected/);
  assert.match(smoke,/await ledger\.DisconnectAsync\(\)/);
  assert.match(smoke,/await keys\.UnlockOfflineAsync\(Root, "local S-key test passphrase 12345"\)/);
  assert.match(smoke,/Offline queue inspection authorized publication/);
});


test('linked unpublished encrypted draft edits reuse atomic CAS and reject cycles or stale fingerprints',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const policy=fs.readFileSync(path.join(core,'VaultLinkedMutationTransitionPolicy.cs'),'utf8');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const browser=fs.readFileSync(path.join(src,'StellarVault.Pwa','Services',
    'IndexedDbVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const native=fs.readFileSync(path.join(core,'FileSystemVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const panel=fs.readFileSync(path.join(src,'StellarVault.UI','Components',
    'VaultLinkedLocalQueuePanel.razor'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,'..','tests',
    'StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(policy,/previous\.State == VaultSyncState\.Pending && next\.State == VaultSyncState\.Pending/);
  assert.match(policy,/EncryptedPayload = next\.EncryptedPayload/);
  assert.match(policy,/DependsOn = next\.DependsOn/);
  assert.match(policy,/Invalid linked queue state transition/);
  for(const adapter of [browser,native])
    assert.match(adapter,/VaultLinkedMutationTransitionPolicy\.RequireValid\(previous, next\)/);
  assert.match(workspace,/OpenDraftAsync\(string mutationId/);
  assert.match(workspace,/ReviseAsync\(/);
  assert.match(workspace,/CryptographicOperations\.FixedTimeEquals\(actual, expected\)/);
  assert.match(workspace,/The proposed folder move creates a dependency cycle/);
  assert.match(workspace,/store\.TryReplaceAsync\(current, replacement, ct\)/);
  assert.match(panel,/DraftWorkspace\.OpenDraftAsync\(id\)/);
  assert.match(panel,/DraftWorkspace\.ReviseAsync\(editingId, draft, revision\)/);
  assert.match(panel,/Edit local draft/);
  assert.match(panel,/Unverified parent/);
  assert.match(smoke,/stale linked draft edit incorrectly overwrote/);
  assert.match(smoke,/Offline encrypted draft revision depended on Horizon/);
});


test('independent S-key manual sync orders encrypted draft review without automatic submission',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const policy=fs.readFileSync(path.join(core,'VaultLinkedManualSyncPlan.cs'),'utf8');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const panel=fs.readFileSync(path.join(src,'StellarVault.UI','Components',
    'VaultLinkedLocalQueuePanel.razor'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,'..','tests',
    'StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(policy,/VaultQueuedCreatePolicy\.Problems\(mutation, entries\)/);
  assert.match(policy,/unresolved-queue-submission/);
  assert.match(policy,/connected-signer-unlock-required/);
  assert.match(policy,/NextReadyMutationId/);
  assert.doesNotMatch(policy,/SubmitApprovedAsync|TryReplaceAsync|QuoteCreateAsync/);
  assert.match(workspace,/VaultLinkedManualSyncPlan\.Build\(all, keys\.CanPublish\)/);
  assert.match(workspace,/finally \{ ClearEntries\(all\); \}/);
  assert.match(panel,/Review manual sync order/);
  assert.match(panel,/Review next ready draft/);
  assert.match(panel,/ReviewDraftAsync\(nextId\)/);
  assert.match(smoke,/initialPlan\.NextReadyMutationId == folder\.MutationId/);
  assert.match(smoke,/afterParentPlan\.NextReadyMutationId == child\.MutationId/);
  assert.match(smoke,/uncertainPlan\.UnresolvedCount > 0 && !uncertainPlan\.CanReviewNext/);
});


test('linked account capacity extension uses a durable single-tail encrypted journal',()=>{
  const root=path.join(__dirname,'..','src');
  const core=path.join(root,'StellarVault.Core','Services');
  const journal=fs.readFileSync(path.join(core,'VaultLinkedExpansionJournal.cs'),'utf8');
  const policy=fs.readFileSync(path.join(core,'VaultLinkedMutationTransitionPolicy.cs'),'utf8');
  const queue=fs.readFileSync(path.join(core,'VaultPendingMutationQueue.cs'),'utf8');
  const manual=fs.readFileSync(path.join(core,'VaultLinkedManualSyncPlan.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const recovery=fs.readFileSync(path.join(core,'VaultLinkedDraftRecoveryReview.cs'),'utf8');
  const panel=fs.readFileSync(path.join(root,'StellarVault.UI','Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,'..','tests','StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(queue,/ExtendLinkedChain/);
  assert.match(queue,/Linked-account expansion journals are never HD queue mutations/);
  assert.match(journal,/RequireNoUnresolvedAsync\(root, scope, ct\)/);
  assert.match(journal,/sv:linked-expansion-intent:v1:/);
  assert.match(journal,/chain\.TailAccountId/);
  assert.match(journal,/Id = reservationId/);
  const reserve=journal.indexOf('store.TryReplaceAsync(draft, reserved, ct)');
  const transmit=journal.indexOf('expansion.SubmitApprovedAsync(approvedQuote, true, ct)');
  assert.ok(reserve>0 && transmit>reserve,'Durable CAS reservation must precede network submit');
  assert.match(journal,/State = VaultSyncState\.Uncertain/);
  assert.match(journal,/State = VaultSyncState\.Confirmed/);
  assert.match(policy,/VaultMutationKind\.ExtendLinkedChain/);
  assert.match(manual,/x\.Kind == VaultMutationKind\.ExtendLinkedChain/);
  assert.match(publisher,/x\.Kind == VaultMutationKind\.ExtendLinkedChain/);
  assert.match(workspace,/IsJournal = false/);
  assert.match(workspace,/Linked account capacity expansion/);
  assert.match(recovery,/item\.Kind == VaultMutationKind\.ExtendLinkedChain/);
  assert.match(panel,/ExpansionJournal\.PrepareAsync\(\)/);
  assert.match(panel,/ExpansionJournal\.SubmitApprovedAsync\(quote, true\)/);
  assert.match(panel,/!draft\.IsJournal/);
  assert.match(smoke,/RunUncertainExpansionSmokeAsync/);
  assert.match(smoke,/await journal\.PrepareAsync\(\)/);
  assert.match(smoke,/new FileSystemVaultLinkedQueueSnapshotStore/);
  for(const host of ['StellarVault.Pwa','StellarVault.Maui']){
    const source=fs.readFileSync(path.join(root,host,host==='StellarVault.Pwa'?'Program.cs':'MauiProgram.cs'),'utf8');
    assert.match(source,/AddScoped<VaultLinkedExpansionJournal>/);
  }
});


test('pending-only linked expansion journal cleanup cannot clear uncertain or submitting transaction',()=>{
  const root=path.join(__dirname,'..','src');
  const core=path.join(root,'StellarVault.Core','Services');
  const journal=fs.readFileSync(path.join(core,'VaultLinkedExpansionJournal.cs'),'utf8');
  const panel=fs.readFileSync(path.join(root,'StellarVault.UI','Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,'..','tests','StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(journal,/DiscardNeverSubmittedIntentAsync/);
  assert.match(journal,/intent\.State != VaultSyncState\.Pending/);
  assert.match(journal,/store\.TryDeleteAsync\(intent, ct\)/);
  assert.match(journal,/VaultMutationKind\.ExtendLinkedChain/);
  assert.match(journal,/x\.Kind == VaultMutationKind\.ExtendLinkedChain/);
  assert.match(journal,/Id = reservationId/);
  assert.match(panel,/Discard never-submitted reservation/);
  assert.match(panel,/ExpansionJournal\.DiscardNeverSubmittedIntentAsync\(id\)/);
  assert.match(workspace,/IsJournal = false/);
  assert.match(panel,/!draft\.IsJournal/);
  assert.match(smoke,/journal\.DiscardNeverSubmittedIntentAsync\(abandoned\.Id\)/);
  assert.match(smoke,/ledger\.Submissions == 0/);
  assert.match(smoke,/RunUncertainExpansionSmokeAsync/);
});


test('linked expansion abandon is limited to pre-transport journal failures',()=>{
  const src=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const journal=fs.readFileSync(path.join(src,'VaultLinkedExpansionJournal.cs'),'utf8');
  const audit=fs.readFileSync(path.join(__dirname,'..','docs','architecture','implementation-reality-check.md'),'utf8');
  assert.match(journal,/var transportInvoked = false/);
  assert.match(journal,/transportInvoked = true;/);
  assert.match(journal,/if \(!transportInvoked\)/);
  assert.match(journal,/expansion\.AbandonUnsubmittedAsync\(CancellationToken\.None\)/);
  assert.match(journal,/State = VaultSyncState\.Uncertain/);
  assert.match(journal,/store\.TryReplaceAsync\(draft, reserved, ct\)/);
  assert.match(audit,/Already implemented/);
  assert.match(audit,/Do not rebuild/);
});


test('linked read-only inspection clears decrypted queue payloads after counting',()=>{
  const source=fs.readFileSync(path.join(__dirname,'..','src',
    'StellarVault.Core','Services','VaultLinkedQueueInspection.cs'),'utf8');
  assert.match(source,/using System\.Security\.Cryptography/);
  assert.match(source,/var mutations = await store\.ListAsync\(root, scope, ct\)/);
  assert.match(source,/finally\s*\{[\s\S]*?CryptographicOperations\.ZeroMemory\(mutation\.EncryptedPayload\)/);
});


test('linked queue scope rejection zeroes decrypted snapshots before throwing',()=>{
  const core=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  assert.match(workspace,/catch\s*\{\s*ClearEntries\(all\);\s*throw;/);
  assert.match(publisher,/catch\s*\{\s*Clear\(all\);\s*throw;/);
});

test('PWA device storage choice stays outside credential tabs and keeps mode-scoped consent',()=>{
  const fs=require('node:fs'),path=require('node:path');
  const root=path.join(__dirname,'..');
  const home=fs.readFileSync(path.join(root,'src','StellarVault.UI','Components','VaultHome.razor'),'utf8');
  const css=fs.readFileSync(path.join(root,'src','StellarVault.Pwa','wwwroot','css','modern-app.css'),'utf8');
  const storage=home.indexOf('class="sv-access-storage-settings sv-entry-storage-outside"');
  const form=home.indexOf('<form class="access-form terminal-panel"');
  assert.ok(storage>0 && storage<form,'Storage preference must precede and remain outside credential form');
  assert.match(home,/CurrentDeviceStoragePreference/);
  assert.match(home,/SelectDeviceStorageAsync\(true\)/);
  assert.match(home,/SelectDeviceStorageAsync\(false\)/);
  assert.match(home,/LinkedRetention\.GrantExplicitConsent\(\)/);
  assert.match(home,/await LinkedQueueKeys\.LockAsync\(\)/);
  assert.match(home,/LinkedRetention\.Reset\(\)/);
  assert.match(home,/_enableLocalStorage = enabled/);
  assert.match(css,/sv-entry-storage-outside/);
  const actions=home.indexOf('class="sv-access-actions sv-entry-actions-outside"');
  const formEnd=home.indexOf('</form>',form);
  assert.ok(actions>formEnd,'Unlock action must remain outside credential form');
  assert.match(home,/type="submit" form="sv-access-form"/);
  assert.match(css,/sv-entry-actions-outside/);
});
