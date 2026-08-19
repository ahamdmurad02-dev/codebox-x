using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodeBoxX.Services;

public enum PythonPreviewStream
{
    Output,
    Error,
    System
}

public readonly record struct PythonPreviewData(long SessionId, string Text, PythonPreviewStream Stream);
public readonly record struct PythonPreviewStatus(long SessionId, bool IsRunning, bool IsError, string Message);
public readonly record struct PythonPreviewStartResult(bool Success, string Message, string InterpreterPath, long SessionId, string Guidance)
{
    public static PythonPreviewStartResult Fail(string message) => new(false, message, string.Empty, 0, string.Empty);
    public static PythonPreviewStartResult Ok(string message, string interpreterPath, long sessionId, string guidance) => new(true, message, interpreterPath, sessionId, guidance);
}

/// <summary>
/// Runs one Python preview process at a time. The process is isolated from the UI,
/// writes stdout and stderr through events, and is stopped as a complete process tree.
/// </summary>
public sealed class PythonPreviewSession : IDisposable
{
    private static readonly (string FileName, string VersionArguments, string RunArgumentsPrefix)[] InterpreterCandidates =
    [
        ("python.exe", "--version", "-u"),
        ("python", "--version", "-u"),
        ("py.exe", "-3 --version", "-3 -u"),
        ("py", "-3 --version", "-3 -u")
    ];

    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private Timer? _timeoutTimer;
    private long _nextSessionId;
    private long _currentSessionId;

    public event EventHandler<PythonPreviewData>? DataReceived;
    public event EventHandler<PythonPreviewStatus>? StatusChanged;

    public long CurrentSessionId
    {
        get
        {
            lock (_gate) return _currentSessionId;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try { return _process is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    public async Task<PythonPreviewStartResult> StartAsync(string scriptPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stop();
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                return PythonPreviewStartResult.Fail("The Python file could not be found. Save the file to a valid location, then run Live Preview again.");
            }

            long sessionId;
            lock (_gate)
            {
                sessionId = ++_nextSessionId;
                _currentSessionId = sessionId;
            }

            var fullPath = Path.GetFullPath(scriptPath);
            var workingDirectory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            var interpreter = await FindInterpreterAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (interpreter is null)
            {
                ClearSessionIfCurrent(sessionId);
                return PythonPreviewStartResult.Fail("Python 3 was not found. Install Python 3 and enable its PATH option, or restore the project environment from MPM.");
            }
            var guidance = GetPreviewGuidance(fullPath);
            Process? process = null;

            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = interpreter.Value.FileName,
                        Arguments = $"{interpreter.Value.RunArgumentsPrefix} \"{fullPath}\"",
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };

                lock (_gate)
                {
                    if (_currentSessionId != sessionId)
                    {
                        return PythonPreviewStartResult.Fail("A newer Python preview request replaced this start request.");
                    }
                    _process = process;
                }

                var capturedProcess = process;
                var capturedSessionId = sessionId;
                process.OutputDataReceived += (_, args) => EmitData(capturedSessionId, args.Data, PythonPreviewStream.Output);
                process.ErrorDataReceived += (_, args) => EmitData(capturedSessionId, args.Data, PythonPreviewStream.Error);
                process.Exited += (_, _) => HandleExited(capturedProcess, capturedSessionId);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                StartTimeoutTimer(sessionId, timeout);
                EmitData(sessionId, $"[Python Live Preview] Started {Path.GetFileName(fullPath)} with {interpreter.Value.DisplayName}.", PythonPreviewStream.System);
                EmitStatus(sessionId, true, false, $"Running {Path.GetFileName(fullPath)} with {interpreter.Value.DisplayName}.");
                return PythonPreviewStartResult.Ok("Python Live Preview is running.", interpreter.Value.DisplayName, sessionId, guidance);
            }
            catch (Win32Exception)
            {
                ClearProcessIfCurrent(process, sessionId);
                try { process?.Dispose(); } catch { }
                return PythonPreviewStartResult.Fail($"Python could not be started from '{interpreter.Value.DisplayName}'. Verify that Python is installed correctly and available on PATH.");
            }
            catch (Exception ex)
            {
                ClearProcessIfCurrent(process, sessionId);
                try { process?.Dispose(); } catch { }
                return PythonPreviewStartResult.Fail($"Python Live Preview could not start: {ex.Message}");
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Stop()
    {
        Process? process;
        long sessionId;
        lock (_gate)
        {
            process = _process;
            sessionId = _currentSessionId;
            _process = null;
            _currentSessionId = 0;
            DisposeTimeoutTimer();
        }

        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process can exit between the state check and Kill.
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }

        if (sessionId != 0) StatusChanged?.Invoke(this, new PythonPreviewStatus(sessionId, false, false, "Python Live Preview stopped."));
    }

    private async Task<InterpreterCandidate?> FindInterpreterAsync(string scriptPath, CancellationToken cancellationToken)
    {
        var projectPython = FindProjectPython(scriptPath);
        if (!string.IsNullOrWhiteSpace(projectPython))
        {
            return new InterpreterCandidate(projectPython, "-u", $"Project Python ({projectPython})");
        }

        foreach (var candidate in InterpreterCandidates)
        {
            using var probe = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = candidate.FileName,
                    Arguments = candidate.VersionArguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            try
            {
                probe.Start();
                var outputTask = probe.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = probe.StandardError.ReadToEndAsync(cancellationToken);
                var exitTask = probe.WaitForExitAsync(cancellationToken);
                var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false);
                if (completed != exitTask && !probe.HasExited)
                {
                    try { probe.Kill(entireProcessTree: true); } catch { }
                    continue;
                }

                await probe.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var reportedVersion = (await outputTask.ConfigureAwait(false) + " " + await errorTask.ConfigureAwait(false)).Trim();
                if (probe.ExitCode == 0 && reportedVersion.Contains("Python", StringComparison.OrdinalIgnoreCase))
                {
                    return new InterpreterCandidate(candidate.FileName, candidate.RunArgumentsPrefix, reportedVersion);
                }
            }
            catch (Win32Exception)
            {
                // This candidate is not available; continue to the next supported launcher.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A broken candidate must not crash CodeBox X. Try the next one.
            }
        }

        return null;
    }

    private static string? FindProjectPython(string scriptPath)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(scriptPath) ?? string.Empty);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, ".codebox-mpm", "python", "Scripts", "python.exe");
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }
        catch
        {
            // Fall back to a system Python interpreter.
        }
        return null;
    }

    private void StartTimeoutTimer(long sessionId, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return;
        lock (_gate)
        {
            DisposeTimeoutTimer();
            _timeoutTimer = new Timer(_ => HandleTimeout(sessionId), null, timeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void HandleTimeout(long sessionId)
    {
        Process? process = null;
        lock (_gate)
        {
            if (_currentSessionId != sessionId || _process is null) return;
            process = _process;
            _process = null;
            _currentSessionId = 0;
            DisposeTimeoutTimer();
        }

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may already be exiting.
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }

        DataReceived?.Invoke(this, new PythonPreviewData(sessionId, "Preview timeout reached. The Python process was stopped safely.", PythonPreviewStream.Error));
        StatusChanged?.Invoke(this, new PythonPreviewStatus(sessionId, false, true, "Preview timed out and was stopped."));
    }

    private void HandleExited(Process process, long sessionId)
    {
        var isCurrent = false;
        var exitCode = -1;
        lock (_gate)
        {
            if (ReferenceEquals(_process, process) && _currentSessionId == sessionId)
            {
                try { exitCode = process.ExitCode; } catch { }
                _process = null;
                DisposeTimeoutTimer();
                isCurrent = true;
            }
        }

        if (!isCurrent) return;
        var isError = exitCode != 0;
        EmitData(sessionId, isError ? $"Python preview ended with exit code {exitCode}." : "Python preview finished successfully.", PythonPreviewStream.System);
        EmitStatus(sessionId, false, isError, isError ? $"Preview failed (exit code {exitCode})." : "Preview finished.");
        try { process.Dispose(); } catch { }
    }

    private void ClearSessionIfCurrent(long sessionId)
    {
        lock (_gate)
        {
            if (_currentSessionId != sessionId) return;
            _process = null;
            _currentSessionId = 0;
            DisposeTimeoutTimer();
        }
    }

    private static string GetPreviewGuidance(string scriptPath)
    {
        try
        {
            var source = File.ReadAllText(scriptPath);
            var usesTkinter = source.Contains("import tkinter", StringComparison.OrdinalIgnoreCase)
                || source.Contains("from tkinter", StringComparison.OrdinalIgnoreCase)
                || source.Contains("tk.Tk(", StringComparison.OrdinalIgnoreCase);
            if (!usesTkinter) return string.Empty;

            if (!source.Contains(".mainloop(", StringComparison.OrdinalIgnoreCase))
            {
                return "Tkinter preview note: this program creates a Tkinter window but does not call mainloop(). Add root.mainloop() after creating EasyGame(root) so the game window stays open.";
            }

            return "Tkinter preview note: the game opens in a separate native Python window. This panel shows the process status, standard output, and errors.";
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ClearProcessIfCurrent(Process? process, long sessionId)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_process, process) && _currentSessionId == sessionId)
            {
                _process = null;
                _currentSessionId = 0;
                DisposeTimeoutTimer();
            }
        }
    }

    private void EmitData(long sessionId, string? text, PythonPreviewStream stream)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (_currentSessionId != sessionId) return;
        }
        DataReceived?.Invoke(this, new PythonPreviewData(sessionId, text, stream));
    }

    private void EmitStatus(long sessionId, bool isRunning, bool isError, string message)
    {
        lock (_gate)
        {
            if (_currentSessionId != sessionId) return;
        }
        StatusChanged?.Invoke(this, new PythonPreviewStatus(sessionId, isRunning, isError, message));
    }

    private void DisposeTimeoutTimer()
    {
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
    }

    public void Dispose()
    {
        Stop();
        _startGate.Dispose();
    }

    private readonly record struct InterpreterCandidate(string FileName, string RunArgumentsPrefix, string DisplayName);
}
