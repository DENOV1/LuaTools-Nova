# KNOWN_ISSUES — LuaTools Nova v1.0.1

## Open (honest list)

1. **No GUI run on a real display yet.** The v1.0.1 audit verified the app boots through the full Avalonia pipeline headlessly (real MainWindow, all 10 pages, navigation, Show/Close). A real Linux Mint 22.3 desktop session is required to visually confirm rendering and compare against the original design. **This is why v1.0.1 ships as a pre-release.**
2. **No live Steam verification.** Discovery/lua install/restart/depot paths are implemented against the documented layouts and covered by unit tests with fixtures, but never ran against a live Steam install.
3. **No live network verification.** API clients target the same endpoints as the original; endpoint drift since the original's last release is unverified here.
4. **System `/opt` install not executed** (requires root). User-local `DEST_OVERRIDE` mode fully executed; same code path.
5. **Velopack auto-update channel not configured** — releases ship as tar.gz via the tag-gated CI workflow; in-app update is deferred.
6. **Launch options editor dialog** is a stub action in Manage (appinfo.vdf codec is ported and ready); full editor UI is open work.
7. **English-only UI**; translation infrastructure ready, satellite resx not carried.
8. **BuildsView depot search** re-filters visible rows; group counts refresh on next depot load. Cosmetic.

## Fixed in v1.0.1 (see TEST_RESULTS.md §3 for evidence)

- **[CRITICAL]** v1.0.0 startup crash: split composition root left `UnlockerServiceProxy` + 14 more services/VMs unregistered. Fixed: single DI container with ValidateOnBuild; regression-tested.
- **[MAJOR]** `MainWindow` hard-cast `IToastService` → concrete class; fixed via `IToastPresenter`.
- **[MINOR]** `install.sh`: user-local desktop entry kept `/opt` Exec path; missing applications-dir creation. Fixed; scripts executed and verified.
- **[MINOR]** `publish.sh` failed without `dotnet` on PATH. Fixed.

## Deliberate non-carries (Linux rationale, UI-surfaced)

- Steamless (Windows .NET Framework CLI), SteamAutoCrack (Windows GUI), CloudRedirect (Windows DLL hook), OST/BST unlocker modes (PE-DLL injection into steam.exe — no Linux mechanism).
