# CodeBox X

**CodeBox X** is a native, lightweight development workspace for **Windows 10 and Windows 11**. Built with WPF on .NET 8, it brings project browsing, multi-tab code editing, an integrated terminal, live previews, package management, a local extension marketplace, and an optional Gemini AI assistant into one focused desktop application. It is not a web, browser, mobile, or hybrid application.

> CodeBox X keeps project files and editor settings on the local Windows device. It does not require an account or a hosted workspace service.

## Highlights

| Area | Included capabilities |
|---|---|
| Workspace and files | Open folders, browse and refresh files, create files and folders, rename and delete with confirmation, recent projects, and drag-and-drop file opening. |
| Editor | Multi-tab editing, syntax-aware coloring, line numbers, undo/redo, search and replace, Save and Save As, autosave, and unsaved-change protection. |
| Native tools | Persistent `cmd.exe` terminal, Run and Stop controls, output panel, native WebView2 Live Preview, and managed Python Live Preview. |
| Developer workflow | MPM Package Manager for Python, Node.js/TypeScript, and .NET projects; website source packaging; and a local Marketplace for themes, linters, and productivity extensions. |
| AI assistance | Optional Gemini 3.1 Flash-Lite chat, code explanation, error fixing, refactoring, generation, comments, insertion, and copying. |
| Desktop experience | Light and dark themes, resizable panes, full-screen mode, configurable editor font size, keyboard shortcuts, and a Windows application icon. |

## Screenshots

The production repository is structured to accept screenshots under `docs/screenshots/`. A visual gallery will be added with future releases; until then, the release assets and source code are the authoritative representation of the current native Windows interface.

## Download and installation

Download the current release from the [GitHub Releases page](https://github.com/ahamdmurad02-dev/codebox-x/releases/latest).

| Distribution | Intended use | Requirements |
|---|---|---|
| `CodeBoxX-Setup-win-x64.exe` | Recommended installation for Windows 10/11 x64. The installer is self-contained, creates Start menu shortcuts, optionally creates a desktop shortcut, and supports standard Windows uninstall. | Windows 10 or Windows 11, 64-bit. |
| `CodeBoxX-win-x64.zip` | Portable build for users who prefer extracting and running the application directly. | Windows 10 or Windows 11, 64-bit, plus .NET Desktop Runtime 8 x64. |

### Installer

Download `CodeBoxX-Setup-win-x64.exe`, run the installer, complete the Windows setup wizard, and launch **CodeBox X** from the Start menu or the optional desktop shortcut. The installer contains the self-contained application runtime.

### Portable build

Download and extract `CodeBoxX-win-x64.zip` to a writable local folder, then run `CodeBoxX.exe`. The portable build is framework-dependent, so install the **.NET Desktop Runtime 8 x64** if it is not already present. The modern HTML Live Preview also requires the **Microsoft Edge WebView2 Runtime**, which is commonly present on current Windows installations.

## System requirements

| Component | Requirement |
|---|---|
| Operating system | Windows 10 or Windows 11, 64-bit. |
| Installed application | The self-contained installer includes the .NET runtime required by CodeBox X. |
| Portable build | .NET Desktop Runtime 8 x64. |
| HTML Live Preview | Microsoft Edge WebView2 Runtime. |
| Build from source | .NET 8 SDK; Visual Studio 2022 with the **.NET desktop development** workload is optional. |
| Language execution | Python, Node.js, Java, Lua, SQLite, or other language tooling only when the corresponding Run, terminal, or package-management feature is used. |

## Build from source

Clone the repository on Windows, open a PowerShell or Developer Command Prompt in the repository root, and run:

```powershell
dotnet restore .\CodeBoxX\CodeBoxX.csproj
dotnet build .\CodeBoxX\CodeBoxX.csproj -c Release
dotnet run --project .\CodeBoxX\CodeBoxX.csproj
```

A framework-dependent x64 publish can be created with:

```powershell
dotnet publish .\CodeBoxX\CodeBoxX.csproj -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

A self-contained x64 publish, suitable for an installer or offline deployment, can be created with:

```powershell
dotnet publish .\CodeBoxX\CodeBoxX.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64-selfcontained
```

The included installer definition is [`Installer/CodeBoxX.iss`](Installer/CodeBoxX.iss). Compile it with Inno Setup 6 after creating the self-contained publish output.

## Supported languages

CodeBox X recognizes common source and project formats for editing and syntax-aware presentation.

| Language or format | Typical extensions |
|---|---|
| Python | `.py` |
| C# and .NET | `.cs`, `.csproj`, `.sln`, `.fsproj` |
| C and C++ | `.c`, `.h`, `.cpp`, `.hpp` |
| Java | `.java` |
| JavaScript and TypeScript | `.js`, `.ts`, `.tsx` |
| Structured data and markup | `.json`, `.xml`, `.xaml`, `.html`, `.css`, `.sql` |
| Game and scripting formats | `.lua`, `.gd` |
| Documentation and text | `.md`, `.txt` |

The Run command proposes an appropriate local command for supported file types. The interpreter, SDK, compiler, or runtime must be installed and available on `PATH`; process output and errors are shown in the editor output area.

## Editor, explorer, and safety controls

The File Explorer lets users open a workspace, create project content, rename items, and delete files or folders after confirmation. If a deleted file is open, CodeBox X closes its related tab to keep the editor state consistent. Every document tab has a close control. Closing a changed document, or closing the main window with changed documents, presents **Save**, **Don't Save**, and **Cancel** choices so source code is not discarded accidentally.

The editor supports multiple documents, line numbers, syntax-aware coloring, Find Next, case-sensitive search, Replace All, undo/redo, saving, Save As, drag-and-drop, adjustable font size, and optional autosave. Runtime exceptions are captured in `%LOCALAPPDATA%\CodeBoxX\runtime-errors.log` for troubleshooting instead of failing silently.

## Terminal and execution

The integrated terminal starts a persistent local `cmd.exe` session rooted in the active workspace when one is open. **New Terminal** starts a clean session and **Clear Terminal** clears the displayed transcript without changing the shell state. Commands run with the permissions of the current Windows user and remain asynchronous so the WPF interface stays responsive.

> Review terminal commands before submitting them. CodeBox X displays command output and errors but cannot make a destructive shell command safe.

## Live Preview

For HTML, Markdown, JSON, XML/XAML, and text, **Live Preview** opens a native WPF preview window backed by WebView2. Local HTML preview preserves relative CSS, JavaScript, image, and other file references by resolving them from the source file's directory. Preview errors, malformed JSON, malformed XML, unsupported file types, and a missing WebView2 Runtime produce clear in-app messages.

For Python files, **Python Live Preview** runs the saved file inside a single managed process and shows separate standard-output and error streams. The window provides **Run**, **Stop**, and **Restart** actions, restarts an active preview after the source is saved, prevents concurrent preview processes, and reports missing interpreters, syntax errors, runtime errors, process exits, and preview timeouts. When a project has an MPM Python environment, the preview uses that project environment automatically.

## MPM Package Manager

**MPM** is CodeBox X's project-aware package manager. It detects Python, Node.js/TypeScript, and .NET projects from common project files and source extensions, then chooses the matching public provider: PyPI/pip, npm, or NuGet/.NET.

| Project type | Detection markers |
|---|---|
| Python | `requirements.txt`, `pyproject.toml`, or `.py` source files. |
| Node.js / TypeScript | `package.json`, `package-lock.json`, `.js`, `.ts`, or `.tsx` files. |
| .NET | `.csproj`, `.fsproj`, `.sln`, or `.cs` files. |

The MPM window offers **Add Project File**, **Refresh MPM**, package search and information, dependency listing, install, uninstall, update, restore, source display, compatibility warnings, and operation output. It writes CodeBox X-managed project dependency metadata to `.codebox-mpm.json`. The terminal also understands these commands:

```text
mpm search <package>
mpm info <package>
mpm list
mpm install <package>
mpm uninstall <package>
mpm update
mpm restore
```

Every state-changing package operation requires confirmation. MPM validates package identifiers, avoids shell invocation for provider commands, blocks appended command text, disables npm package scripts, accepts only binary Python wheels, and redacts accidental secret-like values from operation output.

## Plugin and Extension Marketplace

The native **Marketplace** window provides an offline local catalog abstraction for syntax themes, linters, formatters, language support, and editor tools. It includes search, category filters, Featured, Popular, Recently Added, extension details, installed-extension management, enable/disable controls, uninstall, update state, theme preview/application/reset, and diagnostics for enabled linters.

The bundled examples are **Midnight Aurora Theme**, **Clean Code Linter**, and **Focus Tools**. The sample linter demonstrates line-and-column diagnostics for `TODO` markers and trailing whitespace in Python, C#, JavaScript, and TypeScript. Extension packages are data-only `.cbxext` JSON packages; CodeBox X does not load third-party assemblies, scripts, or executables from them.

Before installation, the application validates package size, manifest schema, identifiers, known permissions, required theme/linter payloads, and a SHA-256 payload integrity hash. Unsupported packages, malformed JSON, permission failures, over-sized packages, and integrity mismatches are rejected without changing the installed-extension state.

## Gemini AI Assistant

The optional **AI Assistant** uses the Gemini 3.1 Flash-Lite endpoint to chat about the active file or workspace and perform code-oriented actions. Available actions include **Explain**, **Fix**, **Refactor**, **Generate**, **Add Comments**, and **Ask Project**. Responses can be copied or inserted into the active editor at the selection or cursor location.

Open **View → AI Settings** to enter a Gemini API key. CodeBox X encrypts the saved key with Windows DPAPI for the current user before storing it in `%LOCALAPPDATA%\CodeBoxX\settings.json`. The saved key is not printed to the output panel, terminal, runtime log, source code, or release archive. The settings window provides Save, Show/Hide entry text, Test Connection, and Clear API Key controls. Missing keys, invalid keys, network failures, rate limits, unavailable models, timeouts, and empty responses are surfaced as clear, non-secret error messages.

> A valid Gemini API key is required only for live AI requests. The remainder of CodeBox X works locally without one.

## Publish Website

**Publish Website** is a local packaging workflow for static websites, not a remote upload service. With a workspace open and an `index.html` file in its root, CodeBox X saves changed workspace documents, creates `publish\site.zip` inside the workspace, and offers to reveal the archive in File Explorer. The archive excludes `.git`, `.vs`, `bin`, `obj`, `node_modules`, `publish`, and similar build or repository directories. Uploading the resulting archive to a hosting provider remains under the user's control.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+N` | Create a new file. |
| `Ctrl+O` | Open a file. |
| `Ctrl+Shift+O` | Open a workspace folder. |
| `Ctrl+S` | Save the active document. |
| `Ctrl+Shift+S` | Save the active document under a new name. |
| `Ctrl+W` | Close the active tab. |
| `Ctrl+H` | Show or hide search and replace. |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo. |
| `F5` | Run the active file. |
| `Shift+F5` | Stop the active process. |
| `F11` | Toggle full-screen mode. |
| `Ctrl+\`` | Focus the integrated terminal. |

## Local data and privacy

User preferences such as theme, font size, autosave, and recent items are stored at:

```text
%LOCALAPPDATA%\CodeBoxX\settings.json
```

Deleting that file resets the corresponding local preferences. The repository `.gitignore` excludes build output, editor state, logs, development configuration, key material, and common credential files. Never commit a Gemini API key, token, password, or user-specific settings file.

## Project structure

```text
CodeBox-X/
├── CodeBoxX.sln                 # Visual Studio solution
├── CodeBoxX/                    # WPF application project
│   ├── Assets/                  # Application icons
│   ├── Controls/                # Native editor controls
│   ├── Dialogs/                 # File and unsaved-change dialogs
│   ├── Extensions/              # Local Marketplace package data
│   ├── Models/                  # Editor, AI, MPM, and extension models
│   ├── Services/                # Terminal, preview, AI, MPM, and publishing services
│   ├── Views/                   # Native WPF tool windows
│   ├── MainWindow.xaml          # Main desktop interface
│   └── CodeBoxX.csproj          # .NET 8 WPF project definition
├── Installer/CodeBoxX.iss       # Inno Setup installer definition
├── .gitignore                   # Source-control exclusions
└── LICENSE                      # MIT License
```

## Release verification

Version 1.2.1 was prepared from a clean Release build with **0 warnings and 0 errors**. Native verification covered editor document save/read/replacement/deletion behavior, asynchronous terminal output, Python Live Preview output, MPM workspace detection and refresh, Marketplace catalog/theme/linter behavior, settings loading, Gemini missing-key safety, and the Release executable startup process. The live Gemini connection is intentionally not exercised without a user-owned API key.

## Changelog

### v1.2.1

MPM now refreshes project detection without restarting CodeBox X, detects Python, Node.js/TypeScript, and .NET workspaces from supported project and source files, loads declared dependencies before provider tools are initialized, and offers safe Add Project File workflows. This release also includes the production installer, portable Windows x64 archive, embedded application icon, persistent terminal session fixes, WebView2-based Live Preview, Python Live Preview lifecycle fixes, Website Publish packaging, Marketplace package validation, and secure Gemini AI configuration.

## License

CodeBox X is distributed under the [MIT License](LICENSE). Copyright notices and license text must remain with substantial portions of the software.

---

For feature requests and issue reports, please use the repository's GitHub Issues section.
