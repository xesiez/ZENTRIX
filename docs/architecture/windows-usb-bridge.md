# ZENTRIX Windows USB Bridge — Architecture Decision

**Status:** Approved direction; implementation not yet started  
**Target:** Xbox Series X UWP client + Windows companion host  
**Branch:** `feat/windows-usb-bridge`

## Decision

USB storage is connected to a Windows PC. ZENTRIX on Xbox accesses it over the user's private LAN through a separately installed Windows companion application.

The Xbox app must not depend on `KnownFolders.RemovableDevices` for the primary USB workflow. The existing app-private storage remains independent and available if the bridge is offline.

## Components

1. **ZENTRIX Xbox client**
   - Adds a distinct `Windows PC` storage location.
   - Connects to a manually entered/discovered host endpoint.
   - Presents remote folders/files as normal explorer entries.
   - Calls the bridge API for listing, metadata, read, write, create-directory, rename, copy, move, and delete.
   - Keeps remote entries distinct from `StorageFolder`/`StorageFile` objects; no fake local paths.

2. **ZENTRIX Windows companion**
   - A Windows desktop application/host, initially a user-session process rather than a privileged Windows service.
   - Enumerates only volumes and folders explicitly approved by the user.
   - Exposes an allowlisted root set; never exposes the whole PC by default.
   - Uses streaming for file transfers and bounded pagination for directory listings.
   - Reports drive removal, access denied, locked files, disk-full, and I/O errors as structured errors.

## Transport and security requirements

- LAN-only; do not expose a router port or bind a public endpoint.
- HTTPS is the release target. Do not silently disable certificate validation.
- Pairing is explicit and time-limited: a one-time code displayed by the Windows host is entered on Xbox. Pairing issues a revocable device token; the one-time code is never reused as the ongoing credential.
- Tokens are stored in Xbox app-local protected storage and Windows host protected storage. Provide revoke/disconnect controls.
- Every operation except the initial pairing handshake requires authentication.
- Restrict host binding/firewall rules to the private network profile and the minimum required application/port.
- Do not accept arbitrary absolute paths from the Xbox. Resolve all paths relative to an approved root, normalize them, reject traversal, and verify the resolved target remains under that root.
- Validate file names, enforce request/body limits, stream large files, and use atomic temporary-file replacement for uploads where practical.
- Keep an audit trail for destructive operations and ask for confirmation in the Xbox UI before delete/overwrite.
- No cloud relay, telemetry upload, or internet exposure is part of this design.

### Certificate feasibility gate

UWP's `HttpBaseProtocolFilter.ServerCustomValidationRequested` runs only after normal OS certificate validation succeeds. A self-signed certificate is therefore not a safe shortcut: do not set broad ignored certificate errors or accept any certificate.

Before implementing the production connector, prove on a real Xbox:
1. The chosen host certificate chain can be trusted by the console/app environment.
2. The UWP client can perform a successful HTTPS request to the companion.
3. A changed/untrusted certificate is rejected.

If a practical trust-provisioning path cannot be established, stop and evaluate a separately reviewed application-layer authenticated-encryption design; do not silently downgrade production traffic to plain HTTP.

## API v1 shape

Base path: `/api/v1`

- `GET /health` — unauthenticated liveness only; returns no sensitive host details.
- `POST /pair` — one-time code exchange; rate-limited and expires quickly.
- `GET /volumes` — approved roots only.
- `GET /entries?rootId=...&path=...&cursor=...` — paginated folders/files.
- `GET /entries/{id}/content` — streamed download.
- `PUT /entries/content?rootId=...&path=...` — streamed upload.
- `POST /directories` — create directory.
- `POST /operations/copy`, `POST /operations/move`, `POST /operations/rename`.
- `DELETE /entries/{id}` — destructive operation; authenticated and confirmed in UI.

Use opaque root IDs and relative paths. Do not expose raw drive letters as authority-bearing identifiers. Return stable error codes such as `access_denied`, `not_found`, `volume_offline`, `conflict`, `disk_full`, and `invalid_path`.

## UWP manifest

The Xbox manifest now declares `privateNetworkClientServer` and no longer declares `removableStorage` or a removable-media file-type association. This matches the approved Windows-hosted USB architecture: the Xbox app is a LAN client and does not need to listen for inbound connections. Verify the capability on the actual Xbox Developer Mode console before implementation proceeds. Do not add `internetClientServer` for this scenario.

## Implementation sequence

1. **Compatibility spike:** prove Xbox-to-Windows LAN connectivity and HTTPS certificate trust on the actual console.
2. **Windows host skeleton:** approved-root picker, health endpoint, pairing, authentication, and volume listing.
3. **Read-only explorer:** list folders/files and download a file; test Unicode names, large files, unplug/replug, and access failures.
4. **Safe mutations:** create folder, upload, rename, copy, move, delete, overwrite confirmation, and atomicity/error tests.
5. **Xbox integration:** add a remote-storage provider abstraction; keep local storage and remote storage implementations separate.
6. **Packaging and documentation:** signed Windows installer/package, first-run firewall explanation, pairing instructions, and recovery/revocation steps.

## Acceptance criteria

- USB stays attached to Windows; Xbox can browse its approved root over LAN.
- No dependency on UWP removable-storage file associations for remote USB files.
- A USB containing arbitrary extensions can be listed and transferred without adding those extensions to the Xbox manifest.
- Unplugging USB or stopping the Windows host never crashes or blocks app-local storage.
- Traversal attempts, unauthenticated mutations, invalid certificates, and revoked tokens are rejected.
- Large file transfer is streamed and does not load the whole file into memory.
- All destructive operations have explicit UI confirmation and produce structured errors.

## References

- Microsoft: [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations) — `privateNetworkClientServer` provides home/work LAN access.
- Microsoft: [UWP socket networking](https://learn.microsoft.com/en-us/windows/apps/develop/networking/sockets) — local networking capability requirements.
- Microsoft: [HttpBaseProtocolFilter.ServerCustomValidationRequested](https://learn.microsoft.com/en-us/uwp/api/windows.web.http.filters.httpbaseprotocolfilter.servercustomvalidationrequested) — custom validation occurs after default OS certificate validation.
- Microsoft: [Host ASP.NET Core in a Windows Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-8.0) — Windows hosting option; a service is not required for the initial user-session prototype.
- Microsoft: [Windows Firewall risks](https://support.microsoft.com/en-us/windows/security/firewall/risks-of-allowing-apps-through-windows-firewall) — prefer narrowly scoped app rules over broadly opening ports.
