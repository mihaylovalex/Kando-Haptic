namespace KandoHaptics.Logging;

/// <summary>Уровни диагностического журнала.</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4,
}

/// <summary>Минимальный интерфейс логгера, общий для ядра и плагина.</summary>
public interface IKandoLogger
{
    bool IsEnabled(LogLevel level);

    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class KandoLoggerExtensions
{
    public static void Debug(this IKandoLogger log, string message) => log.Log(LogLevel.Debug, message);

    public static void Info(this IKandoLogger log, string message) => log.Log(LogLevel.Info, message);

    public static void Warning(this IKandoLogger log, string message, Exception? ex = null) => log.Log(LogLevel.Warning, message, ex);

    public static void Error(this IKandoLogger log, string message, Exception? ex = null) => log.Log(LogLevel.Error, message, ex);

    /// <summary>Отложенное форматирование: строка создаётся только если Debug включён.</summary>
    public static void Debug<T>(this IKandoLogger log, Func<T, string> format, T state)
    {
        if (log.IsEnabled(LogLevel.Debug))
        {
            log.Log(LogLevel.Debug, format(state));
        }
    }
}

public sealed class NullLogger : IKandoLogger
{
    public static readonly NullLogger Instance = new();

    public bool IsEnabled(LogLevel level) => false;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>Рассылает записи нескольким логгерам; сбой одного не мешает остальным.</summary>
public sealed class CompositeLogger : IKandoLogger
{
    private readonly IKandoLogger[] _loggers;

    public CompositeLogger(params IKandoLogger[] loggers) => this._loggers = loggers;

    public bool IsEnabled(LogLevel level)
    {
        foreach (var logger in this._loggers)
        {
            if (logger.IsEnabled(level))
            {
                return true;
            }
        }

        return false;
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        foreach (var logger in this._loggers)
        {
            try
            {
                if (logger.IsEnabled(level))
                {
                    logger.Log(level, message, exception);
                }
            }
            catch
            {
                // Логирование никогда не должно ломать плагин.
            }
        }
    }
}
