using System.Diagnostics;
using System.Text;

namespace CodeBoxX.Services;

public enum TerminalStream
{
    Output,
    Error,
    System
}

public readonly record struct TerminalData(long SessionId, string Text, TerminalStream Stream);

public sealed class TerminalSession : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private long _nextSessionId;
    private long _currentSessionId;

    public event EventHandler<TerminalData>? DataReceived;

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

    public bool Start(string? workingDirectory, out string error)
    {
        Stop();
        error = string.Empty;

        var directory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Process? process = null;
        long sessionId = 0;

        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/D /Q",
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
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
                sessionId = ++_nextSessionId;
                _currentSessionId = sessionId;
                _process = process;
            }

            var capturedProcess = process;
            var capturedSessionId = sessionId;
            process.OutputDataReceived += (_, args) => EmitForSession(capturedSessionId, args.Data, TerminalStream.Output);
            process.ErrorDataReceived += (_, args) => EmitForSession(capturedSessionId, args.Data, TerminalStream.Error);
            process.Exited += (_, _) => HandleExited(capturedProcess, capturedSessionId);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _currentSessionId = 0;
                }
            }
            try { process?.Dispose(); } catch { }
            return false;
        }
    }

    public bool Send(string command, out string error)
    {
        error = string.Empty;
        lock (_gate)
        {
            try
            {
                if (_process is null || _process.HasExited)
                {
                    error = "The terminal is not running. Select New Terminal to start one.";
                    return false;
                }
                _process.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _currentSessionId = 0;
        }
        if (process is null) return;

        try
        {
            // Closing input and terminating the process tree keeps the UI responsive.
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have ended between HasExited and Kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void HandleExited(Process process, long sessionId)
    {
        var isCurrentSession = false;
        lock (_gate)
        {
            if (ReferenceEquals(_process, process) && _currentSessionId == sessionId)
            {
                _process = null;
                // Keep the session identifier until a new terminal starts so the
                // final status message is accepted, while old sessions remain stale.
                isCurrentSession = true;
            }
        }
        if (isCurrentSession) EmitForSession(sessionId, "Terminal session ended. Select New Terminal to start another session.", TerminalStream.System);
    }

    private void EmitForSession(long sessionId, string? text, TerminalStream stream)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_currentSessionId != sessionId) return;
        }
        DataReceived?.Invoke(this, new TerminalData(sessionId, text, stream));
    }

    public void Dispose() => Stop();
}
