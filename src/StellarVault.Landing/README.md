# StellarVault.Landing

Independent static presentation host for https://svault.ir/. The browser app at https://app.svault.ir/ and API at https://api.svault.ir/ are separate hosts.

- wwwroot/: deployable public HTML, CSS, JavaScript, images and discovery files.
- Dockerfile, docker-compose.yml and nginx.conf: host configuration, excluded from the document root.
- source-manifest.json: historical archive provenance, not a current-release checksum or readiness claim.

No .NET project or engine dependency is needed. Wallet, signing and API behavior belong to the application hosts and engine/provider libraries.

Serve only wwwroot as the document root, or run Docker Compose from this directory. No build is required. Run node scripts/verify-landing.cjs from the repository root to validate links, original binary assets and public-root boundaries. Importing an archive requires --replace to overwrite maintained source.

Copy describes separate HD/S-key access, opt-in encrypted drafts/manual Sync and file limits. API supports authenticated read-only diagnostics on configured customer networks. Hosted writes, EVM and native releases are future work. Production/security validation remains incomplete; align claims with docs/ENGINEERING_HANDOFF.md.

English only; fa routes redirect to English. Licensed Dana fonts are not included. Legal pages remain drafts requiring operator-specific review before commercial use.
