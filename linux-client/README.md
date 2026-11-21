# BrentNET Client for Linux: stage-1 preview

Version: `0.2.11-linux-preview.1` (new Linux prototype, **not** a published v0.2.11 binary).

## Basis and scope

The cloned BrentCorp/BrentNet default branch contains placeholders rather than the Windows application's source. This preview is grounded in the project's uploaded v0.2.11 `Program(2).cs`, `HostsManager.cs`, and `routes.json` source. It implements the `.brent` HTTP redirect functionality with read-only route lookup, validated HTTPS manifest syncing, a last-known-good cache, a local health check, and six-hour refreshes while running.

It uses Python 3 standard library only, listens on **127.0.0.1:8765**, and does **not** edit `/etc/hosts`, bind port 80, install a VPN, create tunnels, expose Jellyfin, alter Mullvad/Tailscale, or start automatically. The Python runtime and tests must be verified on Ari-Core separately.

## Test and run

```bash
cd /srv/ari/projects/brentnet/linux-client
python3 -B -m unittest -v test_brentnet_linux.py
python3 -B brentnet_linux.py status
python3 -B brentnet_linux.py sync
python3 -B brentnet_linux.py serve --port 8765
```

In another terminal:

```bash
curl -sSI -H 'Host: net.brent' http://127.0.0.1:8765/
curl -fsS http://127.0.0.1:8765/_brentnet/health
```

To stop, press Ctrl+C in the server terminal. No changes are needed to the current Tailscale/Jellyfin configuration.

## Network security notes

- This preview only performs HTTP redirects to ordinary websites, just like the Windows alpha. **No encrypted private peer connectivity is present.**
- Even if a future hosts-file mapping points `net.brent` at 127.0.0.1, browsers normally use HTTP port 80 or HTTPS 443. This preview's port 8765 is for isolated testing only.
- External route manifests are validated for redirect-only entries, hostname format, supported URL schemes, and maximum payload size. TLS trust still relies on platform trust roots; cryptographically signed manifests and update trust are future work.
- The currently installed Tailscale userspace daemon can continue to provide private Jellyfin access while the BrentNET transport layer is designed and independently tested.

## Next acceptance milestones

1. Run Linux unit tests and inspect health and redirect responses on Ari-Core.
2. Introduce a separate, explicitly approved local DNS/hosts integration and privileged port capability, keeping services isolated.
3. Define service-scoped peer identity, encrypted transport, NAT traversal, relays, revocation and audit controls before Jellyfin routing. Verify remote playback and deny access to all unrelated local ports before replacing Tailscale.