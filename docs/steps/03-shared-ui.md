# Step 03 — Shared Razor UI

**Status:** planned

## Actions
Create `src/StellarVault.UI/` as a Razor Class Library; extract existing `Pages/`, `Components/`, `Layout/` and reusable styles/assets. Register routes from the shared UI assembly in both hosts. Replace host-specific dependencies with injected interfaces.

## Acceptance criteria
Browser UI visually and functionally matches existing application, including folder breadcrumbs, scoped record search, approvals, and lock/unlock. No duplicated Razor screens in hosts.
