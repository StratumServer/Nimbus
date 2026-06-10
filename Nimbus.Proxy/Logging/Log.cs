namespace Nimbus.Proxy;

internal static class Log
{
    private static volatile bool _verbose;

    public static bool TraceEnabled => _verbose;
    public static void Configure(bool verbose) => _verbose = verbose;

    public static void Info(string msg)  => Write(null,    msg);
    public static void Warn(string msg)  => Write("warn",  msg);
    public static void Error(string msg) => Write("error", msg);
    public static void Trace(string msg) { if (_verbose) Write("debug", msg); }

    internal static void Write(string? level, string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine(level is null
            ? $"{ts} [nimbus] {msg}"
            : $"{ts} [nimbus] {level}: {msg}");
    }
}
