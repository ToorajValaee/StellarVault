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

test('HD draft and remote mutation reviews zero decrypted snapshots on all exits', () => {
  const draft = read(path.join(core, 'VaultHybridDraftWorkspace.cs'));
  const remote = read(path.join(core, 'VaultHybridRemoteMutationWorkspace.cs'));
  function method(source, name, nextName) {
    const start = source.indexOf(name);
    const end = source.indexOf(nextName, start + name.length);
    assert.ok(start >= 0 && end > start, 'method section not found: ' + name);
    return source.slice(start, end);
  }
  const create = method(draft, 'public async Task<VaultPendingMutation> CreateAsync(', 'StageTreeFileAsync(');
  const revise = method(draft, 'public async Task<VaultPendingMutation> ReviseAsync(', 'public async Task DiscardAsync(');
  const list = method(draft, 'public async Task<IReadOnlyList<DraftItem>> ListAsync(', 'private sealed record DraftScope(');
  for (const body of [create, revise, list]) {
    assert.match(body, /var entries = await queue\.ReviewAllAsync\(/);
    assert.match(body, /finally \{ ClearReviewPayloads\(entries\); \}/);
  }
  for (const [start, end] of [
    ['private async Task<VaultPendingMutation> StageAsync(', 'public async Task<VaultPendingMutation> ReviseUpdateAsync('],
    ['public async Task<VaultPendingMutation> ReviseUpdateAsync(', 'public async Task<VaultPendingMutation> PreserveUpdateAsNewRecordAsync('],
    ['public async Task<VaultPendingMutation> PreserveUpdateAsNewRecordAsync(', 'public async Task DiscardAsync('],
    ['public async Task DiscardAsync(', 'public async Task<IReadOnlyList<StagedItem>> ListAsync('],
    ['public async Task<IReadOnlyList<StagedItem>> ListAsync(', 'InspectRemoteVersionsAsync(']
  ]) {
    const body = method(remote, start, end);
    assert.match(body, /finally \{ ClearReviewPayloads\((previous|entries)\); \}/);
  }
  assert.match(remote, /finally \{ ClearReviewPayloads\(latest\); \}/);
  for (const source of [draft, remote]) {
    assert.match(source, /private static void ClearReviewPayloads\(IEnumerable<VaultPendingMutation> entries\)/);
    assert.match(source, /CryptographicOperations\.ZeroMemory\(entry\.EncryptedPayload\)/);
  }
  assert.match(remote, /await queue\.EnqueueAsync\(mutation, ct\)/,
    'Queue stage still uses the existing durable CAS store');
});
