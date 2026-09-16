# Changelog

## 1.0.3 - 2026-09-15

### Added

- Light, Dark, and System interface themes with an in-app theme selector.
- Live System-theme updates when the Windows app-color preference changes.
- Portable theme preference persistence in `settings.json` beside the executable.
- Theme-aware window chrome, controls, result bars, status text, and error indicators.
- WPF smoke coverage for switching to Dark theme and rendering the themed interface.

### Fixed

- The collapsed theme selector now maintains readable text contrast in Dark mode.
- WPF scroll-position smoke assertions now allow subpixel layout rounding.

## 1.0.2 - 2026-09-15

### Added

- A self-contained Windows x64 publish profile and Inno Setup installer build workflow.
- Per-user installation with Start menu and optional desktop shortcuts, without requiring administrator rights or a separately installed .NET runtime.
- Application identity metadata and installer build instructions.

### Fixed

- Elevated relaunches from the `dotnet` development host now resolve the entry assembly from the application base directory.

## 1.0.1 - 2026-09-15

### Added

- Draggable dividers for manually resizing the disk-usage result columns.
- Double-click autosizing that measures only the column immediately to the left of the selected divider.
- Shared header and row sizing so result values remain aligned while columns are resized.
