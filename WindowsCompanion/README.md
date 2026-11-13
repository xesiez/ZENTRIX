# ZENTRIX Windows Companion (x64)

## What this build does

- Lets the signed-in Windows user explicitly approve or revoke folders.
- Runs a local companion API on TCP 47831 using HTTPS.
- Exposes only approved roots through opaque IDs.
- Supports one-use pairing codes (5-minute expiry), bearer-token authentication, paginated-ready directory listing, streamed downloads/uploads, directory creation, copy, move, rename, and delete endpoints.
- Streams file bodies with bounded 128 KiB buffers; it does not read an entire file into memory.
- Stores approved-root configuration under the current user's LocalAppData.
- Uses a development certificate generated on first launch. The PFX remains in LocalAppData; only the public CER is exposed.

## First-run setup

1. Extract and run `ZENTRIX.Companion.exe` on Windows 10/11 x64.
2. Approve only the USB folders you want to share.
3. Click **Start secure service** and allow the app through Windows Defender Firewall only on the **Private** network profile. Do not create a Public-profile or router port-forwarding rule.
4. On Xbox Edge, open `http://<Windows-IP>:47830/bootstrap/certificate` and download the public certificate.
5. Before trusting it, compare the certificate's SHA-256 fingerprint with the fingerprint shown in the Windows Companion window. Install it into the Xbox current-user **Trusted Root Certification Authorities** store using the certificate UI.
6. In ZENTRIX, choose **Windows PC**, enter the Windows IP and the one-use pairing code.
7. The actual UWP HTTPS connection must succeed with normal platform certificate validation. ZENTRIX does not ignore certificate errors.

Port 47830 is an HTTP bootstrap listener for the public certificate only. Middleware returns 404 for every other HTTP path. All API endpoints are available only over HTTPS on port 47831.

## Important security notes

- This is a development/test build, not a production PKI deployment.
- The certificate is self-signed and must be verified out-of-band by comparing its SHA-256 fingerprint before installing it as trusted.
- If the Windows host IP changes, the certificate SAN may no longer match. Regenerate/reissue the certificate and repeat trust installation rather than bypassing hostname validation.
- Pairing tokens are in-memory and are revoked when the companion stops.
- Do not expose either port to the public internet. No cloud relay is used.
- Review approved folders before starting the service. Avoid sharing folders containing credentials or private data.
- The file mutation API is present but should be treated as preview functionality until the security and lifecycle test matrix passes on real USB media.

## API v1

- `GET /api/v1/health` — HTTPS liveness
- `POST /api/v1/pair` — one-use pairing
- `GET /api/v1/roots` — approved roots
- `GET /api/v1/entries?rootId=...&path=...` — directory entries
- `GET /api/v1/content?rootId=...&path=...` — streamed download
- `PUT /api/v1/content?rootId=...&path=...` — streamed upload to a new file
- `POST /api/v1/directories` — create directory
- `POST /api/v1/operations/copy` — copy file/directory
- `POST /api/v1/operations/move` — move file/directory
- `POST /api/v1/operations/rename` — rename entry
- `DELETE /api/v1/entries?rootId=...&path=...` — delete entry

All API routes except health, pairing, and the public certificate bootstrap require a bearer token. All routes other than the certificate bootstrap require HTTPS.
