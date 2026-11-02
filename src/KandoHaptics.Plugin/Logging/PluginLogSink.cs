namespace Loupedeck.KandoHapticsPlugin
{
    using System;

    using KandoHaptics.Logging;

    /// <summary>
    /// Дублирует важные записи (Info и выше) в журнал Logi Plugin Service
    /// (%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\KandoHaptics.log).
    /// Debug туда не попадает, чтобы не засорять журнал сервиса.
    /// </summary>
    internal sealed class PluginLogSink : IKandoLogger
    {
        private readonly PluginLogFile _log;

        public PluginLogSink(PluginLogFile log) => this._log = log;

        public Boolean IsEnabled(LogLevel level) => this._log is not null && level is >= LogLevel.Info and < LogLevel.None;

        public void Log(LogLevel level, String message, Exception exception = null)
        {
            switch (level)
            {
                case LogLevel.Info:
                    this._log.Info(message);
                    break;
                case LogLevel.Warning:
                    if (exception is null)
                    {
                        this._log.Warning(message);
                    }
                    else
                    {
                        this._log.Warning(exception, message);
                    }

                    break;
                case LogLevel.Error:
                    if (exception is null)
                    {
                        this._log.Error(message);
                    }
                    else
                    {
                        this._log.Error(exception, message);
                    }

                    break;
            }
        }
    }
}
