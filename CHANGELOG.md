# Changelog

All notable changes to CodeBox X are documented in this file.

## CodeBox X v1.2.2 — CodeBox X Agent

### Added

| Area | Change |
|---|---|
| Native Agent | Added **CodeBox X Agent** in the Explorer sidebar, main toolbar, welcome actions, and View menu. The native panel provides Agent chat, model/API status, cancellation, workspace permission, project search, analysis, reviewed change plans, Accept/Reject, a modified-files list, Clear Chat, and one-step undo. |
| Workspace privacy | Workspace access is opt-in. Before Gemini receives project context, CodeBox X excludes protected directories, binary/build artifacts, `.env` and private-key files, and settings; values resembling API keys, tokens, passwords, and secrets are redacted. |
| Safe file changes | Gemini returns a structured plan only. CodeBox X validates every relative path, blocks protected/out-of-workspace/binary targets, shows a human-readable file list and rationale, and writes files only after **Accept Changes**. Deletions and project-wide proposals require additional confirmation. |
| Reversal and integration | Accepted edits retain one-step prior-file backups for **Undo Agent Changes**. Open tabs refresh after Agent writes or restores files; deleted open files close safely, the Explorer refreshes, and diagnostics are queued again. |
| Agent tools | Added confirmed Agent access to project search, integrated terminal focus, Run Active File, and detected .NET builds. The Agent never sends terminal commands, installs packages, or runs builds without the user’s confirmation. |

### Validation

The v1.2.2 Agent implementation compiles as a Windows Release build with **0 warnings and 0 errors**. Its local Agent service validates workspace boundaries, redaction, proposal parsing, accepted create/modify/delete handling, rejection behavior, and one-step undo. Gemini uses the existing DPAPI-protected API key provider and continues to present missing-key, authentication, network, rate-limit, timeout, safety, and empty-response errors without revealing credentials.

## CodeBox X v1.2.1 — Active File Execution and Secure Updater

### Added

| Area | Change |
|---|---|
| C# | `.cs` and `.csproj` execution now detects the .NET SDK. Projects use `dotnet run --project`; active source files use an isolated generated SDK project where appropriate. |
| Process controls | Added visible **Restart** controls and `Ctrl+F5`. Run, Stop, and Restart protect against duplicate processes and stop complete child-process trees. |
| Output | Active run commands use robust Windows command quoting, retain the file/project working directory, and stream stdout and stderr into the integrated terminal. |
| Secure updater | Added **Update CodeBox X** in Help, Settings, and About, with a native dialog that checks the pinned official GitHub Releases endpoint, compares versions, displays release notes, reports download progress, verifies GitHub’s SHA-256 asset digest, validates the Windows PE header, and safely starts only a verified installer. |

### Improved

Python Active File execution now resolves `python.exe` first and falls back to the Windows `py.exe -3` launcher. File dialogs now expose C# project files in addition to C# source files.

### Validation

The v1.2.1 update passed a full Windows Release build with **0 warnings and 0 errors**. The active-file verification suite successfully executed Python and C# active files with the installed Python 3.12 and .NET SDK. The updater verification suite successfully exercised the live GitHub Releases API, current-version comparison, an actual installer download, progress reporting, executable-header validation, SHA-256 verification, and rejection of an unsafe download URL. C++ and GDScript active-run support were removed by product direction and are not advertised as executable workflows.
