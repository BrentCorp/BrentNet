# BrentNET: public-source recovery snapshot

**Source license:** [AGPL-3.0-only](LICENSE) (copyright © 2026 BrentCorp Holdings Inc.). BrentNET is experimental software for namespace routing and future distributed, user-controlled networking. All source in this recovered package is public-facing for community inspection and testing. Historical GitHub `v0.2.2`–`v0.2.11` tags refer to an earlier placeholder commit; preserve them and publish this as a new source-inclusive revision.

**Publication status:** This ZIP is a reviewed candidate; **GitHub has not been updated by this preparation step**. An independent secrets scan and authoritative development-tree comparison are still required before public commit. See [privacy audit](docs/PRIVACY_AND_SECRET_AUDIT.md) and [publication checklist](docs/PUBLICATION_CHECKLIST.md).

## What is included

- `helper/`: Windows .NET 8 / Windows Forms BrentNET Client v0.2.11 source, route manager and manifest.
- `extension/`: Chromium Manifest V3 resolver extension v0.1.1 source.
- `installer/`: Inno Setup Windows installer source, adjusted to use repository-relative paths (change documented in privacy audit).
- `linux-client/`: experimental Python 3 Linux `0.2.11-linux-preview.1` source, 8 automated tests, route manifest and usage notes.
- `release/`: published 0.2.11 update manifest, included for provenance, not evidence of a reproducible installer.
- `docs/`: source provenance, publishing gates, license choice, privacy audit, and checksums.

## Linux preview quick start

```sh
cd linux-client
python3 -B -m unittest -v test_brentnet_linux.py
python3 -B brentnet_linux.py status
python3 -B brentnet_linux.py serve --port 8765 --no-sync
```

Loopback HTTP only: `127.0.0.1:8765`. This is **not** a peer-to-peer VPN, DNS replacement, or a working replacement for Tailscale. It does not edit `/etc/hosts` or manipulate Jellyfin, Mullvad, network interfaces, or firewall rules.

## Windows build

On Windows with .NET 8 SDK/Desktop, from repository root:

```powershell
dotnet publish .\helper\BrentNETHelper.csproj -c Release -r win-x64 --self-contained true
```

Installer compilation requires Inno Setup and a separately installed `ISCC.exe`, and uses `installer/BNH.iss`, whose inputs are relative to the installer script. Clean installer compilation and binary provenance are **not yet verified**.

## Current security boundaries

The Windows legacy client binds a host-wide HTTP port and loads remote update/route manifests. The Linux preview binds loopback only. Both need further security engineering before either should be advertised as trustworthy private networking infrastructure. See [SECURITY.md](SECURITY.md) and [privacy audit](docs/PRIVACY_AND_SECRET_AUDIT.md).

## Contribute and audit

Review the complete source, run the documented tests, report vulnerabilities through the established maintainer contact, and disclose modified network-service source as applicable under AGPLv3 §13. Build artifacts should carry release hashes, source commit IDs and verified instructions.