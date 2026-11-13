# ZENTRIX

A dedicated Xbox Series X file manager. ZENTRIX is focused exclusively on browsing and managing files and folders.

## Product scope
- Single-purpose file manager interface; no Home dashboard, music player, phone-link, media library or device synchronization sections.
- Controller-first navigation for Xbox.
- Browse app-local storage and accessible removable storage.
- Open files and navigate folders.
- File operations will be delivered in focused stages: create folders, copy, move, rename, delete, multi-select, search, sort and file details, subject to UWP/Xbox storage API and capability restrictions.
- Storage errors are isolated so removable-device failures do not take down app-local browsing.
- Async lifecycle guards prevent stale requests from updating the active view.

## Engineering baseline
- Native UWP, XAML and C# 7.3; x64 target.
- Semantic design resources under Styles/.
- GitHub Actions Windows x64 Release build on main and prep/storage-file-architecture.

## Build
Open ZENTRIX.sln in Visual Studio 2022 with the Universal Windows Platform development workload and Windows 10 SDK 10.0.19041.0 (or compatible newer SDK). Select x64 and build the ZENTRIX project.

Package signing and Store submission are not configured in this baseline.
