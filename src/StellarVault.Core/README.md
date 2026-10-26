# StellarVault.Core

First migration stage: compile the **existing** vault data contracts into a reusable .NET class library. The models remain in `Core/Models.cs` temporarily and are linked here, so no serialization or derivation behavior changes.

The existing Blazor WebAssembly host references this project and excludes the linked model source from its own compilation to avoid duplicate type definitions.

Next: migrate platform-independent services into this library, then relocate the model file into this directory after browser/native compatibility tests.
