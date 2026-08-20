# CodeBox X

**CodeBox X v1.2.2** is a native WPF code editor and lightweight development workspace for **Windows 10 and Windows 11**. It is a local-first desktop application, not a web or mobile application. Projects, editor settings, terminal commands, and optional AI credentials remain on the user’s Windows device.

> CodeBox X does not bundle language runtimes, compilers, package credentials, or Gemini API keys. A required tool is detected when **Run Active File** is used and a clear guidance message is shown when it is unavailable.

## Highlights

| Area | Included capability |
|---|---|
| Editor | Tabs, syntax-aware editor presentation, line numbers, search and replace, undo/redo, drag and drop, Save/Save As, autosave, and safe unsaved-change prompts. |
| Workspace | Folder Explorer, create/rename/delete with confirmations, recent projects and files, resizable panels, light/dark themes, and full screen. |
| Terminal | Persistent asynchronous Windows Command Prompt terminal with stdout and stderr output, New Terminal, Clear Terminal, and a workspace-aware working directory. |
| Run Active File | Runtime-aware execution for Python and C#; Run, Stop, and Restart controls; duplicate-process protection; and clear tool-discovery errors. |
| Preview | Desktop HTML/Markdown/JSON/XML preview using WebView2 and a dedicated Python Live Preview panel with Run, Stop, Restart, status, stdout, and stderr. |
| Developer tools | MPM package management for Python, Node.js/TypeScript, and .NET; local extension Marketplace; syntax themes; linters; and Publish Website ZIP packaging. |
| AI Assistant | Optional Gemini 3.1 Flash-Lite Assistant with local Windows DPAPI key protection. API keys are never included in source, logs, terminal output, or release packages. |
| CodeBox X Agent | Native Gemini-powered project Agent with explicit workspace-read permission, redacted project context, search, analysis, reviewable file-change plans, Accept/Reject, modified-files list, and one-step undo. The Agent never auto-applies a change or sends terminal commands without user confirmation. |
| Updates | Native **Update CodeBox X** controls in Help, Settings, and About; official GitHub Release version comparison, release notes, download progress, SHA-256 verification, Windows installer validation, and safe installer handoff. |

## Run Active File

Click **Run Active File** or press `F5` to save and execute the active Python or C# document. Press `Shift+F5` to stop the active process tree and `Ctrl+F5` to restart the latest active run. CodeBox X prevents a second active-run process from starting while an existing one is still running.

| File type | Resolution and command behavior |
|---|---|
| Python: `.py` | Uses `python.exe` or the Windows `py.exe -3` launcher found on `PATH`, then runs the saved file in its directory. |
| C#: `.cs`, `.csproj` | Detects the .NET SDK. A `.csproj` runs with `dotnet run --project`; `Program.cs` uses its containing project when present; another `.cs` file runs from an isolated generated SDK project so the active source is executed. |

All output and errors are streamed to the integrated terminal. If a required compiler or runtime is absent, CodeBox X reports the exact missing dependency instead of starting a fake run or failing silently.

## System Requirements

CodeBox X itself runs on **Windows 10 or Windows 11 x64**. The installer is self-contained. The portable ZIP needs the **.NET Desktop Runtime 8 x64**. Microsoft Edge WebView2 Runtime is required only for modern document Live Preview.

Language execution requires only the tool relevant to the active file: Python for Python and a .NET SDK for C#. Install the required tool and ensure its executable is discoverable on `PATH`.

## Installation

Download the current installer or portable ZIP from the [GitHub Releases page](https://github.com/ahamdmurad02-dev/codebox-x/releases). Run the Windows installer, or extract the portable ZIP and start `CodeBoxX.exe`.

## CodeBox X Agent

Open **Agent** from the Explorer sidebar, toolbar, welcome actions, or the View menu. The Agent can chat about the project without workspace access, but it cannot read project files until the user explicitly chooses **Allow Workspace Read**. When allowed, CodeBox X sends a bounded snapshot of safe text files to Gemini; protected folders, binary files, `.env` files, private-key files, and settings are excluded, while values resembling API keys, tokens, passwords, and secrets are redacted before they leave the device.

The Agent can analyze and search the approved workspace and request a structured file-change plan. It shows the proposed operations and modified files before any write occurs. **Accept Changes** is required to apply a plan, deletion and large project-wide plans require additional confirmation, and **Undo Agent Changes** restores the most recently accepted Agent changes. Agent access to Run Active File and Build Workspace also uses the existing CodeBox X controls only after confirmation; it never installs packages or sends a terminal command automatically.

## Updating CodeBox X

Choose **Update CodeBox X** from **Help**, **Settings**, or the **About** window. The native update dialog checks only the pinned official GitHub Releases API, compares the installed version to the latest release, and displays that release’s notes. Before offering installation, CodeBox X downloads only a trusted GitHub release asset, checks the GitHub-provided SHA-256 digest, and verifies that the result is a Windows executable. If a check, download, validation, or installer handoff fails, the application presents a clear error and does not run an unverified file.

## Build from Source

Open a Windows Terminal or Developer Command Prompt in the repository root:

```powershell
dotnet restore .\CodeBoxX\CodeBoxX.csproj
dotnet build .\CodeBoxX\CodeBoxX.csproj -c Release
dotnet run --project .\CodeBoxX\CodeBoxX.csproj
```

Create a self-contained Windows x64 publish directory with:

```powershell
dotnet publish .\CodeBoxX\CodeBoxX.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64-selfcontained
```

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+N` / `Ctrl+O` / `Ctrl+Shift+O` | New file / Open file / Open folder |
| `Ctrl+S` / `Ctrl+Shift+S` | Save / Save As |
| `Ctrl+W` | Close active tab safely |
| `Ctrl+H` | Find and replace |
| `F5` | Run Active File |
| `Ctrl+F5` | Restart latest active run |
| `Shift+F5` | Stop active run |
| `Ctrl+\`` | Focus Terminal |
| `F11` | Toggle full screen |

## Gemini AI Assistant

The AI Assistant uses Gemini 3.1 Flash-Lite only after the user enters an API key in **AI Settings**. The key is protected locally with Windows DPAPI and is not committed, bundled, printed in the terminal, or written to the application log. The assistant can explain, fix, refactor, generate, comment on, copy, and insert code.

## MPM and Marketplace

MPM detects Python, Node.js/TypeScript, and .NET workspaces and exposes dependency search, info, list, install, uninstall, update, and restore operations. Actions that modify packages require a confirmation. The native local Marketplace supports validated extension packages, themes, linters, formatters, language-support packages, and productivity tools without executing untrusted extension binaries or scripts.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for v1.2.2 release notes.

## License

CodeBox X is distributed under the [MIT License](LICENSE).
