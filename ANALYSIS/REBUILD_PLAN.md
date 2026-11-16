# Rebuild Plan (executed)

## Decisions
- **License:** MIT (original LuaTools is MIT and permits derivation with attribution; Moon (AGPL) contributes *knowledge only* — no code). See LICENSING_ANALYSIS.md.
- **UI framework:** Avalonia 11.2.8 + FluentAvaloniaUI 2.3.0 (closest WinUI-Fluent match to WPF-UI 4.3; NavigationView/SymbolIcon/ContentDialog parity).
- **.NET 8** (LTS; same major as original → maximum service-code port fidelity).
- **Layering** as in SOURCE_CODE_RELATIONSHIPS.md; interfaces own every OS boundary.
- **Localization:** English master first; satellite translations deferred (infrastructure-ready).

## Stages (all executed or tracked in PROJECT_STATUS.md)
1. Acquire repos ✓
2. Inventory ✓ (COMPLETE_FILE_INVENTORY.md)
3. Original analysis ✓ (ORIGINAL_ARCHITECTURE.md)
4. Moon analysis ✓ (MOON_ARCHITECTURE.md)
5. Architecture docs ✓
6. Feature matrix ✓ (FEATURE_COMPATIBILITY.md)
7. UI design system ✓ (UI_DESIGN_ANALYSIS.md)
8. Linux design ✓ (LINUX_IMPLEMENTATION_MAP.md)
9. Scaffold: 6 src projects + 4 test projects, sln, Directory.Build.props (nullable, implicit usings, net8.0)
10. UI reconstruction: App.axaml theme tokens from UI_DESIGN_ANALYSIS; MainWindow + NavigationView; 10 views; dialogs; toasts
11. Icon: assets/icon ladder (16–512 PNG + .ico + AppStream png), window icon, .desktop, packaging assets
12. Linux services: SteamPathLocator, ProcessControl, SystemIntegrations, ProtonCompat, WineOverrides, protocol registration
13. Feature implementation: services/VMs per inventory; Not-Supported states with explanations
14. Tests: xUnit per subsystem (lua codec/parser/editor, manifest codec, settings atomicity, DoH parser, link parser, mode migration, library discovery fixtures, queue semantics, path containment, proton compat, wine overrides, DTO contracts)
15. Build & test on Debian 13 (trixie) sandbox
16. Fix pass (tracked in KNOWN_ISSUES.md)
17. Packaging: `dotnet publish -r linux-x64 --self-contained` + .desktop + icons + publish script + CI
18. GitHub: `DENOV1/LuaTools-Nova` with the directive's description/topics; pushed staged commits
19. CI workflows: build+test on ubuntu-latest; publish artifact; release workflow (tag-gated)
20. Docs: README (install/build/distro notes), CONTRIBUTING, THIRD_PARTY_NOTICES

## Verification gates
- Every feature marked Implemented has its service/VM present AND exercised by at least one automated test OR an explicit manual-test note in TEST_RESULTS.md.
- Network-gated flows (search/download/auth/depots) are implemented against the same endpoints as the original but require live connectivity; their deterministic logic (parsing, merging, queueing, persistence) is covered by tests.
- No placeholder buttons: every command either performs real work or is disabled with a visible Not-Supported reason string.
