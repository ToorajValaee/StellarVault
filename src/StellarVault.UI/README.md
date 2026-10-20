# Shared Razor UI

This Razor Class Library is the first portable UI project. It references StellarVault.Core and is intended to be consumed by both the Blazor WebAssembly host and MAUI Blazor Hybrid host.

**Status:** active extraction. `CostEstimatePanel` and `CostApprovalModal` have moved from the browser project to this Razor Class Library. The Access, Vault, Add Record pages, MainLayout, and ledger/record modals are now in this library. The browser Router discovers the shared pages via AdditionalAssemblies. Build and native parity testing remain pending. Keep browser-specific session, storage and JavaScript bridge dependencies outside portable components until abstractions are in place.
