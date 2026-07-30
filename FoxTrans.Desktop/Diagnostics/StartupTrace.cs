using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FoxTrans.Desktop.Diagnostics;

internal static class StartupTrace
{
    private static readonly string[] MarkNames =
    [
        "main",
        "app-init",
        "services",
        "vm",
        "window-ctor",
        "opened",
        "first-frame",
        "initialized"
    ];

    private static readonly object Sync = new();
    private static readonly Dictionary<string, double> Marks =
        new(StringComparer.Ordinal);
    private static readonly DateTime ProcessStartUtc = GetProcessStartUtc();
    private static bool _dumped;

    public static bool Enabled { get; } =
        IsEnvironmentFlagEnabled("FOXTRANS_STARTUP_TRACE");

    public static void Mark(string name)
    {
        if (!Enabled)
            return;

        bool shouldDump;
        lock (Sync)
        {
            if (!Marks.TryAdd(
                    name,
                    (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds))
            {
                return;
            }

            shouldDump = !_dumped &&
                Marks.ContainsKey("first-frame") &&
                Marks.ContainsKey("initialized");
            if (shouldDump)
                _dumped = true;
        }

        if (shouldDump)
            WriteCsvRow();
    }

    private static void WriteCsvRow()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "FoxTrans");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "startup-trace.csv");

            bool writeHeader = !File.Exists(path) ||
                new FileInfo(path).Length == 0;
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (writeHeader)
            {
                writer.Write("timestamp_utc,pid,opaque_window");
                foreach (string name in MarkNames)
                {
                    writer.Write(',');
                    writer.Write(name.Replace('-', '_'));
                    writer.Write("_ms");
                }
                writer.WriteLine();
            }

            writer.Write(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            // Keep the historical column so existing trace files remain
            // parseable after transparent rendering became the default again.
            writer.Write(",0");
            lock (Sync)
            {
                foreach (string name in MarkNames)
                {
                    writer.Write(',');
                    if (Marks.TryGetValue(name, out double milliseconds))
                    {
                        writer.Write(milliseconds.ToString(
                            "0.###",
                            CultureInfo.InvariantCulture));
                    }
                }
            }
            writer.WriteLine();
        }
        catch
        {
            // Startup tracing must never affect application startup.
        }
    }

    private static bool IsEnvironmentFlagEnabled(string name) =>
        string.Equals(
            Environment.GetEnvironmentVariable(name),
            "1",
            StringComparison.Ordinal);

    private static DateTime GetProcessStartUtc()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}
