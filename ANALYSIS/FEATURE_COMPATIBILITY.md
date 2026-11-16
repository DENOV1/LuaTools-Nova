# Feature Compatibility Matrix (Original → Nova on Linux)

Statuses as of the initial build (see TEST_RESULTS.md for what was verified):

| Original feature | Nova status | Notes |
|---|---|---|
| Fluent dark shell, NavigationView 7+2 items, titlebar, toasts, onboarding overlay | **Implemented** | Avalonia + FluentAvalonia; same palette/sizes/layout tokens |
| Steam discovery + path override + validation | **Implemented** | symlink/paths/Flatpak-Snap detection; unit-tested |
| Libraries + installed games scan (acf) | **Implemented** | + config/ vdf union rule; unit-tested |
| Home dashboard (stat tiles, status rows, recent covers, drop zone) | **Implemented** | same layout/palette |
| Add page: search, appid/link entry, featured strips, details card, FastFetch, sources + Hubcap synth + usage badges, DLC info, overwrite diff, drop install | **Implemented** | identical API surfaces (public Steam endpoints, lua.tools, Hubcap); requires live network to exercise |
| Manage page: paged tile grid, search, 6 filters/5 sorts, flyout, multi-select delete, launch options action | **Implemented** | filters/sorts identical; appdetails backfill identical |
| Depots/Builds page: picker, vault variants, 3-way depot breakdown, per-depot enable/lock toggles, lua editor + presets + rename | **Implemented** | vault + editor + toggles fully ported; depot download uses linux-x64 DepotDownloader (network-gated) |
| Downloads page: queue/history, pause/resume depot, retry, cancel, show-in-folder | **Implemented** | pause/resume/-validate semantics preserved; unit-tested |
| Mode page (OST/BST/Custom) | **Implemented** (structure) / OST+BST **Not Supported** | Windows DLL unlockers have no Linux path; Custom active; explanatory card |
| Plugin page (install/update/uninstall LTSP frontend, bridge status) | **Implemented** (frontend) / loader **Not Supported** | frontend zip → XDG plugin dir; CDP injection via `-cef-enable-remote-debugging`; winmm loader not applicable |
| Store-page popup backend (HTTP 6767 + CDP drain) | **Implemented** | pure .NET; requires Steam with CDP enabled |
| Fixes page (listings grid, tags, my-games, per-fix manifest/fix/revert with hash-guarded records) | **Implemented** | fix zip extraction with `.luatools-fix/` backups; Proton gating; Wine overrides merge (Nova-native, unit-tested) |
| Discord OAuth + bot-code sign-in | **Implemented** | DPAPI→600-perms file; network-gated flows |
| Donate keys | **Implemented** | config.vdf parse identical |
| Depot download (DepotDownloader) | **Implemented** (code) — **Testing** (end-to-end) | linux-x64 tool; resume/-validate preserved; real downloads need live keys → flagged |
| Auto-update (Velopack) | **Deferred** | infra present; Linux release channel not configured in first build |
| Steamless DRM removal | **Not Supported** | .NET Framework CLI (Windows-only); action visible but disabled with explanation |
| SteamAutoCrack launcher | **Not Supported** | ships `net10.0-windows` GUI; no Linux build |
| CloudRedirect add-on | **Not Supported** | Windows DLL hook; Linux equivalent needs slsteam-moon LD_PRELOAD hook (third-party, out of scope) |
| Tray (minimize-to-tray, open/exit) | **Implemented** | Avalonia TrayIcon; setting-gated |
| Restart Steam | **Implemented** | clean-shutdown ladder + gamescope units |
| luatools:// protocol | **Implemented** (luatoolsnova:// + luatools:// accepted) | .desktop MimeType |
| Analytics, hardware blacklist, DNS-over-HTTPS, GitHub mirrors | **Implemented** | unchanged behavior |
| Depotcache migration (legacy path repair) | **Implemented** | harmless on fresh Linux installs |
| Multi-language UI (29 languages) | **Deferred** | i18n infrastructure ready; English shipped first |
| Big-Picture/gamepad UI, store themes | **Excluded** | that UI belongs to the Steam plugin, not the desktop app |

Totals: Implemented 21, Deferred 2, Not Supported 4 (all with documented Linux justification), Excluded 1.
