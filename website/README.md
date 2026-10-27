# SVAULT presentation website

Static English presentation website for `svault.ir`, with product calls to action pointing to `https://app.svault.ir`.

## Included
- Green terminal-inspired responsive presentation website
- Structured records, folders, file uploads, two storage modes, blockchain explanation, security model, Stellar relationship and Badje support/investment section
- Completed Privacy Policy and Terms of Use pages
- SEO metadata, structured data, sitemap, robots.txt and LLM-readable project descriptions
- No build process required

## Language status
Persian is temporarily disabled. The language switch has been removed, Persian URLs are excluded from the sitemap, and the files under `/fa/` are noindex redirect pages to their English equivalents.

When deploying over an older version, replace the entire document root so stale Persian content is not left online.

## Deploy
Upload the contents of this folder to the document root of `svault.ir`.

## Important legal review
The Privacy Policy and Terms are complete product-facing drafts based on the currently described architecture. Before commercial or regulated use, have qualified counsel confirm:
- Badje's full legal entity name and postal address
- governing law and dispute forum
- actual hosting/log retention and service providers
- production encryption, browser-storage and Stellar ledger behavior
- local consumer, privacy, export-control and sanctions requirements

## Dana font
Dana remains configured in `assets/styles.css`, but Persian is currently disabled. Licensed font files are not included.


## Cross-platform roadmap

The currently available application is the browser/PWA vault at https://app.svault.ir. Native Android, iOS, Windows and macOS apps using shared MAUI Blazor Hybrid UI are **in development** and should not be described as released. See [migration steps](../docs/README.md).

## Source integrity check

Run `python3 scripts/verify-landing.py` from the repository root to compare imported assets with `website/source-manifest.json`. The existence of this manifest does not by itself prove deployment correctness.

## Current feature-status disclaimer
The landing describes the implemented on-chain vault and the opt-in encrypted device credential path. Offline editing, durable pending-change storage, conflict reconciliation and manual ledger synchronization remain under development. Do not advertise those workflows as ready until integrated user-flow and security tests pass.
