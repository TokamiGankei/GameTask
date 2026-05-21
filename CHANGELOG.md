# Changelog

All notable changes to GameTask are documented here.  
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) — versioning follows [Semantic Versioning](https://semver.org/).

---

## [v1.2.1] — 2026-05-21

### Fixed
- Build failure due to missing `Newtonsoft.Json` and `PresentationCore` references in `GameTaskPlugin.csproj`
- Missing `using` directives in `PluginSettings.cs` (`System.Collections.Generic`, `Newtonsoft.Json`, `Playnite.SDK`, `Playnite.SDK.Plugins`)
- Incorrect `VerifySettings` signature — corrected to `out List<string> errors` as required by `ISettings`
- `SettingsView.xaml` not declared as `<Page>` in `.csproj`, preventing XAML compilation
- Missing `WindowsBase` reference in `GameTaskPlugin.csproj`, required by `SettingsView.xaml`
- Switched project SDK to `Microsoft.NET.Sdk.WindowsDesktop` with `<UseWPF>true</UseWPF>` — required for XAML compilation and `InitializeComponent` generation
- Plugin failing to load in Playnite due to `Newtonsoft.Json` version conflict — now uses the version bundled with Playnite instead of packaging its own
- Removed `Newtonsoft.Json` dependency entirely — settings now use a simple INI format to avoid assembly version conflicts with Playnite's bundled libraries
---

## [v1.2.0] — 2026-05-20

### Added
- **Native settings page** — GameTask now has a proper settings screen accessible via Settings → Plugins → GameTask, with checkboxes and tooltips for each option
- **Corrupted task detection** — on startup, GameTask checks whether the `.exe` registered for each scheduled task still exists on disk; shows a fix notification per affected game
- **Success notification** — after creating scheduled tasks, a notification confirms how many were created successfully (and how many failed, if any)
- **Game count in main menu** — "Repair All Tagged Games" now shows the number of tagged games in parentheses, e.g. "Repair All Tagged Games (12)"
- **Settings toggles in main menu** — quick-access toggles under Extensions → GameTask → Settings for all three options, useful in Playnite fullscreen mode

### Changed
- Settings are now also configurable via the native Playnite settings page (Settings → Plugins → GameTask), in addition to the main menu toggles
- `PluginSettings` now inherits from `ObservableObject` for proper XAML data binding

---

## [v1.1.0] — 2026-05-20

### Added
- **Orphan task detection** — on startup, GameTask compares the library against the Windows Task Scheduler and notifies if tasks exist for games no longer in the library
- **Clean Orphan Tasks** — new option in Extensions → GameTask to remove orphan tasks with a single UAC prompt
- **Repair All Tagged Games** — new option in Extensions → GameTask to run Repair on every tagged game at once
- **Settings toggles** — "Bring Game to Foreground" and "Detect Orphan Tasks on Startup" toggles in Extensions → GameTask → Settings
- **`PluginSettings.cs`** — new file persisting settings to `Config/Settings.json`

### Fixed
- **Game window opening behind other windows** — the launcher `.vbs` now uses WMI to find the game process by PID (most recently created matching process) and calls `AppActivate` with the exact PID, preventing focus from going to the wrong window when multiple processes share the same executable name

---

## [v1.0.2] — 2026-05-20

### Fixed
- **Game window focus in Playnite fullscreen** — games launched via scheduled task no longer open behind the Playnite window; the launcher now polls for the game process and brings it to the foreground automatically (reported by @bobokaka)

---

## [v1.0.1] — 2026-05-18

### Added
- Screenshot `06_Action_on_Game.png` added to README
- Buy Me a Coffee link added to `extension.yaml` and README

---

## [v1.0.0] — 2026-05-18

### Added
- Initial release
- Enable/Disable GameTask per game via right-click menu
- Automatic executable detection from Playnite game actions
- Manual executable path override via "Fix Executable Path"
- Windows Scheduled Task registration with elevated rights (`RunLevel Highest`)
- Hidden PowerShell scripts invoked via `.vbs` wrappers (no console flash, single UAC prompt)
- Pending task queue (`PendingTasks.txt`) processed on demand
- "Play Without UAC" game action added automatically
- Playtime tracking via Playnite's built-in process detection
- Notifications for pending tasks and executable fix requests
- "Rebuild Selected" and "Repair Selected" recovery options
- Task priority set to Normal (4) overriding the Task Scheduler default (7)
- Logging to `Logs/GameTask.log` and `Logs/PS1.log`
