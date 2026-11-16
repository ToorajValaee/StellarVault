# HTTP API preview

The authenticated self-hosted .NET 10 API supports operator-configured Stellar customer networks, including private networks with trusted HTTPS Horizon roots. Testnet and Public are defaults, not hardcoded restrictions. No network access is needed to start the host.

```powershell
dotnet run --project src/StellarVault.Api -- --urls http://127.0.0.1:5080
```

Configure profiles in `src/StellarVault.Api/appsettings.json`, an environment-specific settings file, or environment variables. Add this object to the `Networks` array:

```json
{
  "Id": "customer-one",
  "HorizonUrl": "https://horizon.customer.example",
  "ExpectedPassphrase": "Exact customer network passphrase"
}
```

Equivalent environment keys for a third profile are `Networks__2__Id`, `Networks__2__HorizonUrl`, and `Networks__2__ExpectedPassphrase`. Profile IDs are lowercase letters, digits and hyphens (1–64 characters); 1–128 unique profiles are accepted. HTTPS roots cannot contain credentials, paths, query or fragment. An invalid configuration prevents startup. Restart to apply changes. Private certificates must be trusted by the host; certificate validation is never bypassed. Customer network operators can use private DNS/IP endpoints; restrict server egress to those reviewed endpoints. Requests cannot supply arbitrary URLs or passphrases.

| Route | Result |
|---|---|
| `GET /health/live` | Process health only (anonymous) |
| `GET /health/ready` | Access configuration readiness; 503 if no tenant is configured (anonymous) |
| `GET /v1/capabilities` | Implemented and disabled capabilities, configured network IDs |
| `GET /v1/networks` | Authorized tenant network IDs, without endpoints/passphrases |
| `GET /v1/networks/{network}/transactions/{hash}` | Network-bound successful receipt metadata; hash is 64 hex digits |

For example, `/v1/networks/customer-one/transactions/{hash}` checks the configured endpoint's reported network passphrase against the operator's exact expectation before looking up the receipt. The response includes SHA-256 network ID. This remains endpoint-attested identity, not independent consensus or proof of signer/record contents. It always reports `recordLinkedToTransaction: false` and `mutationConfirmed: false`.

Malformed hashes return 400, unknown networks or no usable successful receipts return 404, upstream/network-identity failures return 502, and receipt timeouts return 504. Root inspection failures, including timeouts caught by the existing inspector, return 502. Cancellation propagates. None authorizes retry or submission. No automatic retry is configured.

## API access configuration

Every `/v1` request requires `Authorization: Bearer <api-key>`. Keys are 32 random bytes encoded as 64 lowercase hex characters. Only SHA-256 of the **UTF-8 key string** belongs in configuration. There are no built-in/default keys, query-string keys, or anonymous discovery. With the default empty tenant configuration, the host stays live but readiness returns 503 and all API calls are unauthorized.

Generate a key locally (this command intentionally displays the secret once; protect the output):

```powershell
powershell -File scripts/new-api-credential.ps1 -TenantId customer-one -Networks customer-one
```

Store `ApiKey` in the customer's secret manager. Put only the returned `TenantConfiguration` under `ApiAccess:Tenants`:

```json
{
  "ApiAccess": {
    "Tenants": [
      {
        "Id": "customer-one",
        "KeySha256": ["<64-character SHA-256 digest from the generator>"],
        "Networks": ["customer-one"]
      }
    ]
  }
}
```

The digest placeholder must be replaced: malformed profiles fail startup. All granted network IDs must already exist in `Networks`. Environment equivalents use `ApiAccess__Tenants__0__Id`, `ApiAccess__Tenants__0__KeySha256__0`, and `ApiAccess__Tenants__0__Networks__0`. Up to 256 tenants and four unique key digests per tenant are accepted. Key hashes cannot be shared across tenants. Configuration is copied at startup. To rotate, add a new digest, distribute its key securely, restart, then remove the old digest and restart; removal revokes the key. No tenant-identification header supplied by the caller is trusted.

For authenticated calls, supply the key through a client secret manager or an in-memory request header. Never place keys in URL/query parameters or appsettings. HTTPS and trusted proxy/backend restrictions are required in deployment. Liveness/readiness disclose no tenant/network details. Readiness checks configuration only, not network availability or production approval.
The API and StellarVault reference the same independent `VaultEngine.Core.dll`. HTTP transaction inspection uses `ITransactionObservation` and the engine provider router; a Horizon adapter implements the engine port. The neutral object engine and Stellar provider are physically separated as `VaultEngine.Core` and `VaultEngine.Stellar`. This does not enable hosted record/file write endpoints.

## Architecture and hosting

- `VaultEngine.Core`: independent neutral engine shared by consumer applications and the API. Owns object/provider contracts and observation routing. Has no consumer or chain-specific dependency.
- `VaultEngine.Stellar`: Stellar object provider, crypto, encrypted queues, sessions and signing ports. Never references a StellarVault consumer.
- `VaultEngine.Stellar.Horizon`: optional Stellar HTTP integration. Owns network profiles/catalog, identity checks, bounded Horizon inspectors, receipt workflow and the engine observation adapter. References only the neutral engine; no wallet crypto, UI, ASP.NET, credentials or tenant policy. PWA, MAUI and API use the same library where they need Horizon inspection.
- `StellarVault.Api`: routes, authentication, tenant grants, rate limiting, HTTP composition and hosting. It invokes the engine rather than a separate application-owned storage engine.

The former `StellarVault.Application` and `StellarVault.Infrastructure` projects were removed. Their receipt/Horizon functionality now belongs to the Stellar integration library. A future EVM provider can supply its own RPC adapter; it does not need copies of these projects. No EVM implementation is supplied here.
The authenticated read-only HTTP API accepts no signing keys, mnemonic, plaintext, custom endpoint, transaction submission or durable queue. POST/PUT/DELETE routes are absent. Network aliases are guarded by per-tenant grants. Every `/v1` endpoint requires authentication; unauthorized network lookups return the same 404 as unavailable networks before contacting Horizon. API access does not establish Stellar account ownership or grant signing authority. Tenant storage quotas, billing, distributed rate budgets and hosted private object/journal persistence remain unimplemented.

Global rate limit: 60 requests/minute/process, with no queue. Outbound requests use a 10-second timeout, no redirects/cookies, 8 connections/server, a 64 KiB root response cap and a 256 KiB receipt cap. All responses disable caching. Terminate HTTPS at a trusted reverse proxy, restrict direct backend access and configure `AllowedHosts` for the service hostname (defaults to loopback). Add infrastructure-level distributed abuse/concurrency controls. No permissive CORS or forwarded-header trust is enabled.

## Verification and remaining work

Run `dotnet build src/StellarVault.Api -c Release`, `dotnet run --project tests/StellarVault.Api.Tests -c Release`, and on Windows `powershell -File scripts/verify-api-http.ps1` (loopback only; no Horizon transactions). Focused tests execute the real catalog and Horizon adapters with mocked HTTP: custom network success, wrong identity rejection before receipt fetch, invalid profiles, unknown network, malformed hash, response bounds, redirects and cancellation. They do not contact customer networks. Tenant tests cover credential rejection, network isolation, configuration snapshots and invalid profiles; the HTTP smoke also checks anonymous rejection, readiness and cross-tenant network denial. After consolidating the Horizon integration, API/PWA/Android Release builds passed with zero warnings/errors, and Core/API/HTTP tests plus all 231 source regressions passed for this worktree; device/network validation remains separate.

Storage quotes/preparation, client-signed submission, durable idempotency, hosted tenant-private storage, full file deletion and release/device/security gates remain unfinished. See [API architecture](architecture/noncustodial-saas-api.md), [engine boundary](architecture/account-object-engine.md) and [production gate](verification/production-release-gate.md). A successful API build is not production approval.
