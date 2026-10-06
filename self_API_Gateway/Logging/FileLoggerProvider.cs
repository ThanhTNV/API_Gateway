using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace self_API_Gateway.Logging;

// Writes log entries to {directory}/yyyy-MM-dd.log, starting a new file when the local date changes.
// Levels are filtered through the standard "Logging:File:LogLevel" configuration section.
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly record struct Entry(DateOnly Date, string Text);

    private readonly string _directory;
    // Request threads only enqueue; a single background task does the disk I/O.
    // When the disk can't keep up, entries are dropped rather than stalling requests.
    private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(
        new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _writer;
    private bool _failureReported;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? file = null;
        var fileDate = default(DateOnly);
        try
        {
            while (await _queue.Reader.WaitToReadAsync())
            {
                try
                {
                    while (_queue.Reader.TryRead(out var entry))
                    {
                        if (file is null || entry.Date != fileDate)
                        {
                            file?.Dispose();
                            file = null;
                            file = Open(entry.Date);
                            fileDate = entry.Date;
                        }
                        file.Write(entry.Text);
                    }
                    file?.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Logging must never take the gateway down: report once and retry on the next entry.
                    if (!_failureReported)
                    {
                        _failureReported = true;
                        Console.Error.WriteLine($"File logging to '{_directory}' failed: {ex.Message}");
                    }
                    file = null;
                }
            }
        }
        finally
        {
            file?.Dispose();
        }
    }

    private StreamWriter Open(DateOnly date)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        // FileShare.ReadWrite so the file can be tailed while the gateway is running.
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var now = DateTimeOffset.Now;
            var text = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(Label(logLevel)).Append("] ")
                .Append(category).Append(": ")
                .AppendLine(formatter(state, exception));
            if (exception is not null)
            {
                text.AppendLine(exception.ToString());
            }

            provider._queue.Writer.TryWrite(new Entry(DateOnly.FromDateTime(now.DateTime), text.ToString()));
        }

        private static string Label(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT"
        };
    }
}

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddDailyFile(this ILoggingBuilder builder, string directory)
    {
        builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(directory));
        return builder;
    }
}
