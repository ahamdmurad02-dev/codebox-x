# Changelog

All notable changes to CodeBox X are documented in this file.

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
