# SVAULT public landing site

**Status:** awaiting original asset import. See [Step 01](../docs/steps/01-landing-website.md).

This directory is the standalone static site for `https://svault.ir`, deployed separately from the vault at `https://app.svault.ir`.

## Import source intact

```bash
python3 scripts/import-landing.py "/path/to/svault-website-docker (1).zip"
```

Alternatively pass `svault-landing-repo-package.zip`. The script copies the original HTML/CSS/JS, images, screenshots, legal pages, SEO files and Docker/Nginx config, then creates `source-manifest.json` with SHA-256 hashes.

## Content rules
- The existing browser/PWA vault is the current product.
- Native MAUI Android/iOS/Windows/macOS applications are **planned**, not released.
- Do not promise zero fees or put readable secrets on Stellar.
- Preserve the original multilingual routes and legal content.
- Verify all site-to-app links and metadata before deployment.

## Local test after import

```bash
docker build -t svault-website ./website
docker run --rm -p 8080:80 svault-website
```

Then open `http://localhost:8080` and verify pages and images. No production deployment is implied by this step.
