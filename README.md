# ZENTRIX

Native UWP application foundation for Xbox Series X, focused on file management, storage, media and secure device connectivity.

## Engineering baseline
- Native UWP, XAML and C# 7.3; x64 target.
- Files-first startup with Files, Home, Music and Phone Link navigation.
- Shared semantic design resources under Styles/.
- Controller-first navigation with visible focus feedback and a persistent controller hint bar.
- App-local and removable-storage browsing with isolated storage failures.
- Page/request lifecycle guards to prevent stale asynchronous UI updates.
- GitHub Actions build workflow for main and prep/storage-file-architecture.

## Build
Open ZENTRIX.sln in Visual Studio 2022 with the Universal Windows Platform development workload and Windows 10 SDK 10.0.19041.0 (or a compatible newer SDK). Select x64 and build the ZENTRIX project.

The GitHub Actions workflow performs a Windows x64 Release build. Package signing and Store submission are not configured in this initial baseline.

## Storage permissions
The application declares the removableStorage device capability to enumerate removable devices. Store distribution may require capability approval. App-local storage remains independent of removable-device availability.

No 1080p- or 4K-specific display requirements are imposed by this project baseline.
