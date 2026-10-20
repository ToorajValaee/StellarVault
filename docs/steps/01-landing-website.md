# Step 01 — Public landing website

**Status:** in progress — original landing source has been pushed and key source files verified in GitHub. Runtime/Docker and asset-integrity checks are still pending.

## Source and verified contents
The supplied archive contains the standalone site: `index.html`, `privacy.html`, `terms.html`, Persian redirect pages, CSS, JavaScript, screenshots, icons, social preview, Dockerfile, nginx.conf, sitemap, robots.txt, `llms.txt` and `llms-full.txt`.

The original landing describes the currently available responsive PWA and links to `https://app.svault.ir`. Native Android/iOS/desktop MAUI builds are future work and must not be advertised as released.

## Import procedure
On a local clone of `feature/maui-shared-app`, with the ZIP downloaded:

```bash
python3 scripts/import-landing.py "/path/to/svault-website-docker (1).zip"
# or use svault-landing-repo-package.zip
git status --short
git add website/
git commit -m "feat(website): import original static landing and assets"
git push origin feature/maui-shared-app
```

The importer copies all original files without re-encoding, rejects traversal/symlinks, validates required files and writes a SHA-256 source manifest. The importer intentionally does not push to GitHub.

## Content review
1. Confirm `svault.ir` canonical, OG metadata, sitemap and links are accurate.
2. Confirm `app.svault.ir` links, privacy and terms pages.
3. Describe MAUI native apps as **in development**, not already available.
4. Confirm screenshots still represent the current product.
5. Keep landing deployment separate from browser and MAUI hosts.

## Acceptance criteria
Original HTML, stylesheet, Dockerfile, source manifest and LLM description confirmed present on the branch. Browser rendering, all asset checksums, Docker/Nginx behavior, and legal review **not yet verified**.
