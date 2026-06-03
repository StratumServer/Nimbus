using Microsoft.Extensions.Logging;

namespace Nimbus.Proxy;

internal static class Log
{
    private static readonly object gate = new();
    private static ILoggerFactory factory = BuildFactory(verbose: false);
    private static ILogger logger = factory.CreateLogger("Nimbus.Proxy");

    public static bool TraceEnabled { get; set; } = false;

    public static void Configure(bool verbose)
    {
        lock (gate)
        {
            TraceEnabled = verbose;
            var old = factory;
            factory = BuildFactory(verbose);
            logger = factory.CreateLogger("Nimbus.Proxy");
            old.Dispose();
        }
    }

    public static void Info(string msg) => logger.LogInformation("{Message}", msg);
    public static void Warn(string msg) => logger.LogWarning("{Message}", msg);
    public static void Trace(string msg) { if (TraceEnabled) logger.LogDebug("{Message}", msg); }
    public static void Error(string msg) => logger.LogError("{Message}", msg);

    private static ILoggerFactory BuildFactory(bool verbose)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
        });
    }
}
