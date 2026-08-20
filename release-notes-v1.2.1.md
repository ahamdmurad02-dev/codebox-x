## CodeBox X v1.2.1

This Windows 10/11 desktop update improves **Run Active File** for Python and C# while preserving CodeBox X’s existing editor, terminal, Live Preview, MPM, Marketplace, and Gemini AI Assistant features.

### Highlights

- Python active-file execution discovers `python.exe` and falls back to `py.exe -3`.
- C# supports both `.cs` active files and `.csproj` projects through the installed .NET SDK.
- New **Restart** controls and `Ctrl+F5` restart the most recent active run safely.
- `Shift+F5` stops the complete process tree, and duplicate active-run processes are prevented.
- stdout and stderr are streamed to the integrated terminal with reliable Windows command quoting.
- Missing runtime messages now open the output panel and show a clear native alert.

### Validation

- Windows Release build: **0 warnings, 0 errors**.
- Real Python active-file execution: passed.
- Real C# active-file execution with the installed .NET SDK: passed.

### Downloads

- `CodeBoxX-Setup-win-x64.exe` — self-contained Windows x64 installer.
- `CodeBoxX-win-x64.zip` — portable Windows x64 build; requires .NET Desktop Runtime 8 x64.

C++ and GDScript **active-run** support are not included in this release by product direction. Their existing editor language recognition remains unchanged.
