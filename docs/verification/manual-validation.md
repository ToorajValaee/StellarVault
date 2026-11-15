# Manual validation — StellarVault

GitHub Actions is **opt-in only**. The workflow `.github/workflows/manual-verify.yml` is registered on `main` so it appears in Actions, and is also available on `architecture/pluggable-vault-records-and-chains`. Its only trigger is `workflow_dispatch`. There are **no push or pull-request CI triggers**.

## Preferred local release-candidate validation

Run the repository-owned PowerShell verification runner on the **exact commit** under consideration:

```powershell
pwsh -File scripts/verify-release.ps1
# Optional when MAUI Android prerequisites are installed:
pwsh -File scripts/verify-release.ps1 -Android
```

This exercises the complete Node regression suite, signer syntax, Core/UI/PWA Release builds, and the Core native cryptography/linked queue smoke checks. It does **not** send Testnet transactions, prove actual Stellar XDR validation, or replace real-device testing or an independent security audit. The production decision is defined in [production-release-gate.md](production-release-gate.md). **Production remains blocked** until that evidence is completed. The last reported green tests were on an earlier commit; rerun after any source changes.

## Run on demand

1. Open **GitHub → Actions → Manual StellarVault verification**.
2. Choose **Run workflow**.
3. Select branch **`architecture/pluggable-vault-records-and-chains`**.
4. Start the run. It will check the S-key JS syntax, execute the S-key Node regression suite, build .NET 10 Core, shared UI and PWA, and run the existing Core cryptography/linked-queue console regression.
5. If a step fails, inspect the job output or the `manual-verification-failure-logs` artifact (retained for three days). Fix the **reported failing code** instead of adding another wrapper. Do not dispatch it repeatedly without a change.

This workflow does **not** deploy, sign transactions, make Testnet writes, run MAUI device workloads, or prove historical signer authorization. It does not use GitHub Actions minutes until explicitly run. It is safe to remove again if unwanted.

## Equivalent local commands

```bash
node --check src/StellarVault.Pwa/wwwroot/js/vault-secret-ledger.js
node --test tests/secret-key-ledger.test.cjs
dotnet build src/StellarVault.Core/StellarVault.Core.csproj -c Release
dotnet build src/StellarVault.UI/StellarVault.UI.csproj -c Release
dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Release
dotnet run --project tests/StellarVault.Core.CryptoTests/StellarVault.Core.CryptoTests.csproj -c Release
```

## Separate release gates

- Build/test the MAUI host using supported installed workloads on Windows or another supported target machine.
- Test an HD existing vault and an imported S-key vault **separately**, verifying that their signers, device-consent gates and encrypted stores never cross.
- On Stellar Testnet, independently observe a quoted record/folder transaction and its exact operations and signer policy. A matching *current* record or an observed receipt **alone** is not independent historical proof.
- Exercise offline staging, network switching, stale tabs, multi-tab CAS, pending/uncertain expansion, foreground/background locking, and restart recovery. Never replay an uncertain transaction automatically.
- Validate chunked HD file flows and distinguish which existing file services are wired for independent S-key authorization.
- Review plaintext lifetime and retention/deletion semantics; execute a security review before enabling Mainnet.

**Current evidence:** Source changes and syntax inspection only. This document does not certify an executable build or release readiness.
