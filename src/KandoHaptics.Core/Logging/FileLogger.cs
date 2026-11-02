namespace KandoHaptics.Logging;

using System.Globalization;
using System.Text;
using System.Threading.Channels;

/// <summary>
/// Асинхронный файловый логгер с ротацией по размеру.
/// Запись выполняется отдельной задачей, поэтому вызов <see cref="Log"/> не блокирует
/// обработку событий Kando. При переполнении очереди новые записи отбрасываются.
/// </summary>
public sealed class FileLogger : IKandoLogger, IAsyncDisposable, IDisposable
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string _filePath;
    private readonly long _maxFileBytes;
    private readonly int _maxArchivedFiles;
    private readonly Channel<string> _queue;
    private readonly Task _writerTask;
    private volatile LogLevel _minimumLevel;
    private int _disposed;

    public FileLogger(string filePath, LogLevel minimumLevel = LogLevel.Info, long maxFileBytes = 1024 * 1024, int maxArchivedFiles = 3)
    {
        this._filePath = filePath;
        this._minimumLevel = minimumLevel;
        this._maxFileBytes = Math.Max(16 * 1024, maxFileBytes);
        this._maxArchivedFiles = Math.Clamp(maxArchivedFiles, 1, 20);
        this._queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        this._writerTask = Task.Run(this.WriteLoopAsync);
    }

    public string FilePath => this._filePath;

    public LogLevel MinimumLevel
    {
        get => this._minimumLevel;
        set => this._minimumLevel = value;
    }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= this._minimumLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (!this.IsEnabled(level))
        {
            return;
        }

        var line = new StringBuilder(96 + message.Length)
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelName(level)).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            // Только тип и сообщение: стек нужен редко и раздувает журнал.
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        this._queue.Writer.TryWrite(line.ToString());
    }

    public void Dispose() => this.DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) != 0)
        {
            return;
        }

        this._queue.Writer.TryComplete();
        try
        {
            await this._writerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Не задерживаем выгрузку плагина из-за журнала.
        }
    }

    internal static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "Debug",
        LogLevel.Info => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        _ => "None",
    };

    private async Task WriteLoopAsync()
    {
        var reader = this._queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this._filePath)!);
                this.RotateIfNeeded();

                using var stream = new FileStream(this._filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, Utf8WithBom);
                var written = 0;
                while (written < 256 && reader.TryRead(out var line))
                {
                    writer.WriteLine(line);
                    written++;
                }
            }
            catch
            {
                // Диск недоступен или файл заблокирован: пропускаем пачку, чтобы не зациклиться.
                while (reader.TryRead(out _))
                {
                }

                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(this._filePath);
        if (!info.Exists || info.Length < this._maxFileBytes)
        {
            return;
        }

        var directory = info.DirectoryName!;
        var baseName = Path.GetFileNameWithoutExtension(this._filePath);
        var extension = Path.GetExtension(this._filePath);

        string Archive(int index) => Path.Combine(directory, $"{baseName}.{index}{extension}");

        var oldest = Archive(this._maxArchivedFiles);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = this._maxArchivedFiles - 1; i >= 1; i--)
        {
            var source = Archive(i);
            if (File.Exists(source))
            {
                File.Move(source, Archive(i + 1), overwrite: true);
            }
        }

        File.Move(this._filePath, Archive(1), overwrite: true);
    }
}
