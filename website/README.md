# StellarVault public landing site

**Status: source import pending.** The actual user-supplied landing package has not yet been committed. See [Step 01](../docs/steps/01-landing-website.md) for the checklist and acceptance criteria.

## Hosting
- Public site: `svault.ir`
- Vault app: `app.svault.ir`
- Independent Docker/Nginx deployment; do not mix landing files into the vault WebAssembly host.

## Content policy during migration
- Describe the browser/PWA vault as currently available, subject to production readiness.
- Label Android, iOS and desktop MAUI applications as **planned/in development** until tested and released.
- Do not claim that the migration changes wallet keys, encrypted records, or user recovery phrases.
- Keep English/Persian pages, privacy, terms, sitemap, robots, `llms.txt` and `llms-full.txt` consistent.
- Keep links to the app and legal pages functional.

## Import
Copy the full contents of the prepared landing package's `website/` folder into this directory, including binary images and screenshots. Preserve the original `Dockerfile`, `nginx.conf`, assets and legal pages. Do not replace missing files with placeholders.

The full landing source remains in the user-provided archive until imported.
