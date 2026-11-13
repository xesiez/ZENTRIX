# ZENTRIX Windows Companion — Delivery Plan

**Branch:** `feat/windows-usb-bridge`  
**Baseline:** `611617a526325874774e2cbeba5383f1259c9eae`  
**Status:** Architecture approved; implementation and device validation pending.

## Delivery rules

- Keep all work on `feat/windows-usb-bridge`; do not merge into `main` or another branch without explicit approval.
- The USB remains attached to Windows. The Xbox app-private storage must remain usable independently.
- The Windows host runs as the signed-in user, never elevated by default. It exposes only roots explicitly approved in its UI.
- HTTPS certificate validation is mandatory. No trust-all callback, ignored certificate errors, or production HTTP fallback.
- Read-only connectivity and transfer must pass before enabling mutations.
- The Xbox console is a physical-device test gate. GitHub CI cannot prove Xbox certificate trust or controller behavior.

## Work packages and exit gates

### WP0 — Compatibility spike (blocking)
1. Confirm the Xbox UWP package can reach a Windows host over the private LAN with `privateNetworkClientServer`.
2. Select a certificate issuance/trust model that is practical for a home LAN and accepted by the actual Xbox/UWP networking stack.
3. Demonstrate a successful HTTPS health request from the console.
4. Demonstrate rejection of an untrusted certificate, wrong hostname, expired certificate, and changed certificate.
5. Record console OS/build, host OS, certificate chain, SAN, TLS version, and exact results.

**Exit:** all positive and negative certificate tests pass on the actual console. If not, pause production integration and document alternatives for security review.

### WP1 — Windows Companion foundation
- Create a separate Windows desktop host project targeting supported Windows 10/11 desktop systems.
- UI: enumerate removable and fixed volumes; let the user select individual roots/folders; show approved roots and allow revocation.
- Start/stop local LAN service with a clear status indicator and endpoint details.
- Bind only to the private LAN interface(s); provide a narrowly scoped Windows Firewall rule and explain it before enabling.
- Persist only approved root configuration and protected pairing state.
- Do not enumerate or publish unapproved paths.

**Exit:** a user can approve and revoke a USB root; service restart preserves approved roots; no elevated process is required.

### WP2 — Pairing and authentication
- Display a cryptographically random, short-lived, one-use pairing code on Windows.
- Xbox submits the code over validated HTTPS; rate-limit attempts and expire the code quickly.
- Issue a high-entropy device token; store it using platform-protected storage on both ends.
- Require authentication for every endpoint except minimal liveness and initial pairing.
- Support disconnect/revoke per Xbox client and rotate/revoke tokens.
- Never log pairing codes, tokens, file contents, or sensitive full paths.

**Exit:** pairing, expiry, replay rejection, rate limiting, authenticated access, and revocation tests pass.

### WP3 — Read-only Remote Explorer
- Return approved roots using opaque IDs, not drive-letter authority.
- Provide bounded/paginated directory listings and metadata (name, type, size, modified time).
- Stream downloads with bounded buffers and cancellation; do not buffer whole files.
- Handle Unicode names, long paths, locked files, access denied, disconnected volumes, and stale cursors.
- Keep remote entries as remote models; never fabricate local `StorageFile` or filesystem paths.

**Exit:** list and download work for small and multi-GB test files; memory remains bounded; USB unplug/replug is recoverable.

### WP4 — Safe file mutations
Enable only after WP3 and security gates pass:
- Create directory, upload (streamed to a temporary file then atomic replace), rename, copy, move, delete.
- Confirm overwrite/delete in Xbox UI.
- Validate every path relative to an approved root; reject traversal, rooted paths, alternate separators, device names, and symlink/reparse-point escapes.
- Define conflict behavior, cancellation, partial transfer cleanup, and disk-full recovery.
- Keep an audit record for destructive actions without recording file contents or credentials.

**Exit:** authorization, traversal, race-condition, overwrite, cancellation, disk-full, and operation-recovery tests pass.

### WP5 — Xbox integration
- Add a remote storage provider separate from the app-local storage provider.
- Add Windows PC as a distinct location and expose connection/pairing status.
- Add retry/backoff and explicit reconnect; offline bridge must not block local browsing.
- Map host error codes to useful Xbox UI messages.
- Preserve controller-first focus and navigation.

**Exit:** all supported browse and operation flows work with controller input; host/USB/network loss never crashes or traps navigation.

### WP6 — Packaging, release and documentation
- Produce a signed Windows installer/package and Xbox APPX package.
- Document first-run setup, firewall scope, pairing, certificate trust, revocation, troubleshooting, and uninstall.
- Verify clean install/upgrade/uninstall and ensure secrets/configuration are handled safely.
- Publish checksums and a test report for each candidate build.

**Exit:** reproducible build, install/upgrade tests, security review, and physical Xbox acceptance report.

## Required automated test matrix

| Area | Cases |
|---|---|
| Root isolation | unapproved drive, revoked root, path traversal, rooted path, symlink/reparse escape |
| Pairing | valid code, expired code, replay, brute-force/rate limit, token revoke |
| TLS | trusted chain, untrusted CA, wrong SAN, expired cert, changed cert |
| Transfers | zero-byte, Unicode, large file, cancellation, disconnect mid-transfer, disk full |
| USB lifecycle | unplug while listing, unplug while downloading, reinsert same drive, different drive letter |
| Network lifecycle | host stop/start, Wi-Fi loss, IP change, Xbox sleep/resume |
| Mutations | create, rename, copy, move, delete, overwrite confirmation, concurrent conflict |
| UX | controller focus, back navigation, error recovery, local storage while bridge offline |

## Current implementation snapshot (2026-11-13)

- Branch: `feat/windows-usb-bridge`; all changes remain isolated from `main`.
- Xbox manifest declares `privateNetworkClientServer`; it does not declare `removableStorage`.
- Windows Companion source exists under `WindowsCompanion/` (.NET 8 WinForms + Kestrel).
- Companion UI supports approving individual folders and ready removable USB drives, revoking roots, starting/stopping the service, and displaying the one-use pairing code.
- Companion API source includes HTTPS pairing, five-minute code expiry, one-use pairing, failed-attempt lockout, bearer authentication, approved-root listing, remote directory listing, streamed download/upload, create-directory, copy, move, rename, and delete endpoints.
- A public-certificate bootstrap endpoint is exposed on HTTP port 47830 only; middleware rejects all other plaintext HTTP routes. Authenticated file APIs use HTTPS on port 47831.
- Xbox UWP source now contains a strict-default-validation HTTPS client, Windows PC pairing flow, remote root/folder browsing, remote file download/open, local-to-Windows upload, and remote create/rename/copy/move/delete menu actions.
- A Windows Companion CI run succeeded at commit `e8404b66c170f57ffbc82d8da84072b3d43b18e2` before the later certificate-bootstrap and USB-drive-picker additions. Newer CI runs are pending after the latest code changes.
- An earlier Xbox UWP build compiled and packaged the app at commit `1c4811efc97e7dcf64874b047d8b8d0dd6246538`; the package-collection step then required a fix because MSBuild emitted `.msix`, not `.appx`. The corrected workflow is being re-run.
- No physical Xbox certificate-trust, real USB disconnect/reconnect, or end-to-end file-operation test has been completed.

## Remaining release gates

1. Obtain green CI for the latest Windows Companion and Xbox package commits.
2. Install the Windows Companion and Xbox package on the actual Windows PC and Xbox Series X.
3. Verify the public certificate fingerprint out-of-band; let ZENTRIX add it to its app-specific trusted-root store, then verify UWP HTTPS succeeds without bypassing validation.
4. Test wrong-hostname/untrusted/expired certificate rejection.
5. Test large-file transfer, USB unplug/replug, Wi-Fi loss, host restart, and all destructive operations.
6. Fix any device-specific failures and publish a signed test bundle plus acceptance report.

The current source is an integration candidate, not yet a release-certified build.
