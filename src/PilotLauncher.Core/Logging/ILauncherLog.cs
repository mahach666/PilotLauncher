namespace PilotLauncher.Core.Logging;

public interface ILauncherLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : ILauncherLog
{
    public static readonly NullLog Instance = new();

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

/// <summary>Appends to <c>launcher.log</c>; rolls it over to <c>launcher.log.1</c> after 2 MB.</summary>
public sealed class FileLog : ILauncherLog
{
    private const long MaxBytes = 2 << 20;
    private readonly string _path;
    private readonly object _gate = new();

    public FileLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "launcher.log");
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                File.Move(_path, _path + ".1", overwrite: true);
            File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
        }
    }
}
