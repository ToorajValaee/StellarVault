# PWA and read-only API deployment

The deployment contract is `app.svault.ir` for the PWA and `api.svault.ir` for
authenticated receipt diagnostics. The separate `svault.ir` landing host is not
changed by `deploy/compose.yml`. The API does not provide hosted record storage,
signing, transaction submission, or mutation confirmation.

## Release prerequisites

Production approval remains governed by
[the production gate](verification/production-release-gate.md). Local compilation
and a successful HTTPS health check cannot replace live vault recovery,
interruption/concurrency checks, or independent security review. Never remove
the existing Mainnet/unsupported-feature gates to make a deployment pass.

Use a clean, reviewed commit. With Node/npm and a .NET 10 SDK available:

```powershell
powershell -NoProfile -File scripts/verify-release.ps1
# Optional installed-Edge browser check against the published site:
npm ci --ignore-scripts --registry=https://registry.npmjs.org
npm run verify:browser -- https://app.svault.ir
```

The release runner audits the locked JavaScript dependencies and NuGet packages,
verifies the vendored SDK provenance, runs Node and .NET tests, builds the hosts,
publishes the PWA, and verifies published cache asset hashes and the production
service-worker alias. Network/advisory failures are failures, not a waiver.
Record the commit, clean/dirty state, SDK versions, base-image digests, test logs,
image IDs and deployment configuration in the release evidence. Logs must contain
no credentials or vault contents.

## Build and configure

Build the root `Dockerfile`, `deploy/Dockerfile.api`, and `deploy/Dockerfile.edge`
with `RELEASE_REVISION` set to the tested commit. Pin the SDK/runtime/Nginx/Caddy
base images by digest through their build arguments (the current defaults are pinned). Refresh and review those pins when taking security updates. Set `PREVIEW=false` only
after actual production approval; that label does not grant approval. The source
digest label must represent the source actually built, not an earlier commit.

Caddy's base is configurable with `CADDY_IMAGE`. On the review PC Docker Hub and
`mirror.gcr.io` returned 403; the official-image mirror
`public.ecr.aws/docker/library/caddy:2-alpine` worked. Record and pin the resolved
digest if using that source. Do not disable registry or TLS verification.

Provide an operator-owned settings file outside the checkout, readable only by
the operator/container user. Use `deploy/api-settings.example.json` as a schema
starting point. Generate tenant credentials with `scripts/new-api-credential.ps1`;
only digests go into server configuration. Transfer the actual keys through a
secret manager. Give each tenant only the networks it needs. Use exact expected
network passphrases supplied by the operator, not values blindly discovered
from an arbitrary endpoint. See [API configuration](API.md).

Create `deploy/.env` (ignored by Git) with immutable `PWA_IMAGE`, `API_IMAGE`,
`EDGE_IMAGE`, absolute `API_SETTINGS_FILE`, and `ACME_EMAIL`. The images must be
available to the VPS. Do not use moving `latest` tags for a release.

Only the edge publishes ports 80/443. Leave API/PWA ports private. The API trusts
only the configured edge IP on the dedicated Compose subnet; do not enable the
automatic forwarded-header environment switches or wildcard `AllowedHosts`.
Confirm the subnet does not conflict with the VPS networks. Configure DNS,
firewall, TLS issuance and outbound access to the approved Horizon endpoints.
The landing host may already own ports 80/443: resolve that routing arrangement
before starting another edge proxy.

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml config --quiet
docker compose --env-file deploy/.env -f deploy/compose.yml up -d --wait
```

## Acceptance and rollback

Verify anonymous readiness, anonymous `/v1` rejection (401), tenant-isolated
network discovery, HTTPS and security headers, request limits, and a successful
read-only receipt inspection. Readiness checks configuration, not Horizon
availability or security approval. Test direct `/vault` navigation, installation,
offline reload, browser-console errors and release upgrades on supported devices.
Use disposable accounts/synthetic data for any write tests.

Keep the previous immutable image references and operator configuration before
rollout. To roll back, restore those references and run the same Compose command.
Do not delete encrypted queues, browser data or Caddy volumes as a rollback step.
Clients with an open vault intentionally keep their verified release until tabs
close; coordinate lock/close/reopen. For an urgent client vulnerability, suspend
new writes and communicate the required restart; replacing the server alone does
not revoke cached code. Never retry or clear an uncertain submission to make the
rollout appear healthy.

Monitor container health, 5xx/429 rates and resource use without request secrets
or record bodies. Review host/OS and image advisories, test restore of operator
configuration and TLS state, and document incident ownership before launch.

## Local HTTPS smoke

`deploy/Caddyfile.smoke` uses a private local CA and must never replace the public
ACME configuration on the VPS. Bind the smoke stack to `127.0.0.1` using distinct
HTTP/HTTPS ports and a distinct Compose project. Trust only its exported test CA
for those test requests; never use `curl -k` or disable certificate validation.
On Windows Schannel, an internal CA without revocation services may require
`--ssl-revoke-best-effort`; certificate chain/hostname verification must remain
enabled. This smoke proves local proxy wiring, not public DNS/ACME/VPS readiness.
