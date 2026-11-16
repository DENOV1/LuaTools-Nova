# Windows Dependencies → Linux Implementation Map

Every Windows-specific API/behavior in the original, with the Linux-native replacement in Nova.

| # | Windows dependency | Linux implementation in Nova |
|---|---|---|
| 1 | WPF + WPF-UI 4.3 (FluentWindow, NavigationView, Snackbar, SymbolIcon, ProgressRing, ToggleSwitch) | Avalonia 11.2 + FluentAvalonia 2.3 (NavigationView, SymbolIcon, ProgressRing, ToggleSwitch); Fluent dark theme |
| 2 | WinForms `NotifyIcon` | Avalonia `TrayIcon` (Open/Exit menu, click-to-restore) |
| 3 | Mica backdrop + rounded corners | `TransparencyLevelHint="AcrylicBlur"` with solid `#202020` fallback |
| 4 | `Microsoft.Win32.Registry` — Steam path, language, autostart | `SteamPathLocator`: `~/.steam/steam` symlink (resolved, validated against `steam.sh`) → `~/.local/share/Steam` → `~/.steam/debian-installation` → `~/.steam/root`; Flatpak/Snap enumerated + flagged; language from `~/.steam/registry.vdf`; autostart = XDG `~/.config/autostart/*.desktop` |
| 5 | `steam.exe` process control | `ProcessControl`: `pgrep -x steam` via /proc; clean shutdown `steam -shutdown` (bounded) → SIGTERM → SIGKILL; relaunch resolved launcher detached with runtime env stripped; gamescope/SteamOS systemd-user unit restart when active |
| 6 | DPAPI (`ProtectedData`) | JSON session file `$XDG_DATA_HOME/LuaToolsNova/auth.json`, `chmod 600` (documented tradeoff; libsecret future work) |
| 7 | `%AppData%` | XDG: config → `$XDG_CONFIG_HOME/LuaToolsNova`, data → `$XDG_DATA_HOME/LuaToolsNova` |
| 8 | `explorer.exe /select` | `org.freedesktop.FileManager1` DBus `ShowItems` when available, else `xdg-open` parent dir |
| 9 | WPF clipboard | Avalonia `IClipboard` |
| 10 | `MessageBox.Show` | FluentAvalonia `ContentDialog` (same OK/Cancel semantics) |
| 11 | `OpenFolderDialog`/`OpenFileDialog` | Avalonia `StorageProvider` pickers |
| 12 | NTFS junction `.cef-enable-remote-debugging` | Linux: Steam CDP via launch flag `-cef-enable-remote-debugging`; Nova detects CDP liveness on 127.0.0.1:8080 and surfaces the requirement in Plugin page |
| 13 | `winmm.dll` proxy loader install | **Not supported on Linux** — documented in Plugin page (loader row replaced with CDP status) |
| 14 | OST/BST unlocker DLLs | **Not supported** (PE DLLs meaningless for Linux steam). Mode page shows Custom + explanation; mode persists via settings |
| 15 | `cmd.exe` shims (mklink, relaunch) | direct `Process.Start` + detached relaunch via `setsid nohup` |
| 16 | `HttpListener` on 127.0.0.1:6767 | `HttpListener` works on Linux (managed, loopback, non-privileged) |
| 17 | OAuth callback :53789 listener | same, works on Linux |
| 18 | Windows path normalization | `Path` handles `/`; ordinal comparisons; case-sensitive FS honored via exact resolved paths |
| 19 | `DriveInfo` free space | same API (`statvfs`) — reused |
| 20 | `System.Windows.Threading.Dispatcher` confinement | Avalonia `Dispatcher.UIThread.Post` (same semantics) |
| 21 | `SteamLanguage` registry read | `~/.steam/registry.vdf` parse (Moon-verified vocabulary) |
| 22 | appinfo.vdf launch-options editing | codec is byte-level (BinaryVdf) — platform-neutral; `appcache/appinfo.vdf` exists identically on Linux |
| 23 | DepotDownloaderMod.exe | official SteamRE `DepotDownloader` linux-x64 build, run via apphost or `dotnet DepotDownloader.dll`; identical flag protocol |
| 24 | Velopack Windows installer | Velopack Linux target; dev builds report "not installed" |
| 25 | `luatools://` registry handler | `.desktop` file with `MimeType=x-scheme-handler/luatools;luatoolsnova;` + `update-desktop-database`; pending-URL handoff (replaces named mutex/events) |

Documented non-carries (details in FEATURE_COMPATIBILITY.md): Steamless action, SteamAutoCrack button, CloudRedirect add-on, OST/BST install — each with a Linux-rationale paragraph and a UI-visible "Not supported on Linux" state.
