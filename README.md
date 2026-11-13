# ZENTRIX

A dedicated Xbox Series X file manager with an optional Windows Companion for approved USB storage over a private local network.

## Product scope
- Controller-first Xbox UWP file manager.
- Browse app-private storage independently of the Windows host.
- Browse approved Windows folders and USB volumes through the HTTPS Windows Companion.
- Open remote files after streaming them into app-local storage.
- Windows Companion API includes streamed upload/download and file-operation endpoints; Xbox UI integration is being completed in stages.
- No Home dashboard, music player, phone-link, media library or cloud synchronization.

## Architecture
- Xbox client: native UWP, XAML, C# 7.3, x64.
- Windows Companion: .NET 8 WinForms + ASP.NET Core/Kestrel, x64.
- USB remains attached to Windows. The user explicitly approves each shared folder.
- LAN-only; no cloud relay and no router port forwarding.
- HTTPS with normal platform certificate validation. Development certificate bootstrap is documented in WindowsCompanion/README.md.

## Build and test
- Xbox package workflow: .github/workflows/build.yml.
- Windows Companion workflow: .github/workflows/companion.yml.
- Windows Companion setup and certificate trust: WindowsCompanion/README.md.
- Architecture and acceptance gates: docs/architecture/windows-companion-delivery-plan.md.

## Current limitations
- The Companion is a development build. Its HTTPS certificate trust flow must be validated on the actual Xbox.
- Remote browsing, streamed download/upload, and Xbox-side create/rename/copy/move/delete actions are implemented in source; end-to-end device verification remains acceptance work.
- Do not treat successful CI compilation as proof of Xbox runtime compatibility.
