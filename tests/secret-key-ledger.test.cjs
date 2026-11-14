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
