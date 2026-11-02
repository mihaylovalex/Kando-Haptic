namespace KandoHaptics.Ipc;

using System.Text.Json;

/// <summary>Содержимое <c>ipc-info.json</c>, которое Kando записывает при запуске.</summary>
public sealed record KandoIpcInfo(int Port, int ApiVersion)
{
    /// <summary>Версия IPC API, которую понимает плагин (Kando 3.0+).</summary>
    public const int SupportedApiVersion = 2;

    public const string FileName = "ipc-info.json";

    /// <summary><c>%APPDATA%\kando\ipc-info.json</c>.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kando", FileName);

    /// <summary>Адрес всегда строится на loopback; хост из файла не читается.</summary>
    public Uri WebSocketUri => new($"ws://127.0.0.1:{this.Port}");
}

public enum IpcInfoStatus
{
    Ok,
    NotFound,
    InvalidJson,
    InvalidPort,
    MissingApiVersion,
    UnsupportedApiVersion,
    AccessError,
}

public readonly record struct IpcInfoReadResult(IpcInfoStatus Status, KandoIpcInfo? Info, string? Detail)
{
    public bool IsOk => this.Status == IpcInfoStatus.Ok;
}

public static class KandoIpcInfoReader
{
    private const int MaxFileBytes = 64 * 1024;

    public static IpcInfoReadResult Read(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return new IpcInfoReadResult(IpcInfoStatus.NotFound, null, path);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes)
            {
                return new IpcInfoReadResult(IpcInfoStatus.InvalidJson, null, "file is too large");
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException)
        {
            return new IpcInfoReadResult(IpcInfoStatus.NotFound, null, path);
        }
        catch (DirectoryNotFoundException)
        {
            return new IpcInfoReadResult(IpcInfoStatus.NotFound, null, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IpcInfoReadResult(IpcInfoStatus.AccessError, null, ex.Message);
        }

        return Parse(bytes);
    }

    public static IpcInfoReadResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        // Kando пишет UTF-8; на всякий случай отрезаем BOM.
        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8Json = utf8Json[3..];
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new IpcInfoReadResult(IpcInfoStatus.InvalidJson, null, "root is not an object");
            }

            if (!root.TryGetProperty("port", out var portElement) ||
                portElement.ValueKind != JsonValueKind.Number ||
                !portElement.TryGetInt32(out var port) ||
                port is < 1 or > 65535)
            {
                return new IpcInfoReadResult(IpcInfoStatus.InvalidPort, null, "port is missing or out of range");
            }

            if (!root.TryGetProperty("apiVersion", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var apiVersion))
            {
                return new IpcInfoReadResult(IpcInfoStatus.MissingApiVersion, null, "apiVersion is missing");
            }

            var info = new KandoIpcInfo(port, apiVersion);
            return apiVersion == KandoIpcInfo.SupportedApiVersion
                ? new IpcInfoReadResult(IpcInfoStatus.Ok, info, null)
                : new IpcInfoReadResult(IpcInfoStatus.UnsupportedApiVersion, info, $"apiVersion {apiVersion}, supported {KandoIpcInfo.SupportedApiVersion}");
        }
        catch (JsonException ex)
        {
            return new IpcInfoReadResult(IpcInfoStatus.InvalidJson, null, ex.Message);
        }
    }
}
