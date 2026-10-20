# Shared Razor UI

This Razor Class Library is the first portable UI project. It references StellarVault.Core and is intended to be consumed by both the Blazor WebAssembly host and MAUI Blazor Hybrid host.

**Status:** scaffolding only. The existing pages and layouts still live in the browser project; migration and parity testing are pending. Keep browser-specific session, storage and JavaScript bridge dependencies outside portable components until abstractions are in place.
