# Dependency Map

## Original LuaTools (Windows)

NuGet: WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.Hosting, Velopack 1.2.0, VirtualizingWrapPanel, Markdig.Wpf, System.Security.Cryptography.ProtectedData, UseWindowsForms (tray).

External processes (downloaded at runtime): DepotDownloaderMod (win exe), Steamless (win), Steam-auto-crack (win GUI), CloudRedirect (win).

External services: lua.tools API, Supabase (db.lua.tools), Hubcap, api.steamcmd.net, store.steampowered.com (storesearch/featuredcategories/appdetails), applist.morrenus.xyz, api.github.com (+mirrors), jsnli hardware list, Umami, LTSP releases.

## luatools-moon

Runtime: LuaJIT (host), Millennium/Lumen fs/http modules, system curl/unzip, bundled 7zz, jq (installer). External: GitHub, jsDelivr pinned mirror, hubcapmanifest.com, lua.tools, files/index.luatools.work, api.perondepot.xyz, protondb.com, steamcommunity.com SearchApps, api.steamcmd.net.

## LuaTools Nova (target, all verified on nuget.org)

| Package | Version | Role |
|---|---|---|
| Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent | 11.2.8 | UI framework |
| Avalonia.Fonts.Inter | 11.2.8 | typography |
| FluentAvaloniaUI | 2.3.0 | NavigationView/SymbolIcon/ContentDialog (Fluent chrome) |
| CommunityToolkit.Mvvm | 8.4.2 | ObservableProperty/RelayCommand (same as original) |
| Microsoft.Extensions.Hosting | 8.0.1 | DI (same as original) |
| Markdig | 0.37.0 | markdown (future Fixes descriptions) |
| Velopack | 1.2.0 | update infrastructure |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk / coverlet | 2.9.2 / 2.8.2 / 17.11.1 | tests |

Runtime system deps on Linux: none mandatory beyond the self-contained bundle; optional: `xdg-open`, `unzip`/`7z` (fix archives), `steam` for shutdown, `update-desktop-database` (protocol refresh). All optional usage degrades gracefully.

Persistence is JSON files under XDG dirs (mirrors the original's file-based design; no SQLite).
