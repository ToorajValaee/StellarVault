# Step 01 — Public landing website

**Status:** in progress — `website/` documentation exists; original website files have **not** been committed.

## Source
User-provided `svault-website-docker (1).zip` / prepared `svault-landing-repo-package.zip`. The package contains static HTML, CSS, JS, images, multilingual redirects, privacy/terms, robots/sitemap/llms text, and Docker/Nginx.

## Required actions
1. Extract package's `website/` content directly into repository `website/`.
2. Preserve binary assets, fonts and screenshots byte-for-byte; do not use placeholders.
3. Keep Docker build context at `website/`; do not include this site in the vault app's WebAssembly assets.
4. Audit landing copy against implemented features; distinguish current web/PWA availability from **planned** native MAUI apps.
5. Confirm `svault.ir` links to `app.svault.ir` for vault access and that legal/SEO links are correct.
6. Add a website-specific build/deployment workflow after assets are committed.

## Acceptance criteria
All links, scripts and images resolve; static pages render on mobile/desktop; Docker/Nginx serve the landing and correct fallbacks; `llms.txt` and sitemap reflect current public URLs. Do not mark verified without testing.
