# SVAULT presentation website

Static English presentation website for `svault.ir`, with product calls to action pointing to `https://app.svault.ir`.

## Included
- Green terminal-inspired responsive presentation website
- Product capabilities, blockchain explanation, security model, Stellar relationship and Badje support/investment section
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
