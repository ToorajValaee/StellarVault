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
  let childCounter=0,submitted=0,failSubmission=false,mismatchHash=false;
  const history=new Map();
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
      const txHash=submitted.toString(16).padStart(64,'0');
      history.set(txHash,{
        source:ROOT,operations:tx.operations.slice(),
        hash:()=>Buffer.from(txHash,'hex'),
        signatures:tx.signed.map(key=>({signature:()=>Buffer.from(key,'utf8')}))
      });
      return {successful:true,hash:mismatchHash?'f'.repeat(64):txHash};
    }
  }
  class TransactionBuilder {
    constructor(account,options) {this.operations=[];this.signed=[];this.options=options;
      this.source=account.account_id;}
    addOperation(op){this.operations.push(op);return this;}
    setTimeout(_){return this;}
    build(){return this;}
    sign(pair){this.signed.push(pair.publicKey());}
    hash(){return Buffer.from((submitted+1).toString(16).padStart(64,'0'),'hex');}
    static fromXDR(xdr,network){
      assert.equal(network,passphrase);
      const matched=/^fixture:([0-9a-f]{64})$/.exec(xdr);
      if(!matched||!history.has(matched[1]))throw Error('Unknown signed envelope');
      return history.get(matched[1]);
    }
  }
  function pair(id) {
    return { publicKey:()=>id,
      rawSecretKey:()=>new Uint8Array(32).fill(7) };
  }
  const sdk={
    Keypair:{
      fromSecret:secret=>{assert.equal(secret,SECRET);return pair(ROOT);},
      fromPublicKey:publicKey=>({
        verify:(_hash,sig)=>Buffer.from(sig).toString('utf8')===publicKey
      }),
      random:()=>pair('G'+(++childCounter).toString(36).toUpperCase().padStart(55,'B'))
    },
    StrKey:{isValidEd25519SecretSeed:x=>x===SECRET,
      isValidEd25519PublicKey:x=>typeof x==='string'&&x.length===56&&x[0]==='G'},
    Horizon:{Server},
    TransactionBuilder,
    Asset:{native:()=>({type:'native',getCode:()=> 'XLM'})},
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
      if(url.includes('/transactions/')){
        const hash=url.split('/').at(-1),found=history.get(hash);
        return {ok:!!found,url,
          json:async()=>({successful:!!found,hash,source_account:ROOT,
            envelope_xdr:'fixture:'+hash})};
      }
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
    accounts,history,api,
    open:()=>api.open(session,SECRET,'https://horizon-testnet.stellar.org',passphrase),
    read:()=>api.read(session),
    plan:(kind,id,draft)=>api.plan(session,kind,id,draft),
    submit:id=>api.submit(session,id),
    inspectUncertain:()=>api.inspectUncertain(session),
    close:()=>api.close(session),
    submissions:()=>submitted,
    failNextSubmission:()=>{failSubmission=true;},
    mismatchNextHash:()=>{mismatchHash=true;},
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
  assert.match(finding.TransactionHash,/^[0-9a-f]{64}$/);
  assert.equal(finding.TransactionHash,'1'.padStart(64,'0'));
  assert.equal(finding.NetworkPassphrase,TEST);
  assert.equal(finding.HorizonUrl,'https://horizon-testnet.stellar.org');
  assert.ok(!JSON.stringify(finding).includes(record.Content));
  finding.RecordId='attacker-edited'; // The diagnostic is a defensive copy.
  assert.equal(h.inspectUncertain().RecordId,recordId);
  await assert.rejects(h.plan('expand',null,null),/unresolved/);
  h.close();
});

test('signed transaction hash is stable before failed Horizon submission and cannot authorize retry',async()=>{
  const h=harness();await h.open();
  const q=await h.plan('create',recordId,record);
  h.failNextSubmission();
  await assert.rejects(h.submit(q.QuoteId),/uncertain/);
  const diagnostic=h.inspectUncertain();
  assert.equal(diagnostic.TransactionHash,'1'.padStart(64,'0'));
  assert.equal(h.submissions(),1);
  await assert.rejects(h.plan('create','fedcba9876543210',record),/unresolved/);
  h.close();
});

test('mismatched Horizon hash cannot erase the pre-broadcast signed identity',async()=>{
  const h=harness();await h.open();
  const q=await h.plan('create',recordId,record);
  h.mismatchNextHash();
  await assert.rejects(h.submit(q.QuoteId),/different transaction hash/);
  const diagnostic=h.inspectUncertain();
  assert.equal(diagnostic.TransactionHash,'1'.padStart(64,'0'));
  assert.equal(h.submissions(),1);
  await assert.rejects(h.plan('expand',null,null),/unresolved/);
  h.close();
});

test('signed-hash durable callback completes before linked transaction is broadcast',async()=>{
  const h=harness();await h.open();
  const quote=await h.plan('create',recordId,record);
  let pinned;
  const hash=await h.api.submit(h.session,quote.QuoteId,{
    invokeMethodAsync:async(method,signed)=>{
      assert.equal(method,'CheckpointSignedHash');
      assert.equal(h.submissions(),0,'Network write preceded durable checkpoint');
      assert.equal(signed,'1'.padStart(64,'0'));
      pinned=signed;
    }
  });
  assert.equal(hash,pinned);
  assert.equal(h.submissions(),1);
  h.close();
});

test('failed durable signed-hash checkpoint aborts network submission',async()=>{
  const h=harness();await h.open();
  const quote=await h.plan('create',recordId,record);
  await assert.rejects(h.api.submit(h.session,quote.QuoteId,{
    invokeMethodAsync:async(method,hash)=>{
      assert.equal(method,'CheckpointSignedHash');
      assert.match(hash,/^[0-9a-f]{64}$/);
      throw new Error('Encrypted queue CAS rejected');
    }
  }),/Encrypted queue CAS rejected/);
  assert.equal(h.submissions(),0,'Checkpoint failure submitted a Stellar transaction');
  assert.equal(h.inspectUncertain(),null,'No transport occurred, so no runtime broadcast uncertainty');
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
  assert.equal(reopened.TransactionHash,'1'.padStart(64,'0'));
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
  assert.match(policy,/Hd => Hd/);
  assert.doesNotMatch(policy,/null or Hd => Hd/,'missing strategy must fail closed');
  assert.match(policy,/Encrypted mutation requires an explicit recognized account strategy/);
  assert.match(policy,/Linked => Linked/);
  assert.match(queue,/string OriginStrategyId = VaultMutationStrategyPolicy\.Hd/);
  assert.doesNotMatch(queue,/string\? OriginStrategyId = null/,'unversioned queue entries are not supported');
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
  const submit=expansion.indexOf('hash = await ledger.SubmitApprovedAsync(pending.Quote.QuoteId, ct);');
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
  assert.match(workspace,/<VaultLinkedLocalQueuePanel View=/);
  assert.match(panel,/private bool ShowDrafts => View is "all" or "drafts"/);
  assert.match(panel,/private bool ShowSync => View is "all" or "sync"/);
  assert.match(native,/<VaultSecretKeyWorkspace Embedded="true"/);
  assert.match(native,/NativeContentKey => SecretLedger.IsConnected/);
  assert.match(native,/OnViewChange="NavigateLinkedNative"/);
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
  const broadcast=publisher.indexOf('transport.SubmitWithSignedHashAsync(');
  assert.ok(reserve>=0 && broadcast>reserve);
  assert.match(publisher,/pending\.Quote, approvedQuote/);
  assert.match(publisher,/VaultLinkedMultipartFilePolicy\.Problems\(item, local, remote\)/);
  assert.match(publisher,/CryptographicOperations\.FixedTimeEquals/);
  assert.match(publisher,/Fingerprint\(original\)/);
  assert.match(publisher,/State = VaultSyncState\.Submitting/);
  assert.match(publisher,/State = VaultSyncState\.Uncertain/);
  assert.doesNotMatch(publisher,/State = VaultSyncState\.Confirmed/);
  assert.match(publisher,/Current account snapshot does not prove|matching CURRENT account snapshot/);
  assert.match(publisher,/ledger\.ReadAsync\(ct\)/);
  assert.match(publisher,/store\.TryReplaceAsync\(submission, uncertain, CancellationToken\.None\)/);
  assert.match(publisher,/store\.TryReplaceAsync\(\s*submission, pinned, CancellationToken\.None\)/);
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
  assert.match(smoke,/Successful linked expansion was confirmed without historical signer proof/);
  assert.match(smoke,/VaultSyncState\.Uncertain or VaultSyncState\.Submitting/);
  assert.match(smoke,/MustRejectAsync\(\(\) => publisher\.PrepareAsync\(child\.MutationId\)/);
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
  assert.match(test,/finding\.CurrentRecordMatches is true/);
  assert.match(test,/NetworkFeeXlm = "999\.0000000"/);
  for(const file of ['StellarVault.Pwa/Program.cs','StellarVault.Maui/MauiProgram.cs']){
    const host=fs.readFileSync(path.join(src,...file.split('/')),'utf8');
    assert.match(host,/AddScoped<VaultLinkedDraftRecoveryReview>/);
  }
});


test('existing S-key credentials unlock local encrypted drafts without Horizon or signing authority',()=>{
  const src=path.join(__dirname,'..','src');
  const core=path.join(src,'StellarVault.Core','Services');
  const pwaPage=fs.readFileSync(path.join(src,'StellarVault.Pwa','Pages','Vault.razor'),'utf8');
  const routeAlias=fs.readFileSync(path.join(src,'StellarVault.Pwa','Pages','SecretVault.razor'),'utf8');
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
  assert.match(pwaPage,/Recover independent encrypted S-key drafts offline/);
  assert.match(routeAlias,/Navigation.NavigateTo\(mode == VaultWorkspaceMode.ConflictingSessions/);
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
  const sharedFields=fs.readFileSync(path.join(src,'StellarVault.UI',
    'Components','VaultRecordFields.razor'),'utf8');
  assert.match(panel,/<VaultRecordFields IsFolder="_draftIsFolder"/);
  assert.match(sharedFields,/Unverified parent/);
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
  assert.match(policy,/VaultLinkedMultipartFilePolicy\.Problems\(mutation, entries\)/);
  const multipartPolicy=fs.readFileSync(path.join(core,'VaultLinkedMultipartFilePolicy.cs'),'utf8');
  assert.match(multipartPolicy,/VaultLinkedSingleFilePolicy\.Problems\(mutation, all\)/);
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
  assert.match(smoke,/afterParentPlan\.UnresolvedCount > 0 && !afterParentPlan\.CanReviewNext/);
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
  const transmit=journal.indexOf('expansion.SubmitWithSignedHashAsync(');
  assert.ok(reserve>0 && transmit>reserve,'Durable CAS reservation must precede network submit');
  assert.match(journal,/State = VaultSyncState\.Uncertain/);
  assert.doesNotMatch(journal,/State = VaultSyncState\.Confirmed/);
  assert.match(journal,/historical[\s\S]*?signer/);
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
  const form=home.indexOf('<form id="sv-access-form" class="access-form terminal-panel"');
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

test('linked queue binds volatile signed transaction identity and restart receipt inspection to network scope',()=>{
  const core=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const identifier=fs.readFileSync(path.join(core,'VaultLinkedSignedSubmissionIdentity.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const expansion=fs.readFileSync(path.join(core,'VaultLinkedExpansionJournal.cs'),'utf8');
  const recovery=fs.readFileSync(path.join(core,'VaultLinkedDraftRecoveryReview.cs'),'utf8');
  assert.match(source,/const rawHash=tx\.hash\(\)/);
  assert.match(source,/TransactionHash:signedHash/);
  assert.match(source,/result\.hash\.toLowerCase\(\)!==signedHash/);
  assert.match(identifier,/diagnostic\.NetworkPassphrase != networkPassphrase/);
  assert.match(identifier,/diagnostic\.TargetAccountId != targetAccountId/);
  assert.match(identifier,/ledger\.ConnectionEpoch != signerEpoch/);
  assert.match(publisher,/VaultLinkedSignedSubmissionIdentity\.TryReadAsync/);
  assert.match(expansion,/VaultLinkedSignedSubmissionIdentity\.TryReadAsync/);
  assert.match(recovery,/BuildDurableReceiptQuery\(root, item\)/);
  const smoke=fs.readFileSync(path.join(__dirname,'..','tests',
    'StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(smoke,/class CapturingLinkedReceiptEvidence/);
  assert.match(smoke,/ledger\.Reconnect\(\); \/\/ The volatile JS diagnostic is absent/);
  assert.match(smoke,/witness\.Observed\?\.TransactionHash == hash/);
  assert.match(smoke,/A mocked observed receipt wrongly released an unproven linked expansion/);
  assert.match(recovery,/item\.TransactionHash is not \{ Length: 64 \} hash/);
  assert.match(recovery,/ledger\.ConnectedNetworkPassphrase != expectedPassphrase/);
  assert.doesNotMatch(recovery,/store\.TryReplaceAsync|VaultSyncState\.Confirmed/);
  const policy=fs.readFileSync(path.join(core,'VaultLinkedMutationTransitionPolicy.cs'),'utf8');
  const adapter=fs.readFileSync(path.join(__dirname,'..','src','StellarVault.Pwa','Services','BrowserSecretKeyLedger.cs'),'utf8');
  const interfaceSource=fs.readFileSync(path.join(core,'IStellarSecretKeySignedSubmission.cs'),'utf8');
  assert.match(policy,/previous\.State == VaultSyncState\.Submitting &&\s*next\.State == VaultSyncState\.Submitting/);
  assert.match(policy,/previous\.TransactionHash is \{ \} pinnedHash/);
  assert.match(adapter,/DotNetObjectReference\.Create/);
  assert.match(adapter,/JSInvokable\("CheckpointSignedHash"\)/);
  assert.match(adapter,/IStellarSecretKeySignedSubmission/);
  assert.match(interfaceSource,/SubmitWithSignedHashAsync/);
  assert.match(expansion,/submission\.TransactionHash != hash/);
  assert.match(publisher,/submission\.TransactionHash != hash/);
  assert.match(source,/await checkpoint\.invokeMethodAsync\('CheckpointSignedHash',signedHash\)/);
});

test('linked historical skh/skc inspection is bounded, read-only and never confirms uncertain queue',()=>{
  const root=path.join(__dirname,'..','src');
  const inspector=fs.readFileSync(path.join(root,'StellarVault.Pwa','Services',
    'HorizonVaultLinkedRecordOperationInspector.cs'),'utf8');
  const receipt=fs.readFileSync(path.join(root,'StellarVault.Pwa','Services',
    'HorizonVaultLinkedReceiptEvidence.cs'),'utf8');
  const recovery=fs.readFileSync(path.join(root,'StellarVault.Core','Services',
    'VaultLinkedDraftRecoveryReview.cs'),'utf8');
  const panel=fs.readFileSync(path.join(root,'StellarVault.UI','Components',
    'VaultLinkedLocalQueuePanel.razor'),'utf8');
  for(const host of ['StellarVault.Pwa/Program.cs','StellarVault.Maui/MauiProgram.cs']){
    const content=fs.readFileSync(path.join(root,host),'utf8');
    assert.match(content,/AddScoped<HorizonVaultLinkedRecordOperationInspector>/);
  }
  assert.match(inspector,/transactions\//);
  assert.match(inspector,/operations\?limit=200/);
  assert.match(inspector,/MaxOperations = 90/);
  assert.match(inspector,/MaxBytes = 262144/);
  assert.match(inspector,/skh:/);
  assert.match(inspector,/skc:/);
  assert.match(inspector,/IncrementalHash\.CreateHash\(HashAlgorithmName\.SHA256\)/);
  assert.match(inspector,/CryptographicOperations\.FixedTimeEquals/);
  assert.match(inspector,/Mixed generations or duplicate record chunk/);
  assert.match(inspector,/Record chunk appeared after the manifest/);
  assert.match(inspector,/manifest and skc chunks use different generations|manifest and skc/);
  assert.doesNotMatch(inspector,/TryReplaceAsync|VaultSyncState\.Confirmed|submitTransaction/);
  assert.match(receipt,/records\.InspectCreateAsync\(/);
  assert.match(recovery,/historicalRecordOperations = evidence\.HistoricalRecordOperationsMatch/);
  assert.match(panel,/Historical record operation check/);
  assert.doesNotMatch(recovery,/store\.TryReplaceAsync/);
});

test('linked expansion historical operation verification is read-only and never releases journal',()=>{
  const root=path.join(__dirname,'..','src');
  const inspector=fs.readFileSync(path.join(root,'StellarVault.Pwa','Services',
    'HorizonVaultLinkedExpansionOperationInspector.cs'),'utf8');
  const evidence=fs.readFileSync(path.join(root,'StellarVault.Pwa','Services',
    'HorizonVaultLinkedReceiptEvidence.cs'),'utf8');
  const review=fs.readFileSync(path.join(root,'StellarVault.Core','Services',
    'VaultLinkedDraftRecoveryReview.cs'),'utf8');
  const panel=fs.readFileSync(path.join(root,'StellarVault.UI','Components',
    'VaultLinkedLocalQueuePanel.razor'),'utf8');
  const nativeSmoke=fs.readFileSync(path.join(__dirname,'..','tests',
    'StellarVault.Core.CryptoTests','LinkedExpansionHistorySmoke.cs'),'utf8');
  const program=fs.readFileSync(path.join(__dirname,'..','tests',
    'StellarVault.Core.CryptoTests','Program.cs'),'utf8');
  assert.match(program,/await LinkedExpansionHistorySmoke\.RunAsync\(\)/);
  assert.match(nativeSmoke,/Child master signer was not disabled/);
  assert.match(nativeSmoke,/Incorrect root signer was accepted/);
  assert.match(nativeSmoke,/Parent pointer to wrong child was accepted/);
  assert.match(inspector,/MaxResponseBytes = 256 \* 1024/);
  assert.match(inspector,/create_account/);
  assert.match(inspector,/set_options/);
  assert.match(inspector,/master_key_weight/);
  assert.match(inspector,/GetString\(create, "funder"\) != rootAccount/);
  assert.match(inspector,/PositiveXlm\(GetString\(create, "starting_balance"\)\)/);
  assert.match(inspector,/PositiveXlm\(GetString\(funding, "amount"\)\)/);
  assert.match(inspector,/sv:next:v1/);
  assert.match(inspector,/StellarAccountAddress\.IsValid\(value\)/);
  assert.match(inspector,/transaction_successful/);
  assert.doesNotMatch(inspector,/TryReplaceAsync|SubmitApprovedAsync|VaultSyncState\.Confirmed/);
  assert.match(evidence,/expansions\.InspectAsync\(/);
  assert.match(review,/historicalExpansionOperations = evidence\.HistoricalExpansionOperationsMatch/);
  assert.match(panel,/Historical linked expansion operations/);
  for(const host of ['StellarVault.Pwa/Program.cs','StellarVault.Maui/MauiProgram.cs']){
    assert.match(fs.readFileSync(path.join(root,host),'utf8'),
      /AddScoped<HorizonVaultLinkedExpansionOperationInspector>/);
  }
});

test('linked signer rejects a published folder move that creates a parent cycle',async()=>{
  const h=harness();await h.open();
  const first='1111111111111111',second='2222222222222222';
  const folder={...record,Title:'First',Content:'folder',IsFolder:true,
    FolderId:null};
  await h.submit((await h.plan('create',first,folder)).QuoteId);
  await h.submit((await h.plan('create',second,{...folder,Title:'Second',FolderId:first})).QuoteId);
  await assert.rejects(h.plan('update',first,{...folder,FolderId:second}),/cycle/);
  await assert.rejects(h.plan('update',first,{...folder,FolderId:first}),/cycle/);
  assert.equal(h.submissions(),2,'Rejected folder cycle reached Stellar');
  h.close();
});

test('revision-pinned linked update/delete use encrypted queue with preflight conflict checks',()=>{
  const root=path.join(__dirname,'..');
  const core=path.join(root,'src','StellarVault.Core','Services');
  const pwa=path.join(root,'src','StellarVault.Pwa');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const planner=fs.readFileSync(path.join(core,'VaultLinkedManualSyncPlan.cs'),'utf8');
  const transition=fs.readFileSync(path.join(core,'VaultLinkedMutationTransitionPolicy.cs'),'utf8');
  const revision=fs.readFileSync(path.join(core,'IStellarSecretKeyRecordRevisionReader.cs'),'utf8');
  const adapter=fs.readFileSync(path.join(pwa,'Services','BrowserSecretKeyLedger.cs'),'utf8');
  const panel=fs.readFileSync(path.join(root,'src','StellarVault.UI',
    'Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const smoke=fs.readFileSync(path.join(root,'tests','StellarVault.Core.CryptoTests',
    'LinkedDraftSmoke.cs'),'utf8');
  assert.match(source,/async function recordRevision\(id,recordId\)/);
  assert.match(source,/record\?\.rawHeader\|\|null/);
  assert.match(source,/submit,recordRevision,verifyHistoricalRecord,verifyHistoricalExpansion,releaseVerifiedHistory,inspectUncertain/);
  assert.match(revision,/ReadRecordRevisionAsync/);
  assert.match(adapter,/IStellarSecretKeyRecordRevisionReader/);
  assert.match(adapter,/stellarVaultSecretLedger\.recordRevision/);
  assert.match(workspace,/StageUpdateAsync/);
  assert.match(workspace,/StageDeleteAsync/);
  // Staging passes the original manifest through the factory parameter;
  // publication reads the resulting immutable queue property.
  assert.match(workspace,/expectedRemoteVersion: expectedRevision/);
  assert.match(publisher,/item\.ExpectedRemoteVersion/);
  assert.match(workspace,/currentRevision != expectedRevision/);
  assert.match(workspace,/var beforeRevision = await revisionReader\.ReadRecordRevisionAsync\(entityId, ct\)/);
  assert.match(workspace,/beforeRevision != expectedRevision/);
  assert.match(workspace,/remote\.Any\(x => x\.FolderId == entityId\)/);
  assert.match(publisher,/RequireRemoteVersionAsync\(item, ct\)/);
  assert.match(publisher,/RequireRemoteVersionAsync\(original, ct\)/);
  assert.match(publisher,/QuoteUpdateAsync/);
  assert.match(publisher,/QuoteDeleteAsync/);
  assert.match(publisher,/Deletion\(original\.Kind\)/);
  assert.match(publisher,/SubmitWithSignedHashAsync/);
  assert.match(planner,/VaultMutationKind\.UpdateRecord/);
  assert.match(planner,/VaultMutationKind\.DeleteRecord/);
  assert.match(transition,/VaultMutationKind\.UpdateFolder/);
  assert.match(transition,/VaultMutationKind\.DeleteFolder/);
  const revisionPolicy=fs.readFileSync(path.join(core,
    'VaultLinkedRemoteRevisionPolicy.cs'),'utf8');
  const linkedNative=fs.readFileSync(path.join(core,
    'FileSystemVaultLinkedQueueSnapshotStore.cs'),'utf8');
  const linkedPwa=fs.readFileSync(path.join(pwa,'Services',
    'IndexedDbVaultLinkedQueueSnapshotStore.cs'),'utf8');
  assert.match(revisionPolicy,/ReadUInt16BigEndian/);
  assert.match(revisionPolicy,/ReadInt32BigEndian/);
  assert.match(linkedNative,/VaultLinkedRemoteRevisionPolicy\.IsValid/);
  assert.match(linkedPwa,/VaultLinkedRemoteRevisionPolicy\.IsValid/);
  assert.match(transition,/A staged linked deletion must retain its original encrypted record snapshot/);
  assert.match(panel,/LoadPublishedRecordsAsync/);
  assert.match(panel,/ReadStablePublishedAsync/);
  assert.match(panel,/before != after/);
  assert.match(panel,/StageUpdateAsync\(remoteId, draft, remoteRevision\)/);
  assert.match(panel,/StageDeleteAsync\(_deletePublishedId, _deletePublishedRevision\)/);
  assert.match(smoke,/RunRemoteMutationSmokeAsync\(false\)/);
  assert.match(smoke,/RunRemoteMutationSmokeAsync\(true\)/);
  assert.match(smoke,/ledger\.BumpRemoteRevision\(id\)/);
  assert.doesNotMatch(publisher,/State = VaultSyncState\.Confirmed/);
});

test('historical S-key record proof rejects wrong plaintext and missing root signature',async()=>{
  const h=harness();await h.open();
  const q=await h.plan('create',recordId,record);
  const txHash=await h.submit(q.QuoteId);
  assert.throws(()=>h.api.releaseVerifiedHistory(h.session,txHash),
    /has not verified/);
  assert.equal(await h.api.verifyHistoricalRecord(h.session,txHash,
    'create',recordId,{...record,Content:'another secret'},null),false);
  assert.equal(await h.api.verifyHistoricalRecord(h.session,txHash,
    'create',recordId,record,null),true);
  assert.equal(h.api.releaseVerifiedHistory(h.session,txHash),true);
  h.history.get(txHash).signatures=[];
  await assert.rejects(h.api.verifyHistoricalRecord(h.session,txHash,
    'create',recordId,record,null),/valid root signature/);
  assert.equal(h.submissions(),1,'History inspection retransmitted the transaction');
  h.close();
});

test('historical S-key update/delete proof binds original manifest, ciphertext and removal keys',async()=>{
  const h=harness();await h.open();
  const first=await h.plan('create',recordId,record);
  await h.submit(first.QuoteId);
  let original=await h.api.recordRevision(h.session,recordId);
  const revised={...record,Notes:'changed historical note'};
  const update=await h.plan('update',recordId,revised);
  const updateHash=await h.submit(update.QuoteId);
  assert.equal(await h.api.verifyHistoricalRecord(h.session,updateHash,
    'update',recordId,revised,original),true);
  assert.equal(await h.api.verifyHistoricalRecord(h.session,updateHash,
    'update',recordId,revised,null).catch(()=>false),false);
  original=await h.api.recordRevision(h.session,recordId);
  const deletion=await h.plan('delete',recordId,null);
  const deleteHash=await h.submit(deletion.QuoteId);
  assert.equal(await h.api.verifyHistoricalRecord(h.session,deleteHash,
    'delete',recordId,revised,original),true);
  assert.equal((await h.read()).length,0);
  assert.equal(h.submissions(),3);
  h.close();
});

test('historical linked expansion proof requires matching root and child signatures',async()=>{
  const h=harness();await h.open();
  const first=await h.plan('expand',null,null);
  const firstHash=await h.submit(first.QuoteId);
  assert.equal(await h.api.verifyHistoricalExpansion(h.session,
    firstHash,first.TargetAccountId),true);
  const second=await h.plan('expand',null,null);
  const secondHash=await h.submit(second.QuoteId);
  assert.equal(await h.api.verifyHistoricalExpansion(h.session,
    secondHash,second.TargetAccountId),true);
  h.history.get(secondHash).signatures=h.history.get(secondHash).signatures.slice(0,1);
  await assert.rejects(h.api.verifyHistoricalExpansion(h.session,
    secondHash,second.TargetAccountId),/root and child signatures/);
  assert.equal(h.submissions(),2,'Historical expansion review published again');
  h.close();
});

test('linked verified recovery requires signed XDR and encrypted journal CAS, never a replay',()=>{
  const root=path.join(__dirname,'..');
  const core=path.join(root,'src','StellarVault.Core','Services');
  const pwa=path.join(root,'src','StellarVault.Pwa');
  const app=fs.readFileSync(path.join(pwa,'Services','BrowserSecretKeyLedger.cs'),'utf8');
  const reconciliation=fs.readFileSync(path.join(core,'VaultLinkedVerifiedReconciliation.cs'),'utf8');
  const policy=fs.readFileSync(path.join(core,'VaultLinkedMutationTransitionPolicy.cs'),'utf8');
  const panel=fs.readFileSync(path.join(root,'src','StellarVault.UI','Components',
    'VaultLinkedLocalQueuePanel.razor'),'utf8');
  const native=fs.readFileSync(path.join(root,'tests','StellarVault.Core.CryptoTests',
    'LinkedDraftSmoke.cs'),'utf8');
  assert.match(source,/async function verifyHistoricalRecord\(/);
  assert.match(source,/async function verifyHistoricalExpansion\(/);
  assert.match(source,/TransactionBuilder\.fromXDR\(/);
  assert.match(source,/rootKey\.verify\(rawHash/);
  assert.match(source,/await unseal\(s,recordId,manifest\.gen,cipher\)/);
  assert.match(source,/hasSignature\(s\.root\)\|\|!hasSignature\(childId\)/);
  assert.match(source,/releaseVerifiedHistory/);
  assert.match(app,/IStellarSecretKeyHistoricalRecordVerifier/);
  assert.match(app,/IStellarSecretKeyHistoricalExpansionVerifier/);
  assert.match(reconciliation,/before\.CurrentRecordMatches is not true/);
  assert.match(reconciliation,/before\.HistoricalReceiptObserved/);
  assert.match(reconciliation,/before\.HistoricalExpansionOperationsMatch/);
  assert.match(reconciliation,/signer\.VerifyAsync\(/);
  assert.match(reconciliation,/historical\.VerifyExpansionAsync\(/);
  assert.match(reconciliation,/after\.CurrentRecordMatches is not true/);
  assert.match(reconciliation,/store\.TryReplaceAsync\(entry, confirmed, CancellationToken\.None\)/);
  assert.match(reconciliation,/session\.ReleaseVerifiedAsync\(txHash, CancellationToken\.None\)/);
  assert.doesNotMatch(reconciliation,/SubmitApprovedAsync|QuoteCreateAsync|QuoteUpdateAsync|QuoteDeleteAsync/);
  assert.match(policy,/previous\.State == VaultSyncState\.Uncertain &&\s*next\.State == VaultSyncState\.Confirmed/);
  assert.match(panel,/ConfirmVerifiedExpansionAsync\(mutationId\)/);
  assert.match(panel,/ConfirmVerifiedRecordAsync\(mutationId\)/);
  assert.match(native,/AllowHistoricalProof = true/);
  assert.match(native,/ConfirmVerifiedExpansionAsync\(journalId\)/);
});

test('single-record SVFILE1 and linked multipart SVFILE2 use separate typed review gates',()=>{
  const root=path.join(__dirname,'..');
  const core=path.join(root,'src','StellarVault.Core','Services');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const filePolicy=fs.readFileSync(path.join(core,'VaultLinkedSingleFilePolicy.cs'),'utf8');
  const createPolicy=fs.readFileSync(path.join(core,'VaultQueuedCreatePolicy.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const panel=fs.readFileSync(path.join(root,'src','StellarVault.UI',
    'Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const native=fs.readFileSync(path.join(root,'tests',
    'StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(filePolicy,/StellarLedgerFileProtocol\.BuildUpload/);
  assert.match(filePolicy,/StellarLedgerFileProtocol\.DecodeFile/);
  assert.match(filePolicy,/StellarLedgerFileProtocol\.MaxChunkBytes/);
  assert.match(filePolicy,/SinglePrefix/);
  assert.match(filePolicy,/mutation\.OriginStrategyId != VaultMutationStrategyPolicy\.Linked/);
  assert.match(filePolicy,/mutation\.Kind != VaultMutationKind\.CreateRecord/);
  assert.match(filePolicy,/file-protocol-needs-staged-review/);
  assert.match(createPolicy,/file-protocol-needs-staged-review/);
  assert.match(workspace,/StageSmallFileAsync\(/);
  assert.match(workspace,/CreateCoreAsync\(draft, allowSingleFile: true, ct, ObjectCapacityPolicy\.SingleAccount\)/);
  // The publication gate validates SVFILE1 through the multipart policy's
  // delegation to the single-file policy, rather than calling it directly.
  assert.match(publisher,/VaultLinkedMultipartFilePolicy\.Problems\(item, local, remote\)/);
  const multipartPolicy=fs.readFileSync(path.join(core,'VaultLinkedMultipartFilePolicy.cs'),'utf8');
  assert.match(multipartPolicy,/VaultLinkedSingleFilePolicy\.Problems\(mutation, all\)/);
  assert.match(panel,/StageLinkedSmallFileAsync/);
  assert.match(panel,/DownloadLinkedSmallFileAsync/);
  // Current UI streams directly from IBrowserFile; there is no complete-file
  // plaintext `contents` buffer left to zero. Legacy decoded downloads
  // still wipe their temporary plaintext after use.
  assert.match(panel,/_smallFile\.OpenReadStream\(_smallFile\.Size\)/);
  assert.match(panel,/DraftWorkspace\.StageTreeFileAsync\(/);
  assert.doesNotMatch(panel,/using var buffer = new MemoryStream\(\)/);
  assert.match(panel,/CryptographicOperations\.ZeroMemory\(decoded\)/);
  assert.match(native,/RunSingleFileLinkedSmokeAsync\(\)/);
  assert.match(native,/VaultLinkedSingleFilePolicy\.Problems\(raw, entries\)/);
  assert.match(workspace,/item\.Kind is not \(VaultMutationKind\.CreateRecord or\s*VaultMutationKind\.DeleteRecord\)/);
  assert.match(native,/StageDeleteAsync\(file\.EntityId, originalRevision!\)/);
  assert.match(native,/deletionEntry\.Kind == VaultMutationKind\.DeleteRecord/);
  assert.match(native,/drafts\.OpenDraftAsync\(deletion\.MutationId\)/);
  assert.match(native,/reconciler\.ConfirmVerifiedRecordAsync\(deletion\.MutationId\)/);
  assert.match(workspace,/Cannot stage deletion of a malformed or legacy unchecksummed S-key file/);
  assert.match(workspace,/Multipart S-key file deletion requires the dedicated verified multipart workflow/);
  assert.match(native,/drafts\.StageDeleteAsync\(corruptId, corruptRevision\)/);
  assert.match(native,/drafts\.StageDeleteAsync\(multipartId, multipartRevision\)/);
  assert.match(native,/StellarLedgerFileProtocol\.DecodeFile/);
});

test('linked SVFILE2 multipart staging requires historical part confirmation and manifest-last integrity',()=>{
  const root=path.join(__dirname,'..');
  const core=path.join(root,'src','StellarVault.Core','Services');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const policy=fs.readFileSync(path.join(core,'VaultLinkedMultipartFilePolicy.cs'),'utf8');
  const planner=fs.readFileSync(path.join(core,'VaultLinkedManualSyncPlan.cs'),'utf8');
  const publisher=fs.readFileSync(path.join(core,'VaultLinkedDraftPublication.cs'),'utf8');
  const ui=fs.readFileSync(path.join(root,'src','StellarVault.UI',
    'Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const smoke=fs.readFileSync(path.join(root,'tests',
    'StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  assert.match(policy,/MaxParts = 48/);
  assert.match(policy,/StellarLedgerFileProtocol\.BuildUpload/);
  assert.match(policy,/multipart-previous-part-not-confirmed/);
  assert.match(policy,/multipart-all-parts-must-be-historically-confirmed/);
  assert.match(policy,/multipart-confirmed-part-changed-on-ledger/);
  assert.match(policy,/items\[0\]\.State != VaultSyncState\.Pending/);
  assert.match(policy,/items\[0\]\.TransactionHash is not null/);
  assert.match(policy,/StellarLedgerFileProtocol\.DecodeFile\(candidate, remote\)/);
  assert.match(policy,/IsReferencedByPendingManifest/);
  // Malformed encrypted drafts must fail closed before accessing Content.
  assert.match(policy,/if \(draft\?\.Content is null\) return normal;/);
  assert.match(workspace,/StageMultipartFileAsync/);
  assert.match(workspace,/Linked encrypted draft content is missing/);
  assert.match(workspace,/Published linked record content is missing/);
  assert.match(workspace,/store\.AddAsync\(item, ct\)/);
  assert.match(workspace,/store\.TryDeleteAsync\(item, CancellationToken\.None\)/);
  assert.match(workspace,/VaultLinkedMultipartFilePolicy\.IsReferencedByPendingManifest/);
  assert.match(planner,/VaultLinkedMultipartFilePolicy\.Problems\(mutation, entries\)/);
  assert.match(publisher,/VaultLinkedMultipartFilePolicy\.Problems\(item, local, remote\)/);
  assert.match(ui,/StageTreeFileAsync\(/);
  assert.doesNotMatch(ui,/VaultLinkedMultipartFilePolicy\.MaxBytes\)\"/);
  assert.match(ui,/StellarLedgerFileProtocol\.IsFile\(file\)/);
  assert.match(smoke,/RunMultipartLinkedSmokeAsync\(\)/);
  assert.match(smoke,/Multipart manifest could publish before its file parts/);
});

test('default zero-threshold S-key root recovers records but cannot plan ledger writes',async()=>{
  const fixture=harness();
  fixture.accounts.get(ROOT).thresholds={
    low_threshold:0,med_threshold:0,high_threshold:0
  };
  await fixture.open();
  assert.equal((await fixture.read()).length,0);
  await assert.rejects(fixture.plan('create',recordId,{
    Title:'blocked',Content:'private',IsFolder:false,Tags:[]
  }),/Vault writes are blocked.*zero signing thresholds/);
  assert.equal(fixture.history.size,0);
  fixture.close();
});

test('new pre-launch streaming file protocol has no arbitrary total-byte ceiling in Core',()=>{
  const core=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const tree=fs.readFileSync(path.join(core,'VaultStreamingFileTree.cs'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,
    'StellarVault.Core.CryptoTests','StreamingFileTreeSmoke.cs'),'utf8');
  const runner=fs.readFileSync(path.join(__dirname,
    'StellarVault.Core.CryptoTests','Program.cs'),'utf8');
  assert.match(tree,/public const int Fanout = 8/);
  assert.match(tree,/IncrementalHash\.CreateHash\(HashAlgorithmName\.SHA256\)/);
  assert.match(tree,/input\.ReadAsync\(buffer\.AsMemory\(filled\), ct\)/);
  assert.match(tree,/var rootId = await EmitAsync\(RootKind,/);
  assert.match(tree,/return new Result\(rootId, root, emitted\)/);
  assert.match(tree,/ReadVerifiedAsync\(/);
  assert.match(tree,/CryptographicOperations\.ZeroMemory\(payload\)/);
  assert.doesNotMatch(tree,/MaxFileBytes|MaxParts = \(MaxFileBytes/);
  assert.match(smoke,/1024 \* 1024 \+ 19/);
  assert.match(smoke,/Tampered streaming file node was accepted/);
  assert.match(smoke,/Simulated journal failure/);
  assert.match(runner,/await StreamingFileTreeSmoke\.RunAsync\(\)/);
});

test('shared local file upload now commits a streaming encrypted tree through the vault CAS pointer',()=>{
  const core=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const accountDb=fs.readFileSync(path.join(core,'StellarAccountVaultDatabase.cs'),'utf8');
  const binary=fs.readFileSync(path.join(core,'VaultBinaryWorkspace.cs'),'utf8');
  const adapter=fs.readFileSync(path.join(core,'VaultChunkObjectStoreAdapter.cs'),'utf8');
  const encrypted=fs.readFileSync(path.join(core,'VaultStreamingEncryptedFileStore.cs'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,'StellarVault.Core.CryptoTests',
    'StreamingFileTreeSmoke.cs'),'utf8');
  assert.match(accountDb,/PutIndexedStreamingFileAsync\(/);
  assert.match(accountDb,/ReadStreamingFileAsync\(/);
  assert.match(accountDb,/var original = await GetVersionAsync\(name, vaultKey, cancellationToken\)/);
  assert.match(accountDb,/RestoreAsync\(\s*result.RootId, _fileObjects, Stream.Null/);
  assert.match(smoke,/Unreadable encrypted node was committed as a visible file/);
  assert.match(smoke,/Stale file revision consumed the source before the preflight rejection/);
  assert.match(accountDb,/PutIndexedAsync\(name, expectedVersion, pointerStream/);
  assert.match(accountDb,/FileTreePointerPrefix/);
  assert.match(binary,/database\.PutIndexedStreamingFileAsync\(/);
  assert.match(binary,/database\.ReadStreamingFileAsync\(/);
  assert.match(binary,/CopyVerifiedToAsync\(/);
  assert.match(adapter,/IVaultChunkStore/);
  assert.match(adapter,/SHA256\.HashData\(ciphertext\.Span\)/);
  assert.match(encrypted,/VaultV2Envelope\.Seal\(/);
  assert.match(encrypted,/VaultV2Envelope\.Open\(/);
  assert.match(encrypted,/SHA256\.HashData\(stored\)/);
  assert.match(encrypted,/WriteContentAddressedAsync\(/);
  assert.match(smoke,/database\.PutIndexedStreamingFileAsync\(/);
  assert.match(smoke,/database\.ReadStreamingFileAsync\(/);
  assert.match(smoke,/Stale streamed file record write was accepted/);
});

test('linked SVTREE3 streaming file stages typed nodes through existing history-verified queue',()=>{
  const core=path.join(__dirname,'..','src','StellarVault.Core','Services');
  const policy=fs.readFileSync(path.join(core,'VaultLinkedTreeFilePolicy.cs'),'utf8');
  const workspace=fs.readFileSync(path.join(core,'VaultLinkedDraftWorkspace.cs'),'utf8');
  const multipart=fs.readFileSync(path.join(core,'VaultLinkedMultipartFilePolicy.cs'),'utf8');
  const queued=fs.readFileSync(path.join(core,'VaultQueuedCreatePolicy.cs'),'utf8');
  const smoke=fs.readFileSync(path.join(__dirname,
    'StellarVault.Core.CryptoTests','LinkedDraftSmoke.cs'),'utf8');
  const ui=fs.readFileSync(path.join(__dirname,'..','src','StellarVault.UI',
    'Components','VaultLinkedLocalQueuePanel.razor'),'utf8');
  const browser=fs.readFileSync(path.join(__dirname,'..','src','StellarVault.Pwa',
    'Services','BrowserVaultNativeFileAccess.cs'),'utf8');
  const js=fs.readFileSync(path.join(__dirname,'..','src','StellarVault.Pwa',
    'wwwroot','js','vault-v2-download.js'),'utf8');
  assert.match(policy,/public const string Prefix = "SVTREE3:"/);
  assert.match(policy,/tree-root-not-staged/);
  assert.match(policy,/tree-child-not-historically-confirmed/);
  assert.match(policy,/tree-child-changed-on-ledger/);
  assert.match(policy,/VaultStreamingFileTree\.ReadVerifiedAsync/);
  assert.match(workspace,/public async Task<VaultStreamingFileTree\.Result> StageTreeFileAsync/);
  assert.match(workspace,/VaultStreamingFileTree\.WriteAsync\(input, fileName, contentType/);
  assert.match(multipart,/VaultLinkedTreeFilePolicy\.Problems\(mutation, all, remote\)/);
  assert.match(queued,/VaultLinkedTreeFilePolicy\.Prefix/);
  assert.match(ui,/DraftWorkspace\.StageTreeFileAsync/);
  assert.match(ui,/NativeFiles\.SaveAsync\(prepared\.FileName, reader\)/);
  assert.match(ui,/SaveVerifiedTreeExportAsync\(/);
  assert.match(ui,/VaultLinkedTreeFilePolicy\.ReadAsync\(\s*recordId, records, Stream\.Null\)/);
  assert.match(browser,/new DotNetStreamReference\(content, leaveOpen: true\)/);
  assert.match(js,/async saveStream\(filename, dotnetStreamReference\)/);
  assert.match(smoke,/RunLinkedTreeFileSmokeAsync\(\)/);
  assert.match(smoke,/Streaming file root was ready before its children/);
});
