# UI Design Analysis — Original LuaTools visual system

The original's look is **WPF-UI 4.3.0 Dark theme** as a base, overlaid with a hand-tuned "glass card" system. Nova replicates it in Avalonia + FluentAvalonia with the same tokens (all extracted from the original XAML literals).

## Base (WPF-UI Dark)
- Window background `#202020` (Nova: solid, with AcrylicBlur hint).
- `TextFillColorPrimaryBrush` ≈ `#FFFFFF`; Nova uses Tailwind grays for body/secondary as the original does in-page.
- Accent pinned to `#a78bfa` (violet) — the color the original hard-overrides everywhere.

## App palette (extracted literals — the ground truth)
| Role | Value |
|---|---|
| Card bg / border | `#0DFFFFFF` / `#14FFFFFF` (hover `#18FFFFFF` / `#33FFFFFF`) |
| Subtle card | bg `#08FFFFFF`, border `#12FFFFFF` |
| Row hover / dividers | `#14FFFFFF` / `#10FFFFFF` |
| Accent chips | bg `#2a2440` / fg `#c4b5fd`; dim bg `#14a78bfa`, border `#40a78bfa` |
| Success | `#34d399`, badge `#1422c55e` + border `#3022c55e` |
| Warning | `#fbbf24` (badge `#20f59e0b`), warn text `#f0a868`, depot-missing `#0Ff97316`/`#38f97316` |
| Error | `#f87171` |
| Info | `#38bdf8`, banner `#1A3b82f6`/`#333b82f6`/text `#93c5fd`, recommended `#202563eb`/`#60a5fa` |
| Shared chip | bg `#3a3320`, fg `#d6b36b` |
| Text grays | body `#d1d5db` / `#e5e7eb`; secondary `#9ca3af`; muted `#6b7280`; faint `#4b5563` |
| Overlays | scrim `#99000000`; dialog bg `#1c1c22` border `#33FFFFFF` radius 12; selection bar `#1f1f27`; dropdown `#14141c` |
| Manifest pins (Consolas) | active `#8b96f0`, outdated `#f0a868` |

## Typography
- Segoe UI default → Nova: Inter + system fallback; Consolas for monospace pins/editor.
- Scale: page title 22 Bold; card title 15 SemiBold; section header 13 SemiBold gray; body 12.5–13; secondary 11.5–12; meta 10.5–11; chips 9.5–10.5 SemiBold; home stat 28 Bold violet.

## Layout grammar
- Content max widths: Home/Mode/Plugin 760, Add/Settings 640; Manage/Builds/Fixes full-bleed with `Margin 8,16,16,8`.
- Cards `Padding 16,14`–`18`, CornerRadius 8–10, 1px borders; grids 248×208 tiles gap 12.
- NavigationView pane 190px; pager centered ‹ | windowed numbers | ›, per-page combo right.
- Flyouts: right slide-in 380 (Manage) / 420 (Fixes) `#1c1c22`, scrim click-to-close.
- Toasts bottom-right margin `0,0,24,18`; onboarding card 640 max radius 14 padding 30,32 with Yes/No segmented buttons.

## Nova mapping (implemented)
- All literals above are centralized as `NovaTheme` resources in `App.axaml` (`CardBg`, `AccentBrush`, `SuccessBrush`, …) plus styles (`GlassCard`, `GlassCardSubtle`, `ChipBase`, `ChipText`, `NovaPageTitle`, `NovaSectionHeader`, `NovaSubtitle`).
- FluentAvalonia dark theme supplies Fluent control chrome (buttons/toggles/combos/scrollbars) matching WPF-UI's WinUI idiom.
- NavigationView mirrors the original's 7 menu + 2 footer items with the same order and icons (Home/Add/Manage/Depots/Mode/Fixes/Plugin + Downloads/Settings).
