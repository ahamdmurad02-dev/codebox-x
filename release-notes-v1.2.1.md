## CodeBox X v1.2.1 — Secure Updater Build

This Windows x64 release adds the native **Update CodeBox X** workflow while retaining the existing CodeBox X editor, terminal, Python Live Preview, Gemini AI, MPM, Marketplace, themes, linters, and Python/C# Run Active File features.

### Secure update workflow

- **Update CodeBox X** is available from Help, Settings, and About.
- Checks only the official `ahamdmurad02-dev/codebox-x` GitHub Releases endpoint.
- Compares installed and released versions and displays release notes.
- Downloads the official installer with visible progress and cancellation.
- Restricts download redirects to trusted GitHub hosts.
- Verifies the GitHub-provided SHA-256 asset digest and validates the Windows executable header before installer launch.
- Handles unavailable network, GitHub/API, download, integrity, permission, cancellation, and installer-launch errors safely.

### Windows downloads

| File | Purpose | SHA-256 |
|---|---|---|
| `CodeBoxX-Setup-win-x64.exe` | Self-contained Windows 10/11 x64 installer | `358b0335ee52496ee367f3e11515893756986d950dae305a2ff58d91f0297036` |
| `CodeBoxX-win-x64.zip` | Portable Windows x64 build | `e92ea53e184d11267af98a78030815207bb56924281facafba3cc335fce4cadb` |

The installer is self-contained. The portable ZIP requires the .NET Desktop Runtime 8 x64. Microsoft Edge WebView2 Runtime is needed only for document Live Preview.

See [README.md](https://github.com/ahamdmurad02-dev/codebox-x/blob/main/README.md) and [CHANGELOG.md](https://github.com/ahamdmurad02-dev/codebox-x/blob/main/CHANGELOG.md) for installation, update, security, and build details.
