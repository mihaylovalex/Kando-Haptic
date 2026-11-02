namespace Loupedeck.KandoHapticsPlugin
{
    using System;
    using System.IO;
    using System.Threading.Tasks;

    using KandoHaptics;
    using KandoHaptics.Configuration;
    using KandoHaptics.Logging;

    /// <summary>
    /// Плагин Logi Actions SDK: при загрузке Logi Plugin Service запускает
    /// <see cref="KandoHapticsRuntime"/> (поиск Kando, WebSocket IPC, обработка событий)
    /// и превращает взаимодействия с меню Kando в haptic-события MX Master 4.
    /// Назначать действие на кнопку мыши не требуется: работа идёт в фоне сервиса.
    /// </summary>
    public class KandoHapticsPlugin : Plugin
    {
        private FileLogger _fileLogger;
        private IKandoLogger _log = NullLogger.Instance;

        // Плагин не управляет приложением через горячие клавиши и не привязан к процессу.
        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        internal KandoHapticsRuntime Runtime { get; private set; }

        public override void Load()
        {
            try
            {
                var logPath = Path.Combine(SettingsManager.DefaultDirectory, "Logs", "KandoHaptics.log");
                this._fileLogger = new FileLogger(logPath, LogLevel.Info);
                this._log = new CompositeLogger(this._fileLogger, new PluginLogSink(this.Log));
                this._log.Info($"Загрузка плагина Kando Haptics {typeof(KandoHapticsPlugin).Assembly.GetName().Version}.");

                var provider = new LogitechHapticProvider(this.PluginEvents, this._log);
                var packageDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(this.AssemblyFilePath) ?? String.Empty, ".."));
                if (!InstallPath.IsAscii(packageDirectory))
                {
                    this._log.Warning(
                        $"Плагин установлен в каталог с не-ASCII символами ({packageDirectory}). Logi Options+ не читает " +
                        "events\\extra\\eventMapping.yaml по такому пути, и вибрации не будет. Подключите плагин из каталога " +
                        "без кириллицы (например, C:\\ProgramData\\KandoHaptics) — см. docs/INSTALLATION.md.");
                }

                var eventSourceYaml = Path.Combine(packageDirectory, "events", "DefaultEventSource.yaml");
                provider.RegisterEvents(declaredInYaml: File.Exists(eventSourceYaml));

                this.Runtime = new KandoHapticsRuntime(provider, this._log);
                this.Runtime.Settings.Changed += this.OnSettingsChanged;
                this.Runtime.Start();
            }
            catch (Exception ex)
            {
                // Плагин остаётся загруженным, даже если что-то пошло не так.
                this._log.Error("Ошибка при загрузке Kando Haptics.", ex);
            }
        }

        public override void Unload()
        {
            this._log.Info("Выгрузка плагина Kando Haptics.");
            var runtime = this.Runtime;
            this.Runtime = null;
            if (runtime is not null)
            {
                runtime.Settings.Changed -= this.OnSettingsChanged;
                try
                {
                    // Task.Run исключает взаимоблокировку с контекстом синхронизации сервиса.
                    if (!Task.Run(async () => await runtime.DisposeAsync()).Wait(TimeSpan.FromSeconds(8)))
                    {
                        this._log.Warning("Остановка Kando Haptics заняла больше 8 с.");
                    }
                }
                catch (Exception ex)
                {
                    this._log.Error("Ошибка при остановке Kando Haptics.", ex);
                }
            }

            this._log = NullLogger.Instance;
            this._fileLogger?.Dispose();
            this._fileLogger = null;
        }

        private void OnSettingsChanged(EffectiveSettings settings)
        {
            if (this._fileLogger is not null)
            {
                this._fileLogger.MinimumLevel = settings.LogLevel;
            }
        }
    }
}
