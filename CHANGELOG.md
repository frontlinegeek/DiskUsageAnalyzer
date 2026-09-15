# Changelog

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
