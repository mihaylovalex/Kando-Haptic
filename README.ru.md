<p align="center"><img src="docs/images/icon.png" width="128" alt="Kando Haptics"></p>

# Kando Haptics

[English](README.md) · **Русский**

Плагин Logi Actions SDK, который добавляет тактильную отдачу **Logitech MX Master 4**
к круговому меню [Kando](https://kando.menu/). Открываете своё обычное меню Kando —
и мышь отвечает лёгкими щелчками при наведении на сектора, отдельным импульсом при входе
в подменю и подтверждением при выборе команды.

* Работает внутри Logi Plugin Service: отдельного EXE и фоновых процессов нет.
* Kando обнаруживается автоматически через `%APPDATA%\kando\ipc-info.json`, порт не фиксирован.
* Используются только официальные API: Kando WebSocket IPC v2 и `PluginEvents.RaiseEvent()`
  Logi Actions SDK (`HasHapticMapping`). Никакого HID++, внедрения в процессы и изменения тем Kando.
* Эффекты, cooldown и включение событий настраиваются в `settings.json` и применяются на лету.
* Назначать действия плагина на кнопки мыши **не нужно**.

## Как это работает

```text
Kando Menu ──WebSocket IPC v2──▶ KandoIpcClient ──MenuInteractionEvent──▶ очередь
     ▲   ws://127.0.0.1:<port из ipc-info.json>                            │
     │                                                                     ▼
     │                                                     InteractionProcessor
     │                                       состояние меню · дубликаты · cooldown
     │                                       дрожание на границе · приоритеты
     │                                                                     │
     │                                                       HapticEngine (изоляция ошибок SDK)
     │                                                                     │
MX Master 4 ◀── Logi Plugin Service ◀── PluginEvents.RaiseEvent("waveform_<имя>") ◀── LogitechHapticProvider
```

Сеть, обработка событий и вызовы SDK разделены: приём WebSocket никогда не ждёт обработки,
сбой одного события или вызова SDK не останавливает плагин.

### Обработка событий

| Событие Kando  | Эффект по умолчанию  | Включено | Приоритет |
|----------------|----------------------|:--------:|-----------|
| `openMenu`     | `damp_state_change`  | да       | переход   |
| `hoverButton`  | `subtle_collision`   | да       | навигация |
| `hoverSubmenu` | `subtle_collision`   | да       | навигация |
| `openSubmenu`  | `sharp_state_change` | да       | переход   |
| `closeSubmenu` | `damp_collision`     | да       | переход   |
| `selectButton` | `completed`          | да       | выбор     |
| `hoverCenter`  | —                    | нет      | навигация |
| `hoverParent`  | —                    | нет      | навигация |
| `activateMenu` | —                    | нет      | выбор     |
| `closeMenu`    | —                    | нет      | переход   |
| `scrollUp` / `scrollDown` | —         | нет      | навигация (есть в коде Kando, ещё не в документации IPC) |

Правила, которые делают отклик сдержанным:

* **Дедупликация** — повторный `hover` того же элемента (тот же тип и `path`) не вибрирует.
* **Cooldown 70 мс** между сигналами наведения. Если сектор отсечён cooldown, а курсор на нём
  остался, сигнал прозвучит, как только интервал истечёт (`trailingHoverFeedback`) — задержка
  не больше 70 мс и только при очень быстром движении; обычное наведение играется мгновенно.
* **Debounce дрожания** — возврат A→B→A на границе соседних секторов в пределах 100 мс не
  даёт серии щелчков.
* **Приоритеты** — после перехода (`openSubmenu`, `closeSubmenu`, `openMenu`) или выбора 90 мс
  не звучат менее важные эффекты: открытие подменю и наведение на дочерний элемент, которые
  Kando присылает одновременно, дают один импульс. `selectButton` не блокируется ничем,
  кроме выключения в настройках.
* **Состояние меню** — `openMenu` начинает новый сеанс (прежний путь снова может вибрировать),
  `closeMenu` очищает состояние и отменяет отложенные сигналы; глубина и путь подменю
  отслеживаются по `openSubmenu`/`closeSubmenu` и выравниваются по путям наведения, если
  какое-то событие было пропущено.
* `activateMenu` трактуется только как событие Kando, а не как подтверждение того, что
  внешняя команда выполнилась успешно.

### Почему 15 событий `waveform_*`

Logi Plugin Service читает `events/extra/eventMapping.yaml` при загрузке плагина, поэтому
менять сопоставление «событие → waveform» на лету нельзя. Плагин заранее регистрирует по
одному событию на каждый официальный waveform (`waveform_subtle_collision`,
`waveform_completed`, …) и при событии Kando вызывает то, что выбрано в `settings.json`.
Так эффекты меняются без пересборки и перезапуска.

## Быстрый старт

Требования: Windows 11 x64, Logi Options+ с Logi Plugin Service 6.x, Kando 3.0+, MX Master 4.

1. Установите пакет `dist\KandoHaptics_1_0_0.lplug4` (двойной щелчок) или командой:
   ```bash
   dotnet tool run logiplugintool install dist\KandoHaptics_1_0_0.lplug4
   ```
2. Убедитесь, что Kando запущен. Плагин подключится сам в течение 1–10 секунд.
3. Откройте меню Kando и проведите курсором по секторам.

> **Если имя пользователя Windows написано кириллицей**, Logi Options+ не прочитает
> таблицу вибраций плагина из профиля пользователя, и мышь не будет вибрировать.
> Подключите плагин из каталога без кириллицы:
> `powershell -ExecutionPolicy Bypass -File build\build.ps1 -LinkTo C:\ProgramData\KandoHaptics`.

Подробности: [docs/INSTALLATION.md](docs/INSTALLATION.md),
настройки: [docs/CONFIGURATION.md](docs/CONFIGURATION.md),
проблемы и ручная проверка: [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## Настройки

`%LOCALAPPDATA%\KandoHaptics\settings.json` создаётся при первом запуске
(шаблон — [config/settings.json](config/settings.json)). Права администратора не нужны,
изменения применяются через ~0,25 с после сохранения файла. При ошибке в JSON остаётся
последняя рабочая конфигурация. Полностью выключить вибрацию: `"enabled": false`.

```json
{
  "enabled": true,
  "navigationCooldownMs": 70,
  "navigationDebounceMs": 100,
  "transitionGuardMs": 90,
  "trailingHoverFeedback": true,
  "logLevel": "Info",
  "events": {
    "hoverButton": { "enabled": true, "waveform": "subtle_collision" }
  }
}
```

Сила вибрации регулируется в Logi Options+ (настройки устройства). Официальный SDK не
предоставляет программной регулировки амплитуды, поэтому плагин её не меняет.

## Сборка из исходников

Нужны .NET 10 SDK и установленный Logi Options+ (ссылка на `PluginApi.dll` берётся из
`C:\Program Files\Logi\LogiPluginService`, путь можно переопределить переменной `LOGI_PLUGIN_API_DIR`).

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

Держите репозиторий по короткому пути (например, `C:\src\KandoHaptics`): при длинном пути
сборка тестов может упереться в ограничение Windows в 260 символов.

Скрипт проверяет .NET SDK и зависимости, собирает Release, запускает тесты, упаковывает
`.lplug4` через официальный LogiPluginTool (`pack` + `verify`), проверяет содержимое
архива и кладёт результат в `dist\`. Варианты: `-Configuration Debug`, `-SkipTests`,
`-Install`, `-Uninstall`.

Только тесты (мышь и Logi Options+ не нужны):

```bash
dotnet test tests\KandoHaptics.Tests\KandoHaptics.Tests.csproj
```

Режим разработчика, как в официальном шаблоне (создаёт `.link` в каталоге плагинов Logi
и перезагружает плагин):

```bash
dotnet build src\KandoHaptics.Plugin\KandoHaptics.Plugin.csproj -p:KandoDevLink=true
```

## Структура

```text
KandoHaptics.sln
src/KandoHaptics.Core/          ядро без зависимостей от Logi SDK (полностью тестируется)
  Ipc/                          ipc-info.json, разбор сообщений, WebSocket-клиент
  Processing/                   состояние меню и фильтрация событий
  Haptics/                      каталог waveform, IHapticProvider, HapticEngine
  Configuration/                settings.json: модель, проверка, горячая перезагрузка
  Logging/                      файловый журнал с ротацией
  KandoHapticsRuntime.cs        сборка компонентов, очередь, диспетчер
src/KandoHaptics.Plugin/        плагин Logi Actions SDK
  KandoHapticsPlugin.cs         Load/Unload
  KandoHapticsApplication.cs    обязательный ClientApplication (без него сервис не грузит плагин)
  Haptics/LogitechHapticProvider.cs   AddEvent/RaiseEvent
  Actions/                      необязательные команды Toggle/Test
  package/                      metadata/LoupedeckPackage.yaml, events/*.yaml, иконка
tests/KandoHaptics.Tests/       xUnit: IPC (с локальным WebSocket-сервером), обработка,
                                движок, конфигурация, журнал
config/settings.json            рекомендуемые настройки
build/build.ps1                 сборка, тесты, упаковка
docs/                           установка, настройка, диагностика
```

## Журналы

* `%LOCALAPPDATA%\KandoHaptics\Logs\KandoHaptics.log` — журнал плагина (ротация по 1 МБ,
  хранится 3 архива). Каждое событие Kando и решение по нему видно при `"logLevel": "Debug"`.
* `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\KandoHaptics.log` — записи Info и
  выше в журнале Logi Plugin Service.

В журнал пишутся только тип события и числовой `path`; названия пунктов меню и команды
Kando не записываются (плагин их и не получает).

## Безопасность

* Подключение только к `127.0.0.1`; хост из файла не читается, системный прокси отключён.
* Плагин не открывает сетевых портов и не выполняет ничего из входящих сообщений.
* Разбор JSON — `JsonDocument` с ограничением глубины и размера сообщения (64 КБ), без
  полиморфной десериализации.

## Лицензия

MIT, см. [LICENSE](LICENSE).
