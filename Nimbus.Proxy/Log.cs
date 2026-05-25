namespace Nimbus.Proxy;

internal static class Log
{
    private static readonly object gate = new();

    public static bool TraceEnabled { get; set; } = false;

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Trace(string msg) { if (TraceEnabled) Write("TRACE", msg); }
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (gate) Console.WriteLine(line);
    }
}
