# Changelog

## 1.1.0 - 2026-09-16

### Added

- Persistent multi-root scan caching backed by SQLite, including complete entry hierarchies, logical and allocated sizes, timestamps, attributes, errors, NTFS file IDs, volume identity, and USN checkpoints.
- Immediate loading of previously completed scans with cached-result timestamps, cache deletion, and manual full-rescan controls.
- NTFS USN Change Journal refresh for creates, deletes, moves, renames, and size changes, with safe full-scan fallback when journal or volume validation fails.
- Transactional cache replacement and incremental row updates with indexes for large scans and rollback protection for cancellation or interruption.
- Right-click rescanning for folders and drives, including subtree replacement, aggregate-size propagation, and cache synchronization.
- Windows system icons and fully themed context menus for Open, Copy, Rescan, and Delete actions.
- A cancellable modal loading indicator with live counters and path reporting while large cached scans load on a worker thread.

### Changed

- Filesystem metadata now retains allocated sizes where available, file attributes, and NTFS file identifiers.
- Cache loading and immutable result projection run away from the WPF dispatcher to keep the interface responsive.

### Fixed

- Invalid, unavailable, replaced, or wrapped USN journals now trigger a safe full scan instead of risking stale cached totals.
- Directory aggregate sizes remain accurate after incremental rename, move, delete, and size-change reconciliation.
- Dark-mode context menus no longer show the legacy WPF icon-gutter outline artifact.

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
