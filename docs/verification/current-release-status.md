# Current web release status

Updated 2026-11-25. The release branch is `main`; the architecture branch is no
longer the current deployment branch. This status supersedes older dated
implementation snapshots, without treating design notes as test evidence.

## Deployed and checked

- Landing, PWA and authenticated read-only B2B API containers are deployed on
  the VPS behind its existing shared Traefik proxy, with rollback images and
  configuration retained.
- Certificate-valid origin HTTPS returned 200 for landing, PWA and API readiness.
  Anonymous API calls returned 401; an authenticated operator call returned only
  granted network aliases. Hosted customer storage/signing endpoints do not exist.
- 253 Node regressions passed. Local .NET builds/publish, API boundary and HTTP
  tests passed during preparation; these are not an independent security audit.
- Offline encrypted draft/folder creation, privacy-lock reload, incorrect
  passphrase rejection and correct reopen passed in Edge. Actual current-design
  screenshots with synthetic data are used by the landing page.
- Isolated Edge process termination/restart, two-tab durable recovery CAS,
  wrong-key/network rejection and storage-abort tests passed.
- Live custom-Testnet HD parent/child and small streamed file leaf/root passed
  the existing historical queue verification path, with root-last publication.
  Separate S-key create/update/delete historical comparisons passed earlier.

## Acceptance still open

- Public Cloudflare-facing TLS was failing during the latest external check,
  while origin TLS worked. Cloudflare SSL settings could not be inspected with
  the server's DNS-only credential. Full (strict) and an active edge certificate
  must be checked separately from the origin certificate.
- The live PWA file-export test is not accepted yet. Browser-native 16 MiB
  streaming/hash/abort tests passed using an isolated OPFS destination, but that
  is not a substitute for the complete deployed export path and real system picker.
- Final immutable-image security/SBOM evidence, real supported-mobile-browser
  acceptance and independent cryptographic/application review are not complete.
- Whole-tree deletion, legacy migration and Mainnet S-key writes remain gated.
  Native releases and a hosted B2B vault-write API are outside this web scope.

The services being deployed does not close these checks. The README's preview
warning remains until there is evidence for the outstanding acceptance items;
it must not be removed merely to change the release label.
