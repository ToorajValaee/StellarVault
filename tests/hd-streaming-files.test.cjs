'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const core = path.join(root, 'src', 'StellarVault.Core', 'Services');
const pwa = path.join(root, 'src', 'StellarVault.Pwa', 'Pages');

const read = file => fs.readFileSync(file, 'utf8');

test('HD SVTREE3 file upload shares encrypted queue and existing verified publication engine', () => {
  const workspace = read(path.join(core, 'VaultHybridDraftWorkspace.cs'));
  const queue = read(path.join(core, 'VaultPendingMutationQueue.cs'));
  const create = read(path.join(core, 'VaultQueuedCreatePolicy.cs'));
  const tree = read(path.join(core, 'VaultHdTreeFilePolicy.cs'));
  const editor = read(path.join(pwa, 'VaultItemEditor.razor'));
  const smoke = read(path.join(root, 'tests', 'StellarVault.Core.CryptoTests',
    'HdStreamingFileTreeSmoke.cs'));
  const runner = read(path.join(root, 'tests', 'StellarVault.Core.CryptoTests',
    'Program.cs'));
  assert.match(workspace, /public async Task<VaultStreamingFileTree\.Result> StageTreeFileAsync/);
  assert.match(workspace, /VaultStreamingFileTree\.WriteContentAddressedAsync\(/);
  assert.match(workspace, /queue\.EnqueueAsync\(pending, token\)/);
  assert.match(workspace, /queue\.DiscardPendingAsync\(/);
  assert.match(tree, /hd-tree-root-not-staged/);
  assert.match(tree, /hd-tree-child-not-confirmed/);
  assert.match(tree, /hd-tree-child-remote-mismatch/);
  assert.match(create, /VaultHdTreeFilePolicy\.Problems\(item, all, draft\)/);
  assert.match(queue, /VaultHdTreeFilePolicy\.RemoteProblems\(/);
  assert.match(queue, /VaultLinkedTreeFilePolicy\.BlocksDiscard\(current, entries\)/);
  assert.match(editor, /HybridDrafts\.StageTreeFileAsync\(/);
  assert.match(editor, /selected\.OpenReadStream\(selected\.Size\)/);
  assert.doesNotMatch(editor, /_fileBytes|MaximumLedgerFileLength|StellarLedgerUploadPlan/);
  assert.match(smoke, /HD file-tree dependency review rejected a historically confirmed prefix/);
  assert.match(runner, /HdStreamingFileTreeSmoke\.RunAsync\(\)/);
});

test('HD file root is a single visible file with verified streamed export', () => {
  const vault = read(path.join(pwa, 'Vault.razor'));
  const corePolicy = read(path.join(core, 'VaultHybridRemoteMutationWorkspace.cs'));
  assert.match(vault, /VaultLinkedTreeFilePolicy\.IsRootRecord\(item\)/);
  assert.match(vault, /VaultLinkedTreeFilePolicy\.ReadAsync\(\s*current\.Id, all, Stream\.Null\)/);
  assert.match(vault, /NativeFiles\.SaveAsync\(prepared\.FileName, reader\)/);
  assert.match(vault, /IsHdTreePart\(x\)/);
  assert.match(vault, /!IsHdTreeMutation\(_selectedPending\)/);
  assert.match(corePolicy, /VaultLinkedTreeFilePolicy\.Prefix/);
});
