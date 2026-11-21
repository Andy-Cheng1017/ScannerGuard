using System.Diagnostics;

internal static class Diagnostics
{
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();
    static string? _path;
    static bool _completed;
    static bool _faulted;
    static bool _writeWarningShown;

    internal static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScannerGuard");

    internal static void Initialize(string? directory = null)
    {
        _path = Path.Combine(directory ?? LogDirectory, "diagnostics.log");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Fatal("unhandled", e.ExceptionObject as Exception);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Write("EXIT", $"reason={(_faulted ? "fault" : _completed ? "completed" : "not-completed")} exitCode={Environment.ExitCode}");
        Write("START", $"version={typeof(Diagnostics).Assembly.GetName().Version} build={typeof(Diagnostics).Module.ModuleVersionId} runtime={Environment.Version}");
    }

    internal static void Complete() => _completed = true;

    internal static void Fatal(string source, Exception? error)
    {
        _faulted = true;
        Write("FATAL", $"source={source}{Environment.NewLine}{error}");
    }

    internal static void Write(string kind, string details)
    {
        if (_path == null) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path,
                    $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} uptimeMs={Environment.TickCount64 - StartedAt} {kind} {details}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"ScannerGuard diagnostics unavailable: {ex}");
                if (_writeWarningShown) return;
                _writeWarningShown = true;
                // Queue the warning; never pump window messages inside an input callback.
                ThreadPool.QueueUserWorkItem(_ => System.Windows.Forms.MessageBox.Show(
                    $"Cannot write ScannerGuard diagnostics to {_path}.{Environment.NewLine}{ex.Message}",
                    "ScannerGuard diagnostics", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning));
            }
        }
    }

    static readonly long StartedAt = Environment.TickCount64;
}
