# Contributing to LuaTools Nova

## Development setup

1. Install the .NET 8 SDK (`dotnet-install.sh --channel 8.0` or your distro's package).
2. `dotnet build LuaTools.Nova.sln` — everything must compile with 0 errors.
3. `dotnet test LuaTools.Nova.sln` — all tests must pass.
4. `dotnet run --project src/LuaTools.UI` to launch the app.

## Ground rules

1. **Layering is enforced by project references.** Business code may not reference `LuaTools.Linux` concrete types — go through the interfaces in `LuaTools.Core.Services` (`ISteamLocator`, `IProcessControl`, `ISystemIntegrations`).
2. **No OS checks in business code.** New Linux behavior belongs in `LuaTools.Linux` behind an interface.
3. **Match the original's behavior**, not just its look. When porting a LuaTools feature, read the original service first and note divergences in comments (`// Derived from madoiscool/LuaTools (MIT)`).
4. **Never copy code from luatools-moon** (AGPL-3.0). Behavioral knowledge only — see `ANALYSIS/LICENSING_ANALYSIS.md`.
5. **Every bug fix ships with a test** that reproduces it first.
6. **Atomic writes + .bak for all persistence** under `$XDG_*_HOME/LuaToolsNova`; never block the UI thread on IO.
7. New user-visible strings go in the views; keep the design tokens from `App.axaml` (no hardcoded colors for card/chip/status roles).

## Commit style

Short imperative subject (`fix:`, `feat:`, `tests:`, `docs:`, `packaging:`), body explaining *why* when non-obvious.

## Where to start

`KNOWN_ISSUES.md` and the "Testing" rows in `FEATURE_TRACKER.md` list open work.
