# DEVELOPMENT_LOG — LuaTools Nova

## Session 1 — 2026-11-16

1. **Environment.** Installed .NET SDK 8.0.428 (workspace-local `tools/dotnet`) + GitHub CLI 2.104.0; authenticated as `DENOV1` via PAT stored in workspace `.env` (gitignored, chmod 600). Cloned `madoiscool/LuaTools` (main + downloads branch, 19 tags) and `swwayps/luatools-moon` (main + millennium, 4 tags).
2. **Icon.** Processed the attached 1254×1254 master into an 11-asset ladder (16–512 PNG, multi-res .ico, AppStream 128).
3. **Analysis.** Read the original's entire C#/XAML tree (~76 files: all views, 40+ services, 14 VMs, i18n master) and Moon's Lua backend + shell workers + installer; summarized Moon's 11k-line frontend JS and ~65-test suite via structured agent reports. Produced the 11 `ANALYSIS/` documents.
4. **Key design discoveries.** Original = WPF-UI 4.3 Fluent dark glass-card design (tokens extracted from XAML literals); Moon = behavioral reference for Linux Steam (bootstrap symlink invariant, runtime-env stripping, clean-shutdown ladder, libraryfolders union, Proton mapping, WINEDLLOVERRIDES semantics). Licensing: original MIT (derive with attribution), Moon AGPL (knowledge only — zero code reuse).
5. **Scaffold.** 6 src + 4 test projects; pinned packages after verifying nuget IDs/TFM compatibility (FluentAvaloniaUI 2.3.0 for net8/Avalonia 11.2; Velopack 1.2.0).
6. **Implementation.** Ported the original's services/VMs layer-by-layer into Core/Infrastructure/Linux/Integrations/Application; built the Avalonia UI (theme tokens from the analysis, NavigationView shell mirroring the original's 7+2 items, 10 views, dialogs, toasts, tray).
7. **Setback + recovery.** A sandbox recycle wiped the toolchain, clones, and four of five layers mid-UI-work. Everything was rebuilt from the in-context sources; git commits added as recycle insurance.
8. **UI compile fixes.** Resolved FluentAvalonia API differences: clr-namespace xmlns mapping (the `urn:fluent-avalonia` mapping didn't survive compiled-XAML resolution), SymbolIcon uses the `Symbol` enum (dumped real member names via reflection; mapped BranchFork→Library, ShieldTask→Repair, PuzzlePiece→Code, Info→Help, Person→Contact, Circle→Dismiss, Warning→Alert, ArrowDownload→Download, Options→Manage, Wrench→Repair), `x:Static` enum refs, IconSource (not Icon), bool `IsBackButtonVisible`, ToggleSwitch from Avalonia proper, custom converters for the missing ObjectConverters members, keyless styles moved to Application.Styles, ScrollViewer double-content wrapped.
9. **Tests.** Wrote 4 suites; **they caught 3 real product bugs** — WineOverrides lazy-quantifier regex mistranslation, symlink not followed in SteamPathLocator, PascalCase JSON breaking the plugin popup DTO contract — all fixed with tests green: 56+16+13+17 = 102 assertions-passing cases (89 logical tests).
10. **Packaging.** `publish.sh` (test + self-contained linux-x64 publish + staging), `install.sh`/`uninstall.sh` (/opt/luatools-nova + .desktop + hicolor icons + protocol), CI workflow (build+test+publish artifact, tag-gated release). Verified: 99 MB bundle runs natively; terminates at `XOpenDisplay` (no display in sandbox) — expected headless result.
11. **Publish.** GitHub repo `DENOV1/LuaTools-Nova` created with the directive's exact description and topics; main branch pushed.

## Honest ledger

- Verified here: build, tests, publish, native execution (headless), packaging scripts.
- Not verified here: GUI rendering, live Steam interaction, live network flows (no display, no Steam, documented in TEST_RESULTS.md / KNOWN_ISSUES.md).
