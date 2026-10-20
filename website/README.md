# StellarVault public website

This directory is reserved for the standalone static landing site served at svault.ir. It must not be hosted from the vault application or bundled into MAUI.

Import the contents of the supplied `svault-landing-repo-package.zip` **website/** directory here, preserving its Dockerfile, nginx.conf, legal pages, assets and SEO files.

The original ZIP contains binary screenshots and images, so those assets must be imported intact; placeholder HTML is not a substitute.

The landing website and the vault app should use separate build/deployment pipelines. The app is intended for app.svault.ir.

Do not overwrite this README when importing unless the imported documentation is intentionally preferred.
