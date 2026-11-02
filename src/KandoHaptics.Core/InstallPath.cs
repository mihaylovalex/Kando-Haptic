namespace KandoHaptics;

/// <summary>
/// Проверка каталога установки. Logi Plugin Service передаёт Logi Options+ путь к
/// events\extra, и Options+ (проверено на 2.9.4631 / LPS 6.4.2.3414) не читает
/// eventMapping.yaml, если в пути есть не-ASCII символы, например кириллическое имя
/// пользователя в %LOCALAPPDATA%. События при этом доходят, но вибрации нет.
/// </summary>
public static class InstallPath
{
    public static bool IsAscii(string path)
    {
        foreach (var c in path)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }
}
