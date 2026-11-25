# PWA/API launch preparation — 2026-11-24

**Decision: production NO-GO; launch preparation has progressed, not completed.**
Scope is the browser PWA and authenticated, read-only receipt API. Native-device
approval and a hosted storage/write API are not part of this batch. Work is on
`architecture/pluggable-vault-records-and-chains`, based on `53e22404`; validation
logs identify the actual worktree/commit and explicitly flag uncommitted files.
No production merge, push or VPS deployment was performed. Subsequent authorized synthetic network writes and the latest evidence are recorded in [the November 25 live results](2026-11-25-live-testnet-results.md).

## Resolved findings

- Mnemonic validation no longer includes an entered word in an exception.
- API inspection has a 10-second workflow deadline, including streamed root and
  receipt bodies. HttpClient's response-header timeout alone was insufficient.
- Repository NuGet sources are explicit; an inherited unavailable localhost feed
  previously prevented a clean PWA restore. Container builds use the same config.
- Removed an invalid IPv6 CSP source rejected by Chromium. Production CSP still
  permits HTTPS custom endpoints and supported local development origins.
- Quoted Compose tmpfs options: the previous flow-style YAML split the comma
  into another mount, preventing container startup.
- Replaced vendored Stellar SDK 16.0.1 with 16.4.1. The original version's Axios
  dependency produced security advisories; the new locked dependency graph
  reports zero known advisories. Version, source, normalized SHA-256 and license
  are recorded next to the bundle. Upstream [16.4.1 release notes](https://github.com/stellar/js-stellar-sdk/releases/tag/v16.4.1)
  describe its Axios security update. An advisory scan is not an independent audit.
- Fixed independent S-key ManageData binary-value conversion to use the real
  browser SDK Buffer type. The old mock accepted plain Uint8Array values that the
  real SDK rejects. Mocks now enforce that boundary; real-SDK tests cover it too.
- Release verification now audits JS/NuGet dependencies, verifies vendor
  provenance, publishes the PWA and checks cached asset hashes/worker aliases.
  API smoke additionally checks read-only methods, 429/Retry-After and readiness
  under throttling. Dirty-worktree results no longer imply a clean commit test.

## Evidence

- Windows .NET SDK 10.0.404 installed privately for this task; now under ignored
  `artifacts/tooling/dotnet10` on D:. Node regressions: **240 passed, zero failed**.
- Release engine/provider/Horizon/API/UI/PWA compilation and the engine,
  cryptography/journal, tenant/network and authenticated HTTP harnesses passed.
  The latest full runner output is `artifacts/release-review/verification.log`.
- Actual bundled SDK tests pass the RFC 8032 empty-message signature vector,
  signed XDR roundtrip/wrong-network rejection, and an independent S-key
  encrypted-create/lost-response test with signed-hash checkpoint before the
  mocked network attempt and no subsequent retry.
- Published PWA hash/worker/shell validation passed. Installed desktop Edge
  (1440px) and mobile-size Edge (390px) passed controlled-worker startup,
  offline reload, default persistence opt-in, masked S-key, strategy switching
  and no horizontal overflow or console errors. Screenshots/logs remain in
  ignored review artifacts. This is not Android Chrome/iOS or device acceptance.
- Linux PWA/API/edge images built and the restricted Compose stack started on
  loopback. PWA/API health checks passed. Local-CA HTTPS PWA navigation and API
  authentication worked with chain/hostname verification enabled. Those first
  image builds preceded the final SDK replacement, so their successful wiring
  check is not final-candidate container/artifact attestation.
- The local HTTPS API inspected successful custom-network transaction
  `e6438f2213d13b82637aa1267c6bbfab076eeda1e05fd09e1d3a67573c8ce967`, ledger 3033296.
  Its configured expected network ID was
  `d63e43fd63c63ca4cc5cd66df6c28647a6d1ae4582d4e30e8bb4e8f6bf4a98d2`.
  The response correctly retained `mutationConfirmed=false` and
  `recordLinkedToTransaction=false`. This was a read-only request, not vault proof.

## Remaining launch gates

### Live follow-up and additional blockers

The disposable account was subsequently funded with 20 native. A separately
approved two-operation setup succeeded in ledger 3033652, transaction
`a0fdefcc2b747b24a2f7b413d6685b69e7c7edddc406153a1c560cd09e97a131`:
positive S-key root thresholds and a separate HD root funded with 10 native.

Real browser interaction then exposed and corrected additional defects:

- Shell-invoked S-key create/folder/refresh operations must notify the child
  renderer; changing its fields from the parent does not guarantee a render.
- The shared record title input was missing an attribute-closing quote, so its
  actual DOM ID did not match the label. Razor compilation had not rejected it.
- Blazor camel-cases CLR properties sent to JavaScript. S-key quote/history
  functions expect Pascal-cased protocol fields. The host now supplies explicit
  dictionary keys without changing durable queue JSON. A new executable host
  interop harness tests the actual adapter with camel-case serialization.
- S-key HKDF cleanup used `Buffer.slice()`, which aliases the SDK's retained
  private seed. Wiping the temporary buffer corrupted the signer. It now copies
  with `new Uint8Array(...)`; the writer independently checks local root/child
  signatures before checkpoint/broadcast. Real-SDK tests now assert outside
  the writer's caught transport boundary so failed assertions cannot be masked
  as the deliberately simulated lost response. A corrupt-signer fixture proves
  zero transport calls and zero signed-hash checkpoints. **241 Node tests pass.**

One actual S-key create attempt before the seed-copy fix entered the conservative
uncertain UI without a returned hash. The test driver's local file-lock failure
then lost diagnostic control of that browser session. **That account remains
quarantined; no retry, guessed hash, queue confirmation, or release was attempted.**
An absent current record/receipt and a later source diagnosis are not original
transaction attribution. The account is
`GDPUT7FS2DB352I4JZIQ6H7QEKR7RS5GASRAQD6GUPXRAXHK4ZKRIEW6`.
Independent fresh-account tests are required; this is not a successful create.

Trivy 0.76.2 with its downloaded advisory database found no high/critical issues
in the API image. The original PWA image had seven high/critical OS findings;
refreshing Nginx to digest
`637c35e46af7bc0dd3d3bcf7162e004e13ab3b370fde7cd416e2ac410b5a985a`
(1.30.8-alpine) cleared the high/critical base-image findings and is now pinned.
**Caddy remains blocked** by dependency findings CVE-2026-116587 (go-jose/v3)
and CVE-2026-78661 (x/net). No reachability waiver or custom crypto fork is
claimed. The scanner output and SDK integrity/download checks are local evidence,
not independent security approval.

### Outstanding acceptance

1. Complete fresh disposable custom-Testnet tests for **both** HD and
   independent S-key create/read/update/delete, signed-history reconciliation,
   file integrity, clean-session recovery, interruptions and concurrent-tab
   checks. The initial funding blocker was resolved; its create attempt is now
   quarantined as described above. Use a separately authorized fresh account.
   Never log credentials or use production secrets for this work.
2. Rebuild and attest final immutable Linux images after the source batch is
   committed. Repeat browser/HTTPS smoke on those exact artifacts. Base image
   references must be pinned for the release, not moving tags.
3. Complete applicable supported-browser storage/picker/upgrade and real-device
   recovery acceptance, independent cryptographic/protocol/app security review,
   and image/OS dependency review with findings resolved.
4. Confirm VPS routing/DNS/public ACME, resource budgets, operator-held API
   credentials, rollback, monitoring and incident ownership. The local TLS smoke
   does not establish any of those external deployment facts.

See [production-release-gate.md](production-release-gate.md) for the approval
rule and [DEPLOYMENT.md](../DEPLOYMENT.md) for the manual deployment/rollback
procedure. Main's four manual-only CI commits must be preserved in eventual
integration. Mainnet restrictions and unsupported file-deletion gates remain.
