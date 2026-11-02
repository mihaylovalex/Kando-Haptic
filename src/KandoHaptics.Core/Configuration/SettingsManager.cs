namespace KandoHaptics.Configuration;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KandoHaptics.Logging;

/// <summary>
/// Загружает <c>%LOCALAPPDATA%\KandoHaptics\settings.json</c>, создаёт его при первом
/// запуске и перечитывает при изменении (FileSystemWatcher + debounce). При ошибке JSON
/// остаётся последняя рабочая конфигурация. Файл плагина никогда не перезаписывает
/// существующие пользовательские настройки (кроме явного переключения enabled).
/// </summary>
public sealed class SettingsManager : IDisposable
{
    public const string FileName = "settings.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly IKandoLogger _log;
    private readonly TimeSpan _reloadDelay;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private volatile EffectiveSettings _current = EffectiveSettings.Default;
    private bool _disposed;

    public SettingsManager(string path, IKandoLogger log, TimeSpan? reloadDelay = null)
    {
        this._path = Path.GetFullPath(path);
        this._log = log;
        this._reloadDelay = reloadDelay ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Вызывается после каждой успешной (пере)загрузки.</summary>
    public event Action<EffectiveSettings>? Changed;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KandoHaptics");

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    public string FilePath => this._path;

    public EffectiveSettings Current => this._current;

    /// <summary>Число успешных загрузок (для диагностики и тестов).</summary>
    public int LoadCount { get; private set; }

    public void Initialize(bool watch = true)
    {
        this.EnsureFileExists();
        this.Reload();
        if (watch)
        {
            this.StartWatching();
        }
    }

    /// <summary>Перечитать файл. Возвращает <c>false</c>, если оставлены прежние настройки.</summary>
    public bool Reload()
    {
        lock (this._sync)
        {
            string text;
            try
            {
                text = ReadAllTextShared(this._path);
            }
            catch (FileNotFoundException)
            {
                this._log.Warning($"{FileName} не найден; используются текущие настройки.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                this._log.Warning($"Не удалось прочитать {FileName}; используются текущие настройки.", ex);
                return false;
            }

            HapticSettings? raw;
            try
            {
                raw = JsonSerializer.Deserialize<HapticSettings>(text, SettingsJson.Options);
            }
            catch (JsonException ex)
            {
                this._log.Warning($"{FileName} содержит ошибку JSON (строка {ex.LineNumber + 1}); оставлена последняя рабочая конфигурация.");
                return false;
            }

            if (raw is null)
            {
                this._log.Warning($"{FileName} пуст; оставлена последняя рабочая конфигурация.");
                return false;
            }

            var result = SettingsValidator.Validate(raw);
            foreach (var warning in result.Warnings)
            {
                this._log.Warning($"Настройки: {warning}");
            }

            this._current = result.Settings;
            this.LoadCount++;
            this._log.Info(
                $"Настройки загружены: enabled={result.Settings.Enabled}, cooldown={result.Settings.NavigationCooldownMs} мс, " +
                $"debounce={result.Settings.NavigationDebounceMs} мс, guard={result.Settings.TransitionGuardMs} мс, log={result.Settings.LogLevel}.");
        }

        try
        {
            this.Changed?.Invoke(this._current);
        }
        catch (Exception ex)
        {
            this._log.Warning("Ошибка обработчика смены настроек.", ex);
        }

        return true;
    }

    /// <summary>Включить или выключить вибрацию, сохранив остальные поля файла.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (this._sync)
        {
            JsonNode? root = null;
            try
            {
                root = JsonNode.Parse(ReadAllTextShared(this._path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                this._log.Warning($"{FileName} нельзя разобрать; переключение выполнено только в памяти.");
            }

            if (root is JsonObject obj)
            {
                obj["enabled"] = enabled;
                this.WriteAtomically(obj.ToJsonString(SettingsJson.Options));
            }
        }

        // Watcher тоже перечитает файл, но применяем сразу, без ожидания.
        this.Reload();
    }

    public void Dispose()
    {
        lock (this._sync)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            if (this._watcher is not null)
            {
                this._watcher.EnableRaisingEvents = false;
                this._watcher.Dispose();
                this._watcher = null;
            }

            this._reloadTimer?.Dispose();
            this._reloadTimer = null;
        }
    }

    internal static string SerializeDefault() =>
        JsonSerializer.Serialize(HapticSettings.CreateDefault(), SettingsJson.Options);

    private void EnsureFileExists()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this._path)!);
        if (File.Exists(this._path))
        {
            return;
        }

        this.WriteAtomically(SerializeDefault());
        this._log.Info($"Создан файл настроек по умолчанию: {this._path}");
    }

    private void WriteAtomically(string json)
    {
        var temp = this._path + ".tmp";
        File.WriteAllText(temp, json + Environment.NewLine, Utf8NoBom);
        File.Move(temp, this._path, overwrite: true);
    }

    private void StartWatching()
    {
        lock (this._sync)
        {
            if (this._disposed || this._watcher is not null)
            {
                return;
            }

            this._reloadTimer = new Timer(_ => this.OnReloadTimer(), null, Timeout.Infinite, Timeout.Infinite);
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(this._path)!)
            {
                Filter = Path.GetFileName(this._path),
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += this.OnFileEvent;
            watcher.Created += this.OnFileEvent;
            watcher.Renamed += this.OnFileEvent;
            watcher.Error += (_, e) => this._log.Warning("Ошибка FileSystemWatcher настроек.", e.GetException());
            watcher.EnableRaisingEvents = true;
            this._watcher = watcher;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Редакторы сохраняют файл в несколько приёмов; ждём, пока запись утихнет.
        lock (this._sync)
        {
            if (!this._disposed)
            {
                this._reloadTimer?.Change(this._reloadDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnReloadTimer()
    {
        if (this._disposed)
        {
            return;
        }

        try
        {
            this.Reload();
        }
        catch (Exception ex)
        {
            this._log.Error("Не удалось перезагрузить настройки.", ex);
        }
    }

    private static string ReadAllTextShared(string path)
    {
        // Несколько попыток: редактор может удерживать файл в момент события watcher.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 4 && File.Exists(path))
            {
                Thread.Sleep(40);
            }
        }
    }
}
