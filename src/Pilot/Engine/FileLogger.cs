using System.Collections.Concurrent;

namespace WindowsPilot.Engine;

/// <summary>
/// Appended-line file logger — the app runs as a GUI-subsystem scheduled task
/// with no console (a console exe in Session 1 gets adopted by Windows
/// Terminal as a visible window). Replaces the old `cmd /c exe >> pilot.log`
/// wrapper: same log path, no window. Best-effort: a busy/locked file never
/// takes the service down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    internal void Write(LogLevel level, string category, string message)
    {
        string line;
        try
        {
            // NB: LogLevel is an enum — only G/g/X/x/F/f/D/d format specs are
            // legal; a bad one throws inside the logger and (via the logging
            // plumbing) crashes the whole host. Stringify defensively.
            var lvl = level.ToString().ToUpperInvariant();
            line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{lvl}] {category}: {message}";
        }
        catch { line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [LOG] (format failure)"; }
        lock (_gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch (IOException) { /* disk busy — drop, never crash the pilot */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    sealed class FileLogger(FileLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += " | " + exception;
            p.Write(logLevel, category, msg);
        }
    }
}
