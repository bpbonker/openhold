using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Openhold.Server.Logging;

/// <summary>
/// Writes the server's log to one file a day (<c>openhold-yyyyMMdd.log</c>) in the log folder, which backups include,
/// and deletes files older than the retention period. Lines are queued and written by one background thread, so
/// logging never slows a request down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly int _retentionDays;
    private readonly BlockingCollection<(DateTime At, string Line)> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string folder, int retentionDays)
    {
        _folder = folder;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(folder);
        _writer = new Thread(Write) { IsBackground = true, Name = "Openhold log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Enqueue(string line) => _queue.TryAdd((DateTime.UtcNow, line));

    private void Write()
    {
        StreamWriter? writer = null;
        var day = DateOnly.MinValue;
        foreach (var (at, line) in _queue.GetConsumingEnumerable())
        {
            var today = DateOnly.FromDateTime(at);
            if (writer is null || today != day)
            {
                writer?.Dispose();
                day = today;
                var path = Path.Combine(_folder, $"openhold-{today:yyyyMMdd}.log");
                writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8) { AutoFlush = true };
                DeleteOldFiles();
            }
            writer.WriteLine(line);
        }
        writer?.Dispose();
    }

    private void DeleteOldFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_folder, "openhold-*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-_retentionDays))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // In use or already gone: tried again tomorrow.
                }
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z {Level(logLevel)} {category}: {formatter(state, exception)}";
            provider.Enqueue(exception is null ? line : $"{line}{Environment.NewLine}{exception}");
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Critical => "CRIT",
            LogLevel.Error => "ERROR",
            LogLevel.Warning => "WARN",
            _ => "INFO",
        };
    }
}
