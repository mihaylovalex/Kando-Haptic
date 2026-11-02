# Установка, обновление и удаление

## Требования

| Компонент | Версия |
|---|---|
| Windows | 11 x64 |
| Logi Options+ / Logi Plugin Service | 6.x (проверено с 6.4.2.3414; сервис работает на .NET 10) |
| Kando | 3.0 или новее (IPC API v2) |
| Мышь | Logitech MX Master 4, видна в Logi Options+ |

Для сборки из исходников дополнительно: .NET 10 SDK.

## Важно: путь установки без кириллицы

Logi Plugin Service передаёт Logi Options+ не сами сопоставления waveform, а путь к
папке плагина `events\extra`, и Options+ читает `eventMapping.yaml` оттуда сам. Если в
этом пути есть не-ASCII символы (например, кириллическое имя пользователя:
`C:\Users\Иван\AppData\Local\...`), Options+ файл не читает. Плагин при этом
работает, события Kando приходят, `RaiseEvent` вызывается без ошибок — но мышь не
вибрирует. Проверено на Logi Options+ 2.9.4631 и Logi Plugin Service 6.4.2.3414.

Плагин сам пишет предупреждение в журнал, если загружен из такого каталога.

Если имя вашего пользователя Windows не латиницей, подключайте плагин из каталога без
кириллицы:

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1 -LinkTo C:\ProgramData\KandoHaptics
```

Скрипт соберёт плагин, скопирует его в `C:\ProgramData\KandoHaptics`, создаст
`%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\KandoHaptics.link` и перезагрузит плагин.
Сам `.link` может лежать в профиле с кириллицей — важен только путь, на который он указывает.
Если вибрации нет сразу после первого подключения, перезапустите Logi Options+, чтобы
Options+ заново получил описание источника событий с новым путём.

Установка в `C:\Program Files\Logi\LogiPluginService\Plugins` (путь ASCII) тоже работает,
но требует прав администратора.

## Установка готового пакета

Вариант 1 — двойной щелчок по `KandoHaptics_1_0_0.lplug4`. Формат зарегистрирован Logi
Plugin Service, откроется установщик.

Вариант 2 — официальный Logi Plugin Tool (из каталога репозитория, где есть манифест
`.config/dotnet-tools.json`). На проверенной машине `logiplugintool` 6.4.2.3599 с NuGet не
смог подключиться к сервису 6.4.2.3414 («Cannot connect to Logi Plugin Service»); тогда
используйте вариант 1 или 3 — скрипт вызывает установленный `LogiPluginServiceTool.exe`:

```bash
dotnet tool restore
```

```bash
dotnet tool run logiplugintool install dist\KandoHaptics_1_0_0.lplug4
```

Вариант 3 — скрипт сборки сразу соберёт и установит:

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1 -Install
```

Установщик может запросить подтверждение UAC: сервис кладёт плагины в
`C:\Program Files\Logi\LogiPluginService\Plugins`.

После установки ничего назначать на кнопки не нужно. Плагин загружается вместе с Logi
Plugin Service, создаёт `%LOCALAPPDATA%\KandoHaptics\settings.json` и ждёт Kando.

### Если установка завершается ошибкой «Install plugin method failed»

Так бывает, если в `C:\Program Files\Logi\LogiPluginService\Plugins\KandoHaptics` уже лежит
копия плагина, положенная туда с правами администратора: сервис работает от имени
пользователя и не может её заменить. Удалите эту папку из PowerShell, запущенного от имени
администратора, перезапустите Logi Options+ и установите пакет снова:

```powershell
Remove-Item "C:\Program Files\Logi\LogiPluginService\Plugins\KandoHaptics" -Recurse
```

Если неудачная установка внесла плагин в список отключённых, включите его в Logi Options+
или используйте подключение через `.link` (см. «Режим разработчика» ниже).

## Проверка, что плагин загружен

1. Откройте `%LOCALAPPDATA%\KandoHaptics\Logs\KandoHaptics.log`. Должны быть строки:
   ```text
   [Info] Загрузка плагина Kando Haptics 1.0.0.0.
   [Info] Haptic-события (15) объявлены в DefaultEventSource.yaml.
   [Info] Подключено к Kando IPC v2: ws://127.0.0.1:<порт>, start-observing отправлен.
   ```
2. Откройте меню Kando — мышь должна мягко отозваться на открытие.
3. Для подробной трассировки поставьте `"logLevel": "Debug"` в settings.json: в журнале
   появится каждое событие и решение (`Played`, `Duplicate`, `Deferred`, `Guarded`…).

## Обновление

Установите новый `.lplug4` поверх старого (двойной щелчок или `logiplugintool install`).
`settings.json` хранится отдельно в `%LOCALAPPDATA%\KandoHaptics` и при обновлении не
перезаписывается; новые параметры, которых нет в вашем файле, берут значения по умолчанию.

## Удаление

```bash
dotnet tool run logiplugintool uninstall KandoHaptics
```

или `build\build.ps1 -Uninstall`, или через список плагинов в Logi Options+.
Если плагин подключён через `.link`, удалите `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\KandoHaptics.link`
и каталог, на который он указывает (например, `C:\ProgramData\KandoHaptics`), затем
перезапустите Logi Options+.
Настройки и журналы остаются в `%LOCALAPPDATA%\KandoHaptics` — удалите каталог вручную,
если они больше не нужны.

## Режим разработчика

Как в официальном шаблоне Logi Actions SDK, сборку можно подключить к сервису ссылкой:

```bash
dotnet build src\KandoHaptics.Plugin\KandoHaptics.Plugin.csproj -p:KandoDevLink=true
```

Будет создан `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\KandoHaptics.link`, указывающий
на `artifacts\plugin\Debug\`, и отправлена команда `loupedeck:plugin/KandoHaptics/reload`.
Не держите одновременно `.link` и установленный пакет с одним именем: удалите `.link`
перед установкой `.lplug4`.

Тот же механизм работает и без исходников: скопируйте содержимое
`artifacts\plugin\Release\` (или распакованного `.lplug4`) в постоянный каталог, например
`%LOCALAPPDATA%\KandoHaptics\plugin\`, запишите путь к нему (с завершающим `\`) в
`%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\KandoHaptics.link` и выполните
`start loupedeck:plugin/KandoHaptics/reload`. Права администратора не нужны.
