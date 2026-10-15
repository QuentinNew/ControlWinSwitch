using System.Text;

namespace ControlWin;

/// <summary>Tiny file logger (%APPDATA%\ControlWin\log.txt). Never throws.</summary>
internal static class Log
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object s_lock = new();
    private static string? s_path;
    private static bool s_initialized;

    /// <summary>Also echo every line to <see cref="Console"/> (--debug-input).</summary>
    public static bool ConsoleEnabled { get; set; }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";

        if (ConsoleEnabled)
        {
            try { Console.WriteLine(line); } catch { }
        }

        try
        {
            lock (s_lock)
            {
                if (!s_initialized) Initialize();
                if (s_path is null) return;
                File.AppendAllText(s_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private static void Initialize()
    {
        s_initialized = true;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ControlWin");
            Directory.CreateDirectory(dir);
            s_path = Path.Combine(dir, "log.txt");

            var info = new FileInfo(s_path);
            if (info.Exists && info.Length > MaxBytes) File.Delete(s_path);
        }
        catch
        {
            s_path = null;
        }
    }
}
