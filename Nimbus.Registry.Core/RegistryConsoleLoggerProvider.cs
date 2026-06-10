using Microsoft.Extensions.Logging;

namespace Nimbus.Registry;

// Lightweight ILoggerProvider that writes registry warnings and errors to stdout
// in the same clean format as the proxy's own Log class. Designed for use after
// ClearProviders() so the default Microsoft.Hosting.Lifetime and ASP.NET Core
// infrastructure logs are replaced with silence (or this where appropriate).
public sealed class RegistryConsoleLoggerProvider : ILoggerProvider
{
    private static readonly RegistryConsoleLogger _shared = new();
    public ILogger CreateLogger(string categoryName) => _shared;
    public void Dispose() { }

    private sealed class RegistryConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;
            var msg = formatter(state, exception);
            if (exception != null && !msg.Contains(exception.Message, StringComparison.Ordinal))
                msg += ": " + exception.Message;
            var level = logLevel >= LogLevel.Error ? "error" : "warn";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} [nimbus] {level}: {msg}");
        }
    }
}
