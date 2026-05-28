# Changelog

All notable changes to GameTask are documented here.  
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) — versioning follows [Semantic Versioning](https://semver.org/).

---

## [v1.5.0] — 2026-05-26

### Added
- Per-row action buttons in Diagnostics — Repair, Fix Exe and Disable for each game individually
- Issue hint column in Diagnostics — explains what is wrong and how to fix it for each affected game
- Detection of games without a GameAction configured — shown with a specific message instead of generic "Unknown"

### Fixed
- Playnite crash/serious error on startup — `ScanLibrary` and task checks now run in a background thread instead of blocking the UI thread; notifications are dispatched back to the UI thread safely

---

## [v1.4.0] — 2026-05-26

### Added
- Diagnostics page — accessible via Extensions → GameTask → Diagnostics; shows all tagged games with task status, detected executable, FocusGuard status and custom path; games with issues highlighted in red with a legend in the footer
- Focus guard duration setting — slider from 5s to 120s (default 20s) in Settings → Plugins → GameTask; increase for games that take long to initialize
- "Fix All Unknown Executables" in main menu — opens file selection dialog sequentially for each affected game; count shown in menu label
- "Open Data Folder" added to Extensions → GameTask main menu 
   
### Fixed
- PowerShell syntax error in `CreateTasks.ps1` prevented scheduled tasks from ever being created automatically — this bug was present since v1.0.0; users had to create tasks manually in Task Scheduler as a workaround (reported by @bobokaka)
- "Enable GameTask" and "Rebuild Selected" not showing pending task notification — `AddPendingTask` was silently skipping games with custom paths or launcher-type actions
- "Create Pending Tasks" now automatically rescans for missing scheduled tasks when the pending queue is empty — handles cases where tasks were manually deleted from Task Scheduler (reported by @bobokaka)
- File selection dialog now rejects shortcuts (`.lnk`) with a clear warning message

---

## [v1.3.0] — 2026-05-24

### Added
- Notification on startup for games with no detected executable — FocusGuard cannot work for these games without a known process name
- "Fix All Unknown Executables" in main menu — opens file selection dialog sequentially for each affected game, with Yes/No/Cancel per game; count shown in menu label
- Child process support in `FocusGuard.exe` — monitors child processes via WMI (read-only, anti-cheat safe) to handle games that spawn a launcher or wrapper before the actual game window appears
- "Open Data Folder" added to Extensions → GameTask main menu for quick access to logs
- `FocusGuard.log` written to plugin data `Logs` folder alongside `GameTask.log`
- Launch cooldown (3 seconds) prevents double-triggering "Create Pending Tasks"
- Low Performance Mode — increases FocusGuard process/window timeouts and early push count for PCs with limited resources or many open applications; configurable via Settings → Plugins → GameTask

### Fixed
- File selection dialog now rejects shortcuts (`.lnk`) with a clear warning, preventing accidental selection instead of the actual `.exe`

### Removed
- `FocusGuard.cs` — obsolete since focus management moved to standalone `GameTask.FocusGuard.exe`

---

## [v1.2.2] — 2026-05-22

### Fixed
- Game window losing focus to Playnite fullscreen when splash screen closes — replaced in-process focus logic with a standalone `GameTask.FocusGuard.exe` launched directly by the `.vbs` launcher immediately after the scheduled task fires, before Playnite has a chance to reclaim the foreground (reported by @bobokaka)
- `FocusGuard.exe` receiving the Playnite action name instead of the real game executable — now correctly uses custom path or resolved exe
- File selection dialog now rejects shortcuts (`.lnk`) with a clear warning message, preventing the user from accidentally selecting a shortcut instead of the actual `.exe`
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
- VBScript syntax error `800A0401` when creating scheduled tasks — fixed quote escaping in `.vbs` launcher generation using `Chr(34)` instead of escaped double quotes

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
