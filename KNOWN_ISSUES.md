# KNOWN_ISSUES — LuaTools Nova v1.0.0

## Verified on this sandbox

1. **No GUI run yet.** The sandbox has no X11/Wayland server; the app builds, tests, publishes, and executes up to `XOpenDisplay`. A real desktop session is required to visually verify pages, and any Avalonia layout/runtime issue found there is not yet known. (OPEN)
2. **No Steam on this machine.** Discovery/lua install/restart/depot paths are implemented against the documented layouts and covered by unit tests with fixtures, but never ran against a live Steam install. (OPEN)
3. **Network flows untested live.** All API clients target the same endpoints as the original app; live behavior (and any lua.tools/Hubcap endpoint drift since the original's last release) is unverified here. (OPEN)

## Design-level limitations (documented, intentional)

4. **Windows-only tools not available on Linux:** Steamless (.NET Framework CLI), SteamAutoCrack (net10.0-windows GUI), CloudRedirect (Windows DLL hook + requires slsteam-moon-style LD_PRELOAD). The UI surfaces these as disabled/Not-Supported with explanations rather than fake buttons.
5. **OST/BST unlocker modes:** Windows DLL-proxy injection into steam.exe has no Linux equivalent. Mode page offers Custom and documents why; modes persist via settings.
6. **Plugin loader (winmm proxy):** replaced by Steam's own CEF debug bridge; requires launching Steam with `-cef-enable-remote-debugging` (fixed port 8080; a busy port is the same limitation the original documents).
7. **Auth token storage:** DPAPI replaced by a `chmod 600` JSON file (no OS keystore). libsecret integration is future work; token material is never written to the repo.
8. **Auto-update deferred:** Velopack infrastructure referenced but the Linux release channel is not configured for v1.0.0; releases ship as tar.gz via the tag-gated CI workflow.

## Minor / cosmetic

9. **AVLN3001 publish warning** (MainWindow has no parameterless ctor — DI-injected window). Benign; same pattern as the original app.
10. **UI language:** English only in v1.0.0; the resx/translation infrastructure is ready but satellite translations were not carried.
11. **Launch options dialog:** the appinfo.vdf codec is ported and unit-testable, but the standalone 820×600 editor dialog is reachable as a stub action in Manage (opens an info dialog). Wiring the full editor UI is open work. (OPEN)
12. **BuildsView depot search** re-filters the visible rows but does not re-render the collapsed group counts live until the next depot load. Cosmetic. (OPEN)
13. **Depot download end-to-end** needs a signed-in user with lua-borne depot keys plus Steam's depotcache; the tool acquisition + flag protocol are implemented and the queue semantics are tested, but a real download was not performed here. (OPEN)
