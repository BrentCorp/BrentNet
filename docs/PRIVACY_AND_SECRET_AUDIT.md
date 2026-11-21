# Source privacy and secret audit (2026-11-21)

This bounded static review covers only recovered source files. Scans for common key headers, token prefixes, credential assignments and private network addresses found no obvious secrets. The application's public bug-report contact is brent@brentnet.net. Three developer-specific absolute Windows installer paths were replaced with repository-relative build paths. Windows build artifacts, local NuGet caches and development metadata are excluded.

**Outstanding:** Independently scan entire Git history and authoritative Windows development tree; verify clean Windows build, binary-source correspondence, and Linux manifest synchronization. Static pattern checks cannot guarantee absence of all secrets.

Never commit API tokens, credentials, passwords, private keys, real user data, production configurations, or workstation-specific diagnostics.
