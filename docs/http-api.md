# Local HTTP API

RigShift can listen for HTTP calls from scripts, SimHub, Stream Deck plugins and similar tools on the same PC.

## Turn it on

**Settings → Local HTTP API.** Switching it on creates a random token and shows the port (default `47800`), the
token and buttons to copy the token or a ready-made `curl` example. **New token** invalidates the old one at once.

The API only listens on `127.0.0.1`, so other devices on your network cannot reach it. Every call needs the token:

```
Authorization: Bearer <token>
```

Without it, any web page open in your browser could switch your displays.

## Endpoints

| Method | Path | Does |
|---|---|---|
| `GET` | `/api/status` | Active profile and active displays |
| `GET` | `/api/profiles` | All profiles, the active one marked |
| `POST` | `/api/profiles/{name}/apply` | Switch to a profile (name is not case-sensitive; the profile id works too) |

`apply` accepts `?dryRun=true` (check against the connected displays, change nothing) and `?noConfirm=true` (keep
the new arrangement without the countdown). It answers **once the switch has finished**, so with the confirmation
countdown on it can take as long as the countdown. Set your tool's timeout accordingly.

### Examples

PowerShell:

```powershell
$headers = @{ Authorization = 'Bearer <token>' }
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:47800/api/profiles/Rig/apply' -Headers $headers
```

curl (included in Windows):

```bat
curl.exe -X POST -H "Authorization: Bearer <token>" http://127.0.0.1:47800/api/profiles/Sim%20Rig/apply?noConfirm=true
```

### Responses

`GET /api/profiles`

```json
[{ "id": "c90fa714-…", "name": "Rig", "isActive": true }]
```

`GET /api/status`

```json
{
  "activeProfile": { "id": "c90fa714-…", "name": "Rig", "isActive": true },
  "displays": [{ "name": "Main · G9", "width": 5120, "height": 1440, "refreshHz": 240, "x": 0, "y": 0, "isPrimary": true }]
}
```

`POST /api/profiles/Rig/apply`

```json
{
  "profile": "Rig",
  "outcome": "Applied",
  "exitCode": 0,
  "attempts": 1,
  "durationSeconds": 2.3,
  "audio": "Applied",
  "apps": "NotConfigured",
  "missing": [],
  "warnings": [],
  "message": null
}
```

`outcome` is one of `Applied`, `AppliedPartially`, `RolledBack`, `Blocked`, `Failed`, `DryRun`; `exitCode` matches
the [command line](../README.md) exit codes. A finished switch always answers `200`, even if it was rolled back:
the request itself worked.

Errors come as `{ "error": "…" }`:

| Status | Meaning |
|---|---|
| `401` | Token missing or wrong |
| `403` | Request not addressed to `127.0.0.1` or `localhost` |
| `404` | Unknown endpoint or profile |
| `405` | Wrong method, e.g. `GET` on `apply` |
| `409` | Another switch is running |
| `503` | Display configuration unavailable (e.g. a remote session) |
