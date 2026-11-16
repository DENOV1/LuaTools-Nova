# Third-party notices

LuaTools Nova is MIT-licensed. The following components are bundled or consumed:

| Component | License | Use |
|---|---|---|
| [LuaTools](https://github.com/madoiscool/LuaTools) | MIT | Original Windows app — portions of Nova's logic are ports of it (upstream MIT notice is embedded in `LICENSE`) |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) 11.2.8 | MIT | UI framework |
| [Avalonia.Desktop / Themes.Fluent / Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) 11.2.8 | MIT | windowing, theme, typography |
| [FluentAvaloniaUI](https://github.com/amwx/FluentAvalonia) 2.3.0 | MIT | NavigationView, SymbolIcon, ContentDialog, Fluent theme |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.2 | MIT | source-generated MVVM |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) 8.0.1 | MIT | dependency injection |
| [Markdig](https://github.com/xoofx/markdig) 0.37.0 | BSD-2-Clause | markdown parsing |
| [Velopack](https://github.com/velopack/velopack) 1.2.0 | MIT | update infrastructure (channel deferred) |
| [.NET 8 runtime](https://github.com/dotnet/runtime) | MIT | runtime (self-contained publish bundles it) |
| [xunit](https://github.com/xunit/xunit) 2.9.2 | Apache-2.0 | test framework |

**Not bundled:** Steam, DepotDownloader (fetched at runtime from [SteamRE](https://github.com/SteamRE/DepotDownloader) releases), the LuaTools store plugin frontend (fetched at runtime from [LTSP](https://github.com/madoiscool/LTSP) releases), any AGPL code (see `ANALYSIS/LICENSING_ANALYSIS.md`).

**Assets:** `assets/icon/*` derive from the icon supplied by the project requester.
