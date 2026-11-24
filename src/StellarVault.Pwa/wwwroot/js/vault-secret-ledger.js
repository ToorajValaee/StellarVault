// Independent Stellar S-key vault v1 protocol. No mnemonic/HD fallbacks.
// Changes to the network are always quoted before explicit execution.
// Public mainnet mutation is deliberately gated pending external audit.
window.stellarVaultSecretLedger = (() => {
  'use strict';
  const sessions = new Map();
  // Public-only process-lifetime tombstones survive lock/reopen in this JS realm.
  // They are deliberately NOT durable and never store a seed, quote, or payload.
  const unresolvedByVault = new Map();
  const vaultScope = (root, passphrase) => root+'|'+passphrase;
  const text = new TextEncoder(), decoder = new TextDecoder('utf-8', { fatal:true });
  const HDR='skh:', CHUNK='skc:', MAX_OPS=90, MAX_CHAIN=64;
  const MAINNET='Public Global Stellar Network ; September 2015';
  const bytes = value => Uint8Array.from(atob(value), c=>c.charCodeAt(0));
  const b64 = value => btoa(String.fromCharCode(...value));
  const toXlm = n => {
    const v=BigInt(n), abs=v<0n?-v:v;
    return (v<0n?'-':'')+(abs/10000000n).toString()+'.'+(abs%10000000n).toString().padStart(7,'0');
  };
  const hash = async value => new Uint8Array(await crypto.subtle.digest('SHA-256',value));
  const equal = (a,b) => a.length===b.length && a.every((c,i)=>c===b[i]);
  const validId = id => typeof id==='string' && /^[0-9a-f]{16}$/.test(id);
  const fail = message => { throw new Error(message); };
  function session(id) {
    const s=sessions.get(id);
    if(!s) fail('Unlock the Secret Key vault first.');
    return s;
  }
  async function verifiedNetwork(url, passphrase) {
    const u=new URL(String(url));
    const local=['localhost','127.0.0.1','[::1]'].includes(u.hostname);
    if((u.protocol!=='https:' && !(local&&u.protocol==='http:')) ||
       u.username||u.password||u.search||u.hash||u.pathname!=='/')
      fail('A trusted Horizon origin is required.');
    const r=await fetch(u.origin,{cache:'no-store',headers:{Accept:'application/json'}});
    if(!r.ok) fail('Could not verify the Horizon network.');
    const n=await r.json();
    if(n.network_passphrase!==passphrase) fail('Selected network does not match Horizon.');
    const ledgers=await fetch(u.origin+'/ledgers?order=desc&limit=1',{cache:'no-store'});
    if(!ledgers.ok) fail('Could not read network reserve settings.');
    const ledger=(await ledgers.json())?._embedded?.records?.[0];
    const reserve=Number(ledger?.base_reserve_in_stroops);
    if(!Number.isSafeInteger(reserve)||reserve<=0) fail('Invalid network base reserve.');
    const fees=await fetch(u.origin+'/fee_stats',{cache:'no-store'});
    if(!fees.ok) fail('Network fee statistics unavailable.');
    const stats=await fees.json();
    const baseFee=Number(stats?.fee_charged?.p90 || stats?.last_ledger_base_fee || 100);
    if(!Number.isSafeInteger(baseFee)||baseFee<=0) fail('Invalid network fee quote.');
    return {origin:u.origin,reserve:BigInt(reserve),fee:BigInt(Math.max(baseFee,100))};
  }
  function signer(account,root) {
    const signer=(account.signers||[]).find(x=>x.type==='ed25519_public_key' && x.key===root);
    const weight=Number(signer?.weight||0);
    const medium=Number(account.thresholds?.med_threshold);
    const high=Number(account.thresholds?.high_threshold);
    const low=Number(account.thresholds?.low_threshold);
    if(!Number.isInteger(weight)||!Number.isInteger(medium)||!Number.isInteger(high)||
       !Number.isInteger(low)||weight<1||low<0||medium<0||high<0||
       weight<low||weight<medium||weight<high)
      fail('Linked account lacks valid root signing authority.');
    return low>=1 && medium>=1 && high>=1;
  }
  function next(account,sdk) {
    const data=account.data_attr||{};
    const names=['sv:next:v1','sv:next'].filter(k=>Object.hasOwn(data,k));
    if(names.length>1) fail('Conflicting linked-account pointers.');
    if(!names.length) return null;
    let v;
    try{v=decoder.decode(bytes(data[names[0]]));}catch{fail('Corrupt linked-account pointer.');}
    if(!sdk.StrKey.isValidEd25519PublicKey(v)) fail('Invalid linked-account pointer.');
    return v;
  }
  async function chain(s) {
    const seen=new Set(), nodes=[];
    let id=s.root;
    for(let i=0;i<MAX_CHAIN && id;i++) {
      if(seen.has(id))fail('Cycle detected in linked-account vault.');
      seen.add(id);
      const account=await s.server.loadAccount(id);
      const mutationSafe=signer(account,s.root);
      // A fresh Stellar root can have zero thresholds. Read its encrypted
      // vault safely, but never quote or sign a mutation under zero thresholds.
      if(i>0 && !mutationSafe)
        fail('Child account has unsafe zero signing thresholds.');
      if(i>0 && (account.signers||[]).some(x =>
          x.key!==s.root && Number(x.weight||0)>0))
        fail('Child account has another active signer; single-secret chain is not exclusively controlled by the root.');
      if(i===0 && Object.hasOwn(account.data_attr||{},'sv:registry:v2'))
        fail('HD-v2 vault root cannot be used as an independent Secret Key vault.');
      if(Object.keys(account.data_attr||{}).some(name =>
          /^svh:|^svc:/.test(name)))
        fail('A different or historical vault record format is present. Read-only migration and integrity verification are required before using Secret Key v1 writes.');
      nodes.push(account);
      id=next(account,s.sdk);
    }
    if(id)fail('Linked-account chain exceeds the safe limit.');
    return nodes;
  }
  async function open(id,secret,horizon,passphrase) {
    if(sessions.has(id))fail('This session is already unlocked.');
    const sdk=window.StellarSdk;
    if(!sdk?.Keypair||!sdk?.Horizon?.Server||!sdk?.StrKey?.isValidEd25519SecretSeed(secret))
      fail('A valid Stellar S-key and bundled SDK are required.');
    const net=await verifiedNetwork(horizon,passphrase);
    const pair=sdk.Keypair.fromSecret(secret),root=pair.publicKey();
    const server=new sdk.Horizon.Server(net.origin,{allowHttp:net.origin.startsWith('http:')});
    const seed=pair.rawSecretKey().slice();
    let key;
    try {
      const ikm=await crypto.subtle.importKey('raw',seed,'HKDF',false,['deriveKey']);
      const salt=await hash(text.encode('StellarVault|SecretChain|v1|'+passphrase+'|'+root));
      key=await crypto.subtle.deriveKey({
        name:'HKDF',hash:'SHA-256',salt,info:text.encode('vault-records|skh/skc|v1')
      },ikm,{name:'AES-GCM',length:256},false,['encrypt','decrypt']);
    } finally {seed.fill(0);}
    const prior=unresolvedByVault.get(vaultScope(root,passphrase))||null;
    const state={sdk,root,pair,server,passphrase,key,net,quotes:new Map(),
       verifiedHistories:new Set(),inFlight:false,
      needsReconciliation:!!prior,uncertain:prior?{...prior}:null};
    await chain(state);
    sessions.set(id,state);
    return {RootAccountId:root};
  }
  function clearQuotes(s) {
    for(const q of s.quotes.values()) {
      if(q.cipher)q.cipher.fill(0);
      if(q.child){try{q.child.rawSecretKey().fill(0);}catch{}}
    }
    s.quotes.clear();
  }
  function close(id) {
    const s=sessions.get(id);
    if(s) {
      clearQuotes(s);
      try{s.pair.rawSecretKey().fill(0);}catch{}
      sessions.delete(id);
    }
  }
  function recordKey(id,index,generation) {return CHUNK+id+':'+generation+':'+index.toString(36);}
  function header(id) {return HDR+id;}
  function generation() {
    return Array.from(crypto.getRandomValues(new Uint8Array(4)),x=>x.toString(16).padStart(2,'0')).join('');
  }
  async function seal(s,id,gen,draft) {
    const plain=text.encode(JSON.stringify({Id:id,Title:draft.Title,Content:draft.Content,
      FolderId:draft.FolderId||null,Tags:draft.Tags||[],IsFolder:!!draft.IsFolder,
      UserName:draft.UserName||'',Url:draft.Url||'',Notes:draft.Notes||''}));
    const nonce=crypto.getRandomValues(new Uint8Array(12));
    const aad=text.encode('StellarVault|SecretChain|v1|'+s.passphrase+'|'+s.root+'|'+id+'|'+gen);
    let encrypted;
    try {encrypted=new Uint8Array(await crypto.subtle.encrypt({
      name:'AES-GCM',iv:nonce,additionalData:aad,tagLength:128
    },s.key,plain));}
    finally {plain.fill(0);}
    const envelope=new Uint8Array(nonce.length+encrypted.length);
    envelope.set(nonce);envelope.set(encrypted,12);encrypted.fill(0);
    return envelope;
  }
  async function unseal(s,id,gen,envelope) {
    if(envelope.length<28)fail('Truncated record ciphertext.');
    const aad=text.encode('StellarVault|SecretChain|v1|'+s.passphrase+'|'+s.root+'|'+id+'|'+gen);
    const plain=new Uint8Array(await crypto.subtle.decrypt({
      name:'AES-GCM',iv:envelope.slice(0,12),additionalData:aad,tagLength:128
    },s.key,envelope.slice(12)));
    try {
      const obj=JSON.parse(decoder.decode(plain));
      if(obj.Id!==id||typeof obj.Title!=='string'||typeof obj.Content!=='string'||
         !Array.isArray(obj.Tags)||typeof obj.IsFolder!=='boolean')
        fail('Invalid decrypted record structure.');
      return obj;
    }finally {plain.fill(0);}
  }
  async function encodeManifest(cipher,gen,count) {
    const raw=new Uint8Array(47),view=new DataView(raw.buffer);
    raw[0]=1;
    raw.set(text.encode(gen),1);
    view.setUint16(9,count,false);
    view.setUint32(11,cipher.length,false);
    raw.set(await hash(cipher),15);
    return raw;
  }
  function parseManifest(raw) {
    if(raw.length!==47||raw[0]!==1)fail('Invalid Secret Key record manifest.');
    const gen=decoder.decode(raw.slice(1,9));
    if(!/^[0-9a-f]{8}$/.test(gen))fail('Invalid record generation.');
    const view=new DataView(raw.buffer,raw.byteOffset,raw.byteLength);
    const count=view.getUint16(9,false),size=view.getUint32(11,false);
    if(count<1||count>MAX_OPS-1||size<28||size>count*64||size<=(count-1)*64)
      fail('Invalid ciphertext chunk count.');
    return {gen,count,size,digest:raw.slice(15,47)};
  }
  async function inventory(s) {
    const nodes=await chain(s),records=[],locations=new Map();
    for(const account of nodes) {
      const data=account.data_attr||{};
      for(const name of Object.keys(data).filter(k=>/^skh:[0-9a-f]{16}$/.test(k))) {
        const id=name.slice(4),m=parseManifest(bytes(data[name]));
        if(locations.has(id))fail('Duplicate record ID across linked accounts.');
        const cipher=new Uint8Array(m.size);
        for(let i=0;i<m.count;i++) {
          const k=recordKey(id,i,m.gen);
          if(!Object.hasOwn(data,k))fail('Record chunk missing: '+id);
          const part=bytes(data[k]);
          const expected=Math.min(64,m.size-i*64);
          if(part.length!==expected)fail('Record ciphertext chunk length mismatch.');
          cipher.set(part,i*64);
        }
        if(!equal(await hash(cipher),m.digest))fail('Record ciphertext checksum mismatch.');
        let record;
        try {record=await unseal(s,id,m.gen,cipher);}
        finally {cipher.fill(0);}
        locations.set(id,{account,manifest:m,rawHeader:data[name]});
        records.push(record);
      }
    }
    for(const record of records) {
      if(record.FolderId && !records.some(x=>x.Id===record.FolderId&&x.IsFolder))
        fail('A referenced parent folder does not exist.');
    }
    return {nodes,records,locations};
  }
  async function read(id) {return (await inventory(session(id))).records;}
  async function info(id) {
    const s=session(id),v=await inventory(s),root=v.nodes[0];
    const native=(root.balances||[]).find(x=>x.asset_type==='native');
    const bal=native?.balance||'0';
    const sub=Number(root.subentry_count||0),sponsoring=Number(root.num_sponsoring||0),
      sponsored=Number(root.num_sponsored||0);
    const min=s.net.reserve*BigInt(Math.max(0,2+sub+sponsoring-sponsored));
    const available=decimalStroops(bal)-min-decimalStroops(native?.selling_liabilities||'0');
    return {RootAccountId:s.root,TailAccountId:v.nodes.at(-1).account_id,
      TotalAccounts:v.nodes.length,Records:v.records.filter(x=>!x.IsFolder).length,
      Folders:v.records.filter(x=>x.IsFolder).length,BalanceNative:bal,
      SpendableNative:toXlm(available>0n?available:0n),SponsoredReserveNative:'0.0000000'};
  }
  function decimalStroops(value) {
    const str=String(value||'0');
    if(!/^\d+(?:\.\d{1,7})?$/.test(str))fail('Invalid Stellar balance.');
    const [whole,fract='']=str.split('.');
    return BigInt(whole)*10000000n+BigInt(fract.padEnd(7,'0'));
  }
  function validateDraft(draft) {
    if(!draft||typeof draft.Title!=='string'||draft.Title.trim().length<1||
       draft.Title.length>256||typeof draft.Content!=='string'||
       (!draft.IsFolder&&!draft.Content)||
       !Array.isArray(draft.Tags||[])||
       (draft.Tags||[]).length>24||
       (draft.Tags||[]).some(t=>typeof t!=='string'||t.length>64)||
       (draft.FolderId&&!validId(draft.FolderId)))
      fail('Invalid record or folder fields.');
  }
  function requireAcyclicPublishedParent(records,recordId,parentId) {
    if(!parentId)return;
    const byId=new Map(records.map(record=>[record.Id,record]));
    const seen=new Set([recordId]);
    let cursor=parentId;
    while(cursor) {
      if(seen.has(cursor))fail('Folder move creates a parent cycle.');
      seen.add(cursor);
      const parent=byId.get(cursor);
      if(!parent||!parent.IsFolder)
        fail('Updated record parent must be a published folder.');
      cursor=parent.FolderId;
    }
  }
  function chosenNode(s,view,old) {
    if(old)return old.account;
    return view.nodes.at(-1);
  }
  async function plan(id,kind,recordId,draft) {
    const s=session(id);
    if(s.passphrase===MAINNET)fail('Secret Key ledger mutations are Testnet-only pending audit.');
    if(s.inFlight)fail('A ledger operation is already in progress.');
    if(s.needsReconciliation || unresolvedByVault.has(vaultScope(s.root,s.passphrase)))
      fail('A previous submission is unresolved for this vault; reconcile ledger history before another write.');
    if(!['create','update','delete','expand'].includes(kind))fail('Invalid ledger operation.');
    if(kind!=='expand'&&!validId(recordId))fail('Record identifier is invalid.');
    if(kind==='create'||kind==='update')validateDraft(draft);
    // Re-fetch network identity, fees and base reserve for each new approval.
    s.net=await verifiedNetwork(s.net.origin,s.passphrase);
    const v=await inventory(s),old=v.locations.get(recordId);
    if(v.nodes.some(node => !signer(node,s.root)))
      fail('Vault writes are blocked: one or more linked accounts have zero signing thresholds. Set positive low, medium and high thresholds in a trusted Stellar wallet first. Reading remains available.');
    if(kind==='create'&&old)fail('Record already exists.');
    if((kind==='update'||kind==='delete')&&!old)fail('Published record was not found.');
    if((kind==='update'||kind==='create') && draft) {
      if(kind==='update' && old && !!draft.IsFolder!==!!v.records.find(x=>x.Id===recordId)?.IsFolder)
        fail('An existing record cannot change its folder type.');
      requireAcyclicPublishedParent(v.records,recordId,draft.FolderId);
    }
    if(kind==='delete' && v.records.some(x=>x.FolderId===recordId))
      fail('Delete nested records or folders first.');
    if(kind==='expand')return quoteExpansion(s,v,id);
    const node=chosenNode(s,v,old);
    const oldM=old?.manifest;
    const gen=kind==='delete'?null:generation();
    const cipher=gen?await seal(s,recordId,gen,draft):null;
    const count=cipher?Math.ceil(cipher.length/64):0;
    const newKeys=cipher?Array.from({length:count},(_,i)=>recordKey(recordId,i,gen)):[];
    const priorKeys=oldM?Array.from({length:oldM.count},(_,i)=>recordKey(recordId,i,oldM.gen)):[];
    const writeOps=(kind==='delete'?1:count+1)+priorKeys.length;
    const newUnits=(kind==='create'?1:0)+newKeys.length;
    const unitsReclaimed=priorKeys.length+(kind==='delete'?1:0);
    // Newly staged keys consume reserve before old keys are removed;
    // require peak headroom even if the net change is negative.
    const additional=BigInt(newUnits)*s.net.reserve;
    // A record must fit in a single atomic transaction even on an empty account.
    // Never allocate an account for an oversized write that cannot succeed there.
    const minimumOps=writeOps+(kind==='create'&&node.account_id!==s.root?1:0);
    if(minimumOps>MAX_OPS) {
      cipher?.fill(0);
      fail('Record exceeds atomic transaction limit; allocating another account cannot fix it.');
    }
    // A tail account needs one free ManageData entry for the future sv:next:v1 link.
    // For new records, reserve that slot rather than filling the last entry.
    const isTail=node.account_id===v.nodes.at(-1).account_id;
    // A tail must retain capacity for the next signed sv:next:v1 pointer even
    // after an update. Existing non-tail nodes already have their pointer.
    // Deletion cannot consume entries, so its peak capacity is unchanged.
    const reservedCeiling=kind==='delete'||!isTail ? 1000 : 999;
    if(Number(node.subentry_count||0)+newUnits>reservedCeiling) {
      cipher?.fill(0);
      if(kind!=='create')
        fail('Original record account has insufficient subentry capacity; updating it requires a separate migration protocol.');
      if(newUnits>997)
        fail('Record needs too many Stellar data entries to fit on a fresh account.');
      if(Number(node.subentry_count||0)>=1000)
        fail('Tail account has no free entry for a chain link. Manual recovery is required; a new account cannot be safely attached.');
      fail('SV_ACCOUNT_CAPACITY_REQUIRED: Current account cannot hold this record; account capacity must be reviewed before saving.');
    }
    const root=v.nodes[0],native=root.balances?.find(x=>x.asset_type==='native');
    const bal=decimalStroops(native?.balance);
    const used=BigInt(2+Number(root.subentry_count||0)+Number(root.num_sponsoring||0)-
      Number(root.num_sponsored||0))*s.net.reserve;
    const spend=bal-used-decimalStroops(native?.selling_liabilities||'0');
    // Source account for writes is the node; when node is not root, its
    // reserve headroom must be independently available.
    const ownerNative=node.balances?.find(x=>x.asset_type==='native');
    const ownerBalance=decimalStroops(ownerNative?.balance);
    const ownerMin=BigInt(2+Number(node.subentry_count||0)+Number(node.num_sponsoring||0)-
      Number(node.num_sponsored||0))*s.net.reserve;
    const ownerFree=ownerBalance-ownerMin-decimalStroops(ownerNative?.selling_liabilities||'0');
    // The root can fund its child using a Payment operation in the *same*
    // atomic transaction. This avoids creating unusable minimum-funded nodes.
    const topUp=node.account_id===s.root?0n:(ownerFree<additional?additional-ownerFree:0n);
    const opCount=writeOps+(topUp>0n?1:0);
    if(opCount>MAX_OPS){cipher?.fill(0);fail('Record exceeds atomic transaction limit.');}
    const fees=BigInt(opCount)*s.net.fee;
    if((node.account_id===s.root && ownerFree<additional) ||
       spend<fees+topUp+(node.account_id===s.root?additional:0n)) {
      cipher?.fill(0);
      fail('Insufficient native-asset balance for account reserve top-up and network fees.');
    }
    const fingerprint=old?.rawHeader||null;
    const quoteId=Array.from(crypto.getRandomValues(new Uint8Array(16)),x=>x.toString(16).padStart(2,'0')).join('');
    const quote={kind,recordId,draft,gen,cipher,count,newKeys,priorKeys,node:node.account_id,
      fingerprint,opCount,topUp,created:Date.now(),fee:fees,reserve:additional,root:s.root,
      feeBase:s.net.fee,reserveBase:s.net.reserve};
    clearQuotes(s);s.quotes.set(quoteId,quote);
    return {QuoteId:quoteId,Kind:kind,Operations:opCount,Transactions:1,
      NetworkFeeXlm:toXlm(fees),PeakReserveXlm:toXlm(additional),
      NetReserveChangeXlm:toXlm(BigInt(newUnits-unitsReclaimed)*s.net.reserve),
      FundingTopUpXlm:toXlm(topUp),CreatesChildNode:false,TargetAccountId:node.account_id};
  }
  async function quoteExpansion(s,v,id) {
    if(v.nodes.length>=MAX_CHAIN)fail('Chain already reached the supported limit.');
    const tail=v.nodes.at(-1),tailId=tail.account_id;
    if(next(tail,s.sdk))fail('Tail was modified; refresh first.');
    if(Number(tail.subentry_count||0)>=1000)
      fail('Tail account has no free entry for a chain link. Manual recovery is required; another account cannot be safely attached.');
    if(Object.hasOwn(tail.data_attr||{},'sv:next:v1'))fail('Tail link is occupied.');
    const childFunding=3n*s.net.reserve,pointerReserve=s.net.reserve;
    const opCount=tailId===s.root?3:4,fees=BigInt(opCount)*s.net.fee;
    const root=v.nodes[0],native=root.balances?.find(x=>x.asset_type==='native');
    const rootFree=decimalStroops(native?.balance)-
      BigInt(2+Number(root.subentry_count||0)+Number(root.num_sponsoring||0)-
        Number(root.num_sponsored||0))*s.net.reserve-
      decimalStroops(native?.selling_liabilities||'0');
    const required=childFunding+pointerReserve+fees;
    if(rootFree<required)fail('Root account cannot afford the child reserve, link and network fees.');
    // Generate the temporary child signing key only after capacity/funding checks.
    const child=s.sdk.Keypair.random(),childId=child.publicKey();
    const quoteId=Array.from(crypto.getRandomValues(new Uint8Array(16)),x=>x.toString(16).padStart(2,'0')).join('');
    clearQuotes(s);s.quotes.set(quoteId,{kind:'expand',child,childId,tailId,created:Date.now(),
      opCount,fee:fees,reserve:childFunding+pointerReserve,required,
      feeBase:s.net.fee,reserveBase:s.net.reserve});
    return {QuoteId:quoteId,Kind:'expand',Operations:opCount,Transactions:1,
      NetworkFeeXlm:toXlm(fees),PeakReserveXlm:toXlm(childFunding+pointerReserve),
      NetReserveChangeXlm:toXlm(childFunding+pointerReserve),
      CreatesChildNode:true,TargetAccountId:childId,FundingTopUpXlm:"0.0000000"};
  }
  async function submit(id,quoteId,checkpoint=null) {
    const s=session(id),q=s.quotes.get(quoteId);
    if(!q)fail('No matching reviewed quote. Estimate again.');
    if(s.inFlight)fail('Another ledger submission is in progress.');
    if(s.needsReconciliation || unresolvedByVault.has(vaultScope(s.root,s.passphrase)))
      fail('An earlier submission for this vault requires reconciliation before another transaction.');
    s.quotes.delete(quoteId);
    if(Date.now()-q.created>120000) {
      if(q.cipher)q.cipher.fill(0);
      if(q.child){try{q.child.rawSecretKey().fill(0);}catch{}}
      fail('Fee quote expired; review again.');
    }
    if(s.passphrase===MAINNET)fail('Secret Key ledger mutations are Testnet-only pending audit.');
    s.inFlight=true;
    try{
      const freshNet=await verifiedNetwork(s.net.origin,s.passphrase);
      if(freshNet.fee!==q.feeBase || freshNet.reserve!==q.reserveBase)
        fail('Network fees or reserves changed. Review a fresh quote before signing.');
      const current=await inventory(s);
      const root=await s.server.loadAccount(s.root);
      const fee=Number(s.net.fee);
      let builder=new s.sdk.TransactionBuilder(root,{fee:String(fee),networkPassphrase:s.passphrase});
      if(q.kind==='expand') {
        if(current.nodes.at(-1).account_id!==q.tailId ||
           Object.hasOwn(current.nodes.at(-1).data_attr||{},'sv:next:v1'))
          fail('Tail was changed before approval; quote is invalid.');
        // Fund account + root signer reserve. The child master signs the
        // one-time setup and is disabled in the same atomic transaction.
        builder.addOperation(s.sdk.Operation.createAccount({
          source:s.root,destination:q.childId,startingBalance:toXlm(3n*s.net.reserve)
        }));
        builder.addOperation(s.sdk.Operation.setOptions({
          source:q.childId,masterWeight:0,lowThreshold:1,medThreshold:1,highThreshold:1,
          signer:{ed25519PublicKey:s.root,weight:1}
        }));
        if(q.tailId!==s.root)
          builder.addOperation(s.sdk.Operation.payment({
            source:s.root,destination:q.tailId,asset:s.sdk.Asset.native(),
            amount:toXlm(s.net.reserve)
          }));
        builder.addOperation(s.sdk.Operation.manageData({
          source:q.tailId,name:'sv:next:v1',value:s.pair.rawPublicKey().constructor.from(text.encode(q.childId))
        }));
      } else {
        const latest=current.locations.get(q.recordId);
        if((latest?.rawHeader||null)!==q.fingerprint ||
           (q.kind==='create'&&latest)||(q.kind!=='create'&&!latest))
          fail('Record changed before approval. Reconcile the ledger first.');
        if(!current.nodes.some(x=>x.account_id===q.node))
          fail('Record storage account no longer belongs to this chain.');
        if(q.topUp>0n) {
          builder.addOperation(s.sdk.Operation.payment({
            source:s.root,destination:q.node,asset:s.sdk.Asset.native(),amount:toXlm(q.topUp)
          }));
        }
        const writes=[];
        if(q.kind==='delete')
          writes.push({name:header(q.recordId),value:null});
        else {
          for(let i=0;i<q.count;i++)
            writes.push({name:q.newKeys[i],value:q.cipher.slice(i*64,Math.min((i+1)*64,q.cipher.length))});
          writes.push({name:header(q.recordId),value:await encodeManifest(q.cipher,q.gen,q.count)});
        }
        for(const k of q.priorKeys) writes.push({name:k,value:null});
        for(const w of writes)
          // Browser SDK's Buffer is a different class from Uint8Array (and
          // Node's Buffer). Use its own public-key buffer constructor.
          builder.addOperation(s.sdk.Operation.manageData({source:q.node,name:w.name,
            value:w.value===null?null:s.pair.rawPublicKey().constructor.from(w.value)}));
      }
      const tx=builder.setTimeout(60).build();
      tx.sign(s.pair);
      if(q.kind==='expand')tx.sign(q.child);
      let result;
      // Recheck immediately before transmission: a different already-approved
      // session may have submitted while this one was fetching network state.
      if(unresolvedByVault.has(vaultScope(s.root,s.passphrase)))
        fail('Another submission for this vault is unresolved; do not transmit a second transaction.');
      // Record the network-bound signed hash BEFORE broadcasting. A timeout may
      // follow a successful submission; keep this public identity for recovery.
      // This is not proof of inclusion and NEVER authorizes an automatic retry.
      const rawHash=tx.hash();
      if(!rawHash||rawHash.length!==32)fail('Signed transaction hash unavailable; no broadcast attempted.');
      const signedHash=Array.from(rawHash,b=>b.toString(16).padStart(2,'0')).join('');
      // Journal-backed callers MUST durably CAS-checkpoint this exact hash
      // before the first byte is broadcast. If the callback rejects, do not
      // transmit; leave the caller's Submitting journal in quarantine.
      if(checkpoint) {
        if(typeof checkpoint.invokeMethodAsync!=='function')
          fail('Durable signed-hash callback is invalid; no transaction was sent.');
        await checkpoint.invokeMethodAsync('CheckpointSignedHash',signedHash);
      }
      if(sessions.get(id)!==s)
        fail('Signing session was revoked before broadcast; keep the journal quarantined.');
      // From this point any network failure may mean the transaction was accepted.
      // Keep this session quarantined until the connected outcome is verified.
      s.needsReconciliation=true;
      // Public-only, session-lifetime diagnostic. No record plaintext, keys, or ciphertext.
      s.uncertain={Kind:q.kind,RootAccountId:s.root,
        TargetAccountId:q.kind==='expand'?q.childId:q.node,
        RecordId:q.kind==='expand'?null:q.recordId,
        TransactionHash:signedHash,NetworkPassphrase:s.passphrase,
        HorizonUrl:s.net.origin};
      unresolvedByVault.set(vaultScope(s.root,s.passphrase),{...s.uncertain});
      try {result=await s.server.submitTransaction(tx);}
      catch {fail('Transaction outcome is uncertain. Reconcile the ledger before any retry.');}
      if(!result?.hash || result.successful===false)
        fail('No confirmed Stellar transaction hash. Reconcile before retry.');
      if(!/^[0-9a-f]{64}$/i.test(result.hash) || result.hash.toLowerCase()!==signedHash)
        fail('Horizon returned a different transaction hash. Keep the signed hash quarantined and investigate.');
      // An accepted response must still be checked against the new ledger state.
      // If Horizon is temporarily behind, report as uncertain rather than resubmitting.
      try {
        const updated=await inventory(s);
        if(q.kind==='expand') {
          if(updated.nodes.at(-1)?.account_id!==q.childId)fail('Child link not yet visible.');
        } else if(q.kind==='delete') {
          if(updated.locations.has(q.recordId))fail('Deletion not yet visible.');
        } else {
          const newRecord=updated.locations.get(q.recordId);
          if(!newRecord||newRecord.manifest.gen!==q.gen)fail('New record not yet visible.');
        }
      }catch{
        fail('Transaction accepted ('+result.hash+') but state verification is pending. Do not retry blindly.');
      }
      s.needsReconciliation=false;
      s.uncertain=null;
      unresolvedByVault.delete(vaultScope(s.root,s.passphrase));
      return result.hash;
    }finally {
      if(q.cipher)q.cipher.fill(0);
      if(q.child){try{q.child.rawSecretKey().fill(0);}catch{}}
      s.inFlight=false;
    }
  }
  async function recordRevision(id,recordId) {
    if(!validId(recordId))fail('Invalid linked record identifier.');
    const s=session(id);
    if(s.inFlight)fail('A linked transaction is in progress.');
    const current=await inventory(s);
    const record=current.locations.get(recordId);
    return record?.rawHeader||null;
  }
  // Independent read-only history check. The queue stores the signed hash
  // before broadcasting. Re-fetch its envelope, verify its network-bound hash
  // AND root signature, then decrypt the exact historical operation ciphertext.
  // No quote, signer state, network transaction or queue entry is changed here.
  async function verifyHistoricalRecord(id,txHash,kind,recordId,draft,previousHeader) {
    const s=session(id);
    if(!/^[0-9a-f]{64}$/.test(txHash)||!validId(recordId)||
       !['create','update','delete'].includes(kind))
      fail('Invalid historical linked record verification scope.');
    if((kind==='create'&&previousHeader!==null) ||
       (kind!=='create'&&(typeof previousHeader!=='string'||!previousHeader)))
      fail('Historical mutation original manifest was not bound to the queue.');
    if(kind!=='delete')validateDraft(draft);
    // Re-establish the selected network before accepting the historical receipt.
    const verified=await verifiedNetwork(s.net.origin,s.passphrase);
    if(verified.origin!==s.net.origin)fail('Selected Horizon endpoint changed.');
    const endpoint=s.net.origin+'/transactions/'+txHash;
    const response=await fetch(endpoint,{cache:'no-store',headers:{Accept:'application/json'}});
    if(!response.ok||new URL(response.url||endpoint).origin!==s.net.origin)
      fail('Historical transaction receipt was unavailable or redirected.');
    const receipt=await response.json();
    if(receipt?.successful!==true||receipt?.hash?.toLowerCase()!==txHash||
       receipt?.source_account!==s.root||
       typeof receipt?.envelope_xdr!=='string'||receipt.envelope_xdr.length>65536)
      fail('Historical receipt does not bind a successful root transaction.');
    const tx=s.sdk.TransactionBuilder.fromXDR(receipt.envelope_xdr,s.passphrase);
    const rawHash=tx.hash();
    if(rawHash?.length!==32 ||
       Array.from(rawHash,x=>x.toString(16).padStart(2,'0')).join('')!==txHash ||
       tx.source!==s.root || !Array.isArray(tx.operations) ||
       tx.operations.length<1||tx.operations.length>MAX_OPS)
      fail('Historical signed envelope does not bind expected transaction.');
    const rootKey=s.sdk.Keypair.fromPublicKey(s.root);
    if(!Array.isArray(tx.signatures)||!tx.signatures.some(sig=>{
      try{return rootKey.verify(rawHash,new Uint8Array(sig.signature()));}
      catch{return false;}
    }))fail('Signed historical envelope lacks a valid root signature.');

    const nodes=await chain(s),nodeIds=new Set(nodes.map(n=>n.account_id));
    let owner=null,headerRaw=null,headerAt=-1,headerRemoved=false;
    const chunks=new Map(),removed=new Set();
    let newGen=null,oldGen=null,oldCount=0,topUpSeen=false;
    if(previousHeader!==null) {
      const old=parseManifest(bytes(previousHeader));
      oldGen=old.gen;oldCount=old.count;
    }
    for(let index=0;index<tx.operations.length;index++){
      const op=tx.operations[index],source=op.source||tx.source;
      if(op.type==='payment'){
        if(topUpSeen||index!==0||source!==s.root||!op.destination||
           !nodeIds.has(op.destination)||op.destination===s.root||
           op.asset?.getCode?.()!=='XLM'||!(Number(op.amount)>0))
          fail('Historical reserve payment does not match linked record account.');
        owner=op.destination;topUpSeen=true;
        continue;
      }
      if(op.type!=='manageData')fail('Unexpected historical operation in record mutation.');
      const name=op.name;
      if(typeof name!=='string'||
         (name!=='skh:'+recordId&&!name.startsWith('skc:'+recordId+':')))
        fail('Historical transaction modifies unrelated data.');
      if(!nodeIds.has(source)||(owner!==null&&source!==owner))
        fail('Historical record operation source is outside the linked chain.');
      owner=source;
      const value=op.value;
      if(name==='skh:'+recordId){
        if(headerAt!==-1)fail('Duplicate historical record manifest operation.');
        headerAt=index;
        if(value==null)headerRemoved=true;
        else {
          if(kind==='delete')fail('Delete transaction wrote a record manifest.');
          headerRaw=new Uint8Array(value);
          const m=parseManifest(headerRaw);
          newGen=m.gen;
        }
      } else if(value==null){
        if(headerAt===-1)fail('Historical chunk removal preceded the record manifest.');
        if(!name.startsWith('skc:'+recordId+':'))fail('Unexpected historical deletion key.');
        if(removed.has(name))fail('Duplicate historical chunk deletion.');
        removed.add(name);
      } else {
        if(headerAt!==-1||kind==='delete')fail('Historical ciphertext chunk followed its manifest.');
        if(chunks.has(name))fail('Duplicate historical encrypted record chunk.');
        const chunk=new Uint8Array(value);
        if(chunk.length<1||chunk.length>64)fail('Invalid historical ciphertext chunk length.');
        chunks.set(name,chunk);
      }
    }
    if(!owner||headerAt===-1||
       (kind==='delete'?!headerRemoved:!headerRaw) ||
       (kind==='create'&&removed.size!==0))
      fail('Incomplete historical linked record transaction.');
    if(kind==='update'||kind==='delete') {
      if(removed.size!==oldCount)fail('Incomplete historical previous-generation removal.');
      for(let i=0;i<oldCount;i++)
        if(!removed.has(recordKey(recordId,i,oldGen)))
          fail('Historical previous ciphertext removal key was missing.');
    }
    if(kind==='delete'){
      const complete=chunks.size===0;
      for(const part of chunks.values())part.fill(0);
      if(complete)s.verifiedHistories.add(txHash);
      return complete; // Signed transaction removed exact original skh/skc keys.
    }
    const manifest=parseManifest(headerRaw);
    if(chunks.size!==manifest.count||
       (kind==='update'&&manifest.gen===oldGen))
      fail('Incomplete historical ciphertext manifest or reused generation.');
    const cipher=new Uint8Array(manifest.size);
    try {
      for(let i=0;i<manifest.count;i++){
        const name=recordKey(recordId,i,manifest.gen),part=chunks.get(name);
        if(!part||part.length!==Math.min(64,manifest.size-i*64))
          fail('Historical ciphertext chunk was missing or malformed.');
        cipher.set(part,i*64);
      }
      if(!equal(await hash(cipher),manifest.digest))
        fail('Historical ciphertext digest does not match the signed manifest.');
      const plain=await unseal(s,recordId,manifest.gen,cipher);
      const normalized={
        Id:recordId,Title:draft.Title,Content:draft.Content,
        FolderId:draft.FolderId||null,Tags:draft.Tags||[],IsFolder:!!draft.IsFolder,
        UserName:draft.UserName||'',Url:draft.Url||'',Notes:draft.Notes||''
      };
      const matched=JSON.stringify(plain)===JSON.stringify(normalized);
      if(matched)s.verifiedHistories.add(txHash);
      return matched;
    } finally {
      cipher.fill(0);
      for(const part of chunks.values())part.fill(0);
      if(headerRaw)headerRaw.fill(0);
    }
  }

  // Independently inspect a linked-account expansion: root AND newly created
  // child must sign the SAME successful envelope, which must create exactly
  // the child, disable its master key, install root signer and point from the
  // immediately preceding linked tail. Never submits a network operation.
  async function verifyHistoricalExpansion(id,txHash,childId) {
    const s=session(id);
    if(!/^[0-9a-f]{64}$/.test(txHash) ||
       !s.sdk.StrKey.isValidEd25519PublicKey(childId)||childId===s.root)
      fail('Invalid linked expansion history scope.');
    const verified=await verifiedNetwork(s.net.origin,s.passphrase);
    if(verified.origin!==s.net.origin)fail('Selected network origin changed.');
    const endpoint=s.net.origin+'/transactions/'+txHash;
    const response=await fetch(endpoint,{cache:'no-store',headers:{Accept:'application/json'}});
    if(!response.ok||new URL(response.url||endpoint).origin!==s.net.origin)
      fail('Historical linked expansion receipt is not trustworthy.');
    const receipt=await response.json();
    if(receipt?.successful!==true||receipt?.hash?.toLowerCase()!==txHash||
       receipt?.source_account!==s.root||
       typeof receipt.envelope_xdr!=='string'||receipt.envelope_xdr.length>65536)
      fail('Historical expansion receipt is not a successful root transaction.');
    const tx=s.sdk.TransactionBuilder.fromXDR(receipt.envelope_xdr,s.passphrase);
    const rawHash=tx.hash();
    if(rawHash?.length!==32||tx.source!==s.root||
       Array.from(rawHash,x=>x.toString(16).padStart(2,'0')).join('')!==txHash||
       !Array.isArray(tx.operations)||![3,4].includes(tx.operations.length))
      fail('Historical expansion envelope hash or operation count mismatches.');
    const hasSignature=key=>tx.signatures?.some(signature=>{
      try{return s.sdk.Keypair.fromPublicKey(key)
        .verify(rawHash,new Uint8Array(signature.signature()));}
      catch{return false;}
    });
    if(!hasSignature(s.root)||!hasSignature(childId))
      fail('Historical linked expansion requires root and child signatures.');
    const nodes=await chain(s);
    const childIndex=nodes.findIndex(account=>account.account_id===childId);
    if(childIndex<1||childIndex!==nodes.length-1)
      fail('Verified linked child must be the current chain tail.');
    const previousTail=nodes[childIndex-1].account_id;
    const ops=tx.operations,creation=ops[0],options=ops[1],pointer=ops.at(-1);
    if(creation.type!=='createAccount'||(creation.source||tx.source)!==s.root||
       creation.destination!==childId||!(Number(creation.startingBalance)>0))
      fail('Historical expansion did not create the expected funded child.');
    if(options.type!=='setOptions'||(options.source||tx.source)!==childId||
       options.masterWeight!==0||options.lowThreshold!==1||
       options.medThreshold!==1||options.highThreshold!==1||
       options.signer?.ed25519PublicKey!==s.root||options.signer?.weight!==1)
      fail('Historical child signer and thresholds do not match the linked protocol.');
    if(ops.length===4){
      const payment=ops[2];
      if(previousTail===s.root||payment.type!=='payment'||
         (payment.source||tx.source)!==s.root||
         payment.destination!==previousTail||
         payment.asset?.getCode?.()!=='XLM'||!(Number(payment.amount)>0))
        fail('Historical tail reserve payment does not match the parent.');
    }else if(previousTail!==s.root) {
      fail('An existing child tail requires an atomic reserve payment.');
    }
    if(pointer.type!=='manageData'||(pointer.source||tx.source)!==previousTail||
       pointer.name!=='sv:next:v1'||pointer.value==null||
       decoder.decode(new Uint8Array(pointer.value))!==childId)
      fail('Historical linked parent pointer does not target the child.');
    s.verifiedHistories.add(txHash);
    return true;
  }

  // Called only AFTER durable encrypted CAS committed a full historical proof.
  // Never clear an unrelated uncertain transaction or a different vault.
  function releaseVerifiedHistory(id,transactionHash) {
    const s=session(id),scope=vaultScope(s.root,s.passphrase);
    if(!/^[0-9a-f]{64}$/.test(transactionHash) ||
       !s.verifiedHistories.has(transactionHash))
      fail('The current signer has not verified this signed historical transaction.');
    const marker=unresolvedByVault.get(scope)||s.uncertain;
    if(marker && marker.TransactionHash!==transactionHash)
      fail('A different uncertain transaction remains in this signer session.');
    if(marker){
      unresolvedByVault.delete(scope);
      s.uncertain=null;
      s.needsReconciliation=false;
    }
    s.verifiedHistories.delete(transactionHash);
    return true;
  }
  function inspectUncertain(id) {
    const s=session(id);
    // Return a defensive public-only copy. Presence does NOT prove that the
    // transaction reached the network, nor that its contents were confirmed.
    // A second already-open session must see a sibling's unresolved attempt,
    // not merely its own initially clean per-session snapshot.
    const finding=unresolvedByVault.get(vaultScope(s.root,s.passphrase))||s.uncertain;
    return finding ? {...finding} : null;
  }
  function cancelQuote(id,quoteId) {
    const s=session(id),q=s.quotes.get(quoteId);
    if(!q || s.inFlight) return;
    if(q.cipher)q.cipher.fill(0);
    if(q.child){try{q.child.rawSecretKey().fill(0);}catch{}}
    s.quotes.delete(quoteId);
  }
  // Drop all in-memory keypairs and pending child keys when the host is
  // backgrounded or leaves the page. A new S-key unlock is required after.
  function revokeAll() {
    for(const id of Array.from(sessions.keys())) close(id);
  }
  if(typeof document!=='undefined' && typeof document.addEventListener==='function')
    document.addEventListener('visibilitychange',()=>{
      if(document.visibilityState==='hidden') revokeAll();
    });
  if(typeof window.addEventListener==='function')
    window.addEventListener('pagehide',revokeAll);
  return Object.freeze({open,close,read,info,plan,submit,recordRevision,verifyHistoricalRecord,verifyHistoricalExpansion,releaseVerifiedHistory,inspectUncertain,cancelQuote,revokeAll});
})();
