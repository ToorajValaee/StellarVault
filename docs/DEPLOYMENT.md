# Landing, PWA and B2B receipt API deployment

The deployment contract is `svault.ir` for the landing page, `app.svault.ir` for
the PWA and `api.svault.ir` for authenticated B2B receipt diagnostics. The user's
November 25 scope explicitly includes all three hosts in `deploy/compose.yml`.
The API does not provide hosted record storage,
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

Build the root `Dockerfile`, `deploy/Dockerfile.api`, `deploy/Dockerfile.edge`
and `src/StellarVault.Landing/Dockerfile` (with the landing directory as context)
with `RELEASE_REVISION` set to the tested commit. Pin the SDK/runtime/Nginx/Caddy
base images by digest through their build arguments (the current defaults are pinned). Refresh and review those pins when taking security updates. Set `PREVIEW=false` only
after actual production approval; that label does not grant approval. The source
digest label must represent the source actually built, not an earlier commit.

The production edge is stock unprivileged Nginx, pinned through
`EDGE_SERVER_IMAGE`. The previous Caddy image remains unsuitable because of
unresolved dependency findings; the old Caddyfiles are historical local-smoke
examples, not the production Compose configuration. Nginx uses externally
managed certificates and never receives an ACME account key.

Provide an operator-owned settings file outside the checkout, readable only by
the operator/container user. Use `deploy/api-settings.example.json` as a schema
starting point. Generate tenant credentials with `scripts/new-api-credential.ps1`;
only digests go into server configuration. Transfer the actual keys through a
secret manager. Give each tenant only the networks it needs. Use exact expected
network passphrases supplied by the operator, not values blindly discovered
from an arbitrary endpoint. See [API configuration](API.md).

Create `deploy/.env` (ignored by Git) with immutable `LANDING_IMAGE`, `PWA_IMAGE`, `API_IMAGE`,
`EDGE_IMAGE`, absolute `API_SETTINGS_FILE`, `TLS_CERTIFICATE_DIR` and
`ACME_WEBROOT`. The images must be
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
Do not delete encrypted queues, browser data or certificate history as a rollback step.
Clients with an open vault intentionally keep their verified release until tabs
close; coordinate lock/close/reopen. For an urgent client vulnerability, suspend
new writes and communicate the required restart; replacing the server alone does
not revoke cached code. Never retry or clear an uncertain submission to make the
rollout appear healthy.

Monitor container health, 5xx/429 rates and resource use without request secrets
or record bodies. Review host/OS and image advisories, test restore of operator
configuration and TLS state, and document incident ownership before launch.

## Certificates and renewal

Use one certificate containing `svault.ir`, `app.svault.ir` and `api.svault.ir`. Keep
the ACME account and original keys in the operator's root-only certificate
store. The edge reads only the installed certificate version:
`TLS_CERTIFICATE_DIR/current/fullchain.pem` and `current/privkey.pem`.
The mount parent and version directory must be readable/traversable by container
group 101; use directory mode 0750, certificate 0644 and private key 0640.
Never copy certificate keys into source, build context, images or chat.

Before initial issuance, route HTTP port 80 challenges for all three names to
`ACME_WEBROOT/.well-known/acme-challenge/`. If there is no existing proxy,
`deploy/nginx-acme-bootstrap.conf` provides a challenge-only HTTP configuration
on container port 8080; it serves no vault. If the landing proxy already owns
ports 80/443, add routing there rather than stopping it blindly. After DNS and
challenge routing are verified, the operator can issue with:

```sh
certbot certonly --webroot -w /var/lib/stellarvault/acme \
  -d svault.ir -d app.svault.ir -d api.svault.ir
```

Initialize a version directory under `TLS_CERTIFICATE_DIR`, install both files
with the permissions above, and create the relative `current` symlink before
starting the production Compose stack. No self-signed certificate is acceptable
for public deployment. Configure the host's Certbot renewal timer and an
operator-owned deploy hook calling `scripts/install-edge-certificate.sh` with
the absolute Certbot lineage, installed certificate parent and Compose env file.
The hook checks expiry, all three names and matching public keys, atomically switches
the version link, tests Nginx configuration and reloads without stopping workers.
It restores the previous link if validation/reload fails. Keep old versions for
rollback; restrict backups and record the renewal owner. Run `certbot renew
--dry-run`, exercise the installation hook against a temporary test deployment,
and verify the externally served expiry before accepting the VPS. See the
[official Certbot guide](https://eff-certbot.readthedocs.io/en/stable/using.html).

## Local HTTPS smoke

Generate isolated short-lived certificates with both test hostnames and mount
them under the same `current` layout. Bind the smoke stack to `127.0.0.1` using distinct
HTTP/HTTPS ports and a distinct Compose project. Trust only its exported test certificate/CA
for those test requests; never use `curl -k` or disable certificate validation.
On Windows Schannel, an internal CA without revocation services may require
`--ssl-revoke-best-effort`; certificate chain/hostname verification must remain
enabled. This smoke proves local proxy wiring, not public DNS/ACME/VPS readiness.
