# Largest Files View

## Requirements

- A Largest files toggle is available before and after scanning, and remains active across scans and refreshes.
- Before a scan, the selected path defines the scope once results arrive.
- After a scan, selecting a folder in either panel sets the scope. Selecting a file does not change it.
- The view lists only files directly in that folder or its descendants. Parent and sibling content must never appear.
- An unscanned or empty folder produces an empty list, with no fallback to the scan root.
- Files default to descending logical size; Smallest first reverses the order. Equal sizes sort by full path.
- Search, extension, and minimum-size filters continue to apply. Full paths remain available in row tooltips and Copy path.
- Enabling the view expands the left panel through every ancestor to the selected folder, selects it, and brings it into view. Subsequent folder changes reveal the new scope.
- Turning the toggle off restores the right-hand hierarchy's expanded folders, selected item, and vertical/horizontal scroll position from when Largest files was enabled. Selection and navigation in Largest files must not overwrite that saved tree state. Display changes require no rescan.
- Restoration uses the current scan and filters; removed or filtered-out items cannot be restored, and scroll offsets are limited by the remaining content.
- Folder expansion is asynchronous and only loads the path's branches. File filtering and ordering run in the background.
- CSV export continues to export the complete scan.

## Verification

- Automated tests cover before/after-scan toggles, subtree isolation, sorting, filters, empty/unscanned scopes, and ancestor expansion.
- The opt-in WPF smoke workflow renders the largest-files view at desktop and narrow window sizes.
