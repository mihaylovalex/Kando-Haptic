namespace KandoHaptics.Ipc;

using System.Text.Json;

/// <summary>Тип взаимодействия из сообщения <c>menu-interaction</c> Kando IPC API v2.</summary>
public enum KandoInteraction
{
    Unknown = 0,
    OpenMenu,
    HoverButton,
    HoverSubmenu,
    HoverCenter,
    HoverParent,
    OpenSubmenu,
    CloseSubmenu,
    SelectButton,
    ActivateMenu,
    CloseMenu,

    /// <summary>Есть в исходниках Kando (main), но пока не описано в документации IPC.</summary>
    ScrollUp,

    /// <summary>Есть в исходниках Kando (main), но пока не описано в документации IPC.</summary>
    ScrollDown,
}

public static class KandoInteractionNames
{
    private static readonly Dictionary<string, KandoInteraction> ByWireName = new(StringComparer.Ordinal)
    {
        ["openMenu"] = KandoInteraction.OpenMenu,
        ["hoverButton"] = KandoInteraction.HoverButton,
        ["hoverSubmenu"] = KandoInteraction.HoverSubmenu,
        ["hoverCenter"] = KandoInteraction.HoverCenter,
        ["hoverParent"] = KandoInteraction.HoverParent,
        ["openSubmenu"] = KandoInteraction.OpenSubmenu,
        ["closeSubmenu"] = KandoInteraction.CloseSubmenu,
        ["selectButton"] = KandoInteraction.SelectButton,
        ["activateMenu"] = KandoInteraction.ActivateMenu,
        ["closeMenu"] = KandoInteraction.CloseMenu,
        ["scrollUp"] = KandoInteraction.ScrollUp,
        ["scrollDown"] = KandoInteraction.ScrollDown,
    };

    private static readonly Dictionary<KandoInteraction, string> ByInteraction =
        ByWireName.ToDictionary(pair => pair.Value, pair => pair.Key);

    /// <summary>Все известные имена в формате протокола (camelCase).</summary>
    public static IReadOnlyCollection<string> All => ByWireName.Keys;

    public static KandoInteraction Parse(string? wireName) =>
        wireName is not null && ByWireName.TryGetValue(wireName, out var value) ? value : KandoInteraction.Unknown;

    /// <summary>Для settings.json: допускаем «HoverButton» вместо «hoverButton».</summary>
    public static KandoInteraction ParseIgnoreCase(string? name)
    {
        if (name is null)
        {
            return KandoInteraction.Unknown;
        }

        foreach (var (wireName, value) in ByWireName)
        {
            if (string.Equals(wireName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return KandoInteraction.Unknown;
    }

    public static string ToWireName(this KandoInteraction interaction) =>
        ByInteraction.TryGetValue(interaction, out var name) ? name : "unknown";

    public static bool IsHover(this KandoInteraction interaction) => interaction is
        KandoInteraction.HoverButton or KandoInteraction.HoverSubmenu or
        KandoInteraction.HoverCenter or KandoInteraction.HoverParent;
}

/// <summary>Нормализованное событие взаимодействия с меню.</summary>
public sealed record MenuInteractionEvent(KandoInteraction Interaction, string RawInteraction, IReadOnlyList<int> Path)
{
    public static MenuInteractionEvent Create(KandoInteraction interaction, params int[] path) =>
        new(interaction, interaction.ToWireName(), path);

    public string PathText => FormatPath(this.Path);

    public static string FormatPath(IReadOnlyList<int> path) => "[" + string.Join(",", path) + "]";

    public override string ToString() => $"{this.RawInteraction} {this.PathText}";
}

public enum KandoMessageKind
{
    /// <summary>Сообщение не является корректным JSON-объектом.</summary>
    Invalid,

    /// <summary>Корректный JSON неизвестного или неподдерживаемого типа.</summary>
    Unsupported,
    MenuInteraction,
    Error,
}

public readonly record struct KandoMessage(
    KandoMessageKind Kind,
    string? Type,
    MenuInteractionEvent? Interaction,
    string? ErrorReason,
    string? ErrorDescription);

/// <summary>
/// Безопасный разбор входящих сообщений: только <see cref="JsonDocument"/>, без
/// полиморфной десериализации, с ограничением глубины и длины пути.
/// </summary>
public static class KandoMessageParser
{
    public const int MaxPathLength = 32;

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 16 };

    public static KandoMessage Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json, Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return new KandoMessage(KandoMessageKind.Invalid, null, null, null, null);
            }

            var type = typeElement.GetString();
            switch (type)
            {
                case "menu-interaction":
                    return ParseInteraction(root, type);
                case "error":
                    return new KandoMessage(
                        KandoMessageKind.Error,
                        type,
                        null,
                        TryGetString(root, "reason"),
                        TryGetString(root, "description"));
                default:
                    return new KandoMessage(KandoMessageKind.Unsupported, type, null, null, null);
            }
        }
        catch (JsonException)
        {
            return new KandoMessage(KandoMessageKind.Invalid, null, null, null, null);
        }
    }

    public static KandoMessage Parse(string json) => Parse(System.Text.Encoding.UTF8.GetBytes(json));

    private static KandoMessage ParseInteraction(JsonElement root, string type)
    {
        var raw = TryGetString(root, "interaction");
        if (raw is null)
        {
            return new KandoMessage(KandoMessageKind.Invalid, type, null, null, null);
        }

        var path = new List<int>();
        if (root.TryGetProperty("path", out var pathElement))
        {
            if (pathElement.ValueKind != JsonValueKind.Array)
            {
                return new KandoMessage(KandoMessageKind.Invalid, type, null, null, null);
            }

            foreach (var item in pathElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index) || index < 0 || path.Count >= MaxPathLength)
                {
                    return new KandoMessage(KandoMessageKind.Invalid, type, null, null, null);
                }

                path.Add(index);
            }
        }

        var interaction = new MenuInteractionEvent(KandoInteractionNames.Parse(raw), raw, path.ToArray());
        return new KandoMessage(KandoMessageKind.MenuInteraction, type, interaction, null, null);
    }

    private static string? TryGetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
