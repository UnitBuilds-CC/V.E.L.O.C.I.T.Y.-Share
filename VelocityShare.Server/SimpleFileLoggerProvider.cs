using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace VelocityShare.Server
{
    /// <summary>
    /// Lightweight file logger for Production post-mortem debugging.
    /// Writes daily rotating log files to the specified directory.
    /// </summary>
    public class SimpleFileLoggerProvider : ILoggerProvider
    {
        private readonly string _logDir;
        private StreamWriter _writer;
        private DateTime _currentDay;
        private readonly object _lock = new();

        public SimpleFileLoggerProvider(string logDir)
        {
            _logDir = logDir;
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            _currentDay = DateTime.UtcNow.Date;
            _writer = OpenWriter(_logDir, _currentDay);
            // Clean up log files older than 30 days on startup
            CleanupOldLogs(30);
        }

        private static StreamWriter OpenWriter(string logDir, DateTime day)
        {
            var path = Path.Combine(logDir, $"velocity-{day:yyyy-MM-dd}.log");
            return new StreamWriter(path, append: true) { AutoFlush = true };
        }

        public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(this, categoryName);

        public void Dispose()
        {
            lock (_lock) { _writer.Dispose(); }
        }

        private void CleanupOldLogs(int keepDays)
        {
            try
            {
                var cutoff = DateTime.UtcNow.Date.AddDays(-keepDays);
                foreach (var f in Directory.GetFiles(_logDir, "velocity-*.log"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var datePart = name.Replace("velocity-", "");
                    if (DateTime.TryParse(datePart, out var fileDate) && fileDate < cutoff)
                    {
                        try { File.Delete(f); } catch { /* best effort */ }
                    }
                }
            }
            catch { /* best effort */ }
        }

        internal void WriteLog(string message)
        {
            lock (_lock)
            {
                try
                {
                    // Rotate log file at midnight UTC
                    var today = DateTime.UtcNow.Date;
                    if (today != _currentDay)
                    {
                        _writer.Dispose();
                        _currentDay = today;
                        var logFile = Path.Combine(_logDir, $"velocity-{today:yyyy-MM-dd}.log");
                        _writer = new StreamWriter(logFile, append: true) { AutoFlush = true };
                    }
                    _writer.WriteLine(message);
                }
                catch { /* best effort */ }
            }
        }

        private class SimpleFileLogger : ILogger
        {
            private readonly SimpleFileLoggerProvider _provider;
            private readonly string _category;

            public SimpleFileLogger(SimpleFileLoggerProvider provider, string category)
            {
                _provider = provider;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var msg = $"{DateTime.UtcNow:O} [{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception != null) msg += $"\n  {exception}";
                _provider.WriteLog(msg);
            }
        }
    }
}
