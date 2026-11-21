# BrentNET source-publication checklist

- [x] Recover available Windows, Linux preview, installer and extension source.
- [x] Select AGPL-3.0-only and include complete license.
- [x] Static token/key/credential scanning of the recovered publication candidate.
- [x] Normalize three Windows installer paths.
- [x] Eight Linux preview unit tests passed on Ari-Core (observed terminal output).
- [ ] Full independent secrets scan of all Git history and authoritative Windows source tree.
- [ ] Rebuild Windows application and installer from clean checkout; verify released artifact provenance.
- [ ] Test live Linux route syncing, add CI and automated secret scanner.
- [ ] Publish separate experimental Linux release asset, checksum, and hub download link.

No Jellyfin/Tailscale replacement or secure peer networking is implemented by this release.
