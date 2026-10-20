# Step 05 — MAUI Blazor Hybrid host

**Status:** in progress — initial native project, platform entry points and preview screen committed; **not build-verified**.

## Implemented
- `src/StellarVault.Maui/StellarVault.Maui.csproj` references shared Core and Razor UI.
- Native entry points for Android, iOS, Mac Catalyst and Windows.
- BlazorWebView with a shared UI component and an explicit non-functional preview screen.

## Not yet implemented
- Existing vault pages, session, network settings and encrypted Stellar storage in native runtime.
- Native secret persistence policies and cryptographic JS interop bridge.
- Production app assets, platform signing, CI build, device tests and store packaging.

## Next
1. Verify native project builds with .NET 8 MAUI workload.
2. Extract the existing browser UI to shared RCL.
3. Define platform-neutral vault service interfaces and native implementations.
4. Test cross-platform recovery and derivation against synthetic vectors.
5. Enable native vault functionality only after compatibility and security checks.

## Acceptance criteria
Same shared pages render on native targets and recover existing browser vaults without derivation/format changes. **Not yet satisfied.**
