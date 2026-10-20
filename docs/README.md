# StellarVault — AI-readable development map

This directory is the **source of truth for implementation steps**. Each milestone has its own Markdown file, with scope, prerequisites, compatibility rules, acceptance criteria, and status.

## Read in order

1. [00 — Product and architecture path](steps/00-path.md)
2. [01 — Landing website](steps/01-landing-website.md)
3. [02 — Shared Core](steps/02-shared-core.md)
4. [03 — Shared Razor UI](steps/03-shared-ui.md)
5. [04 — Web host](steps/04-web-host.md)
6. [05 — MAUI host](steps/05-maui-host.md)
7. [06 — Compatibility and production cutover](steps/06-compatibility-release.md)

## Working rules
- Make changes on `feature/maui-shared-app`; never deploy or merge automatically.
- Update the corresponding step file whenever implementation changes.
- Distinguish **planned**, **in progress**, **implemented**, and **verified**. A commit is not a successful build.
- Keep browser and native recovery formats identical.
- Never put secrets or real mnemonics into tests, documentation, or logs.
