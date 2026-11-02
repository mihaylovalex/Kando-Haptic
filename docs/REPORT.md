# Отчёт о разработке — Kando Haptics 1.0.0

Дата: 2026-11-02. Машина: Windows 11 Pro 10.0.22631 x64.

## Среда

| Компонент | Версия |
|---|---|
| .NET SDK | 10.0.100 |
| Logi Options+ | 2.9.4631 |
| Logi Plugin Service | 6.4.2.3414, runtime `.NETCoreApp,Version=v10.0` |
| PluginApi.dll | 6.4.2.3414 |
| LogiPluginTool | 6.4.2.3599 (NuGet, локальный dotnet tool) |
| Kando | 3.0.0-beta1, IPC API v2 |
| Мышь | Logitech MX Master 4 (Bluetooth) |

## Исследование SDK и протокола

* Сигнатуры проверены рефлексией по установленному `PluginApi.dll`:
  `Plugin.PluginEvents` → `PluginEventSender` с методами
  `AddEvent(String eventName, String displayName, String description)`,
  `RaiseEvent(String eventName)`, `RaiseEvent(String eventSourceName, String eventName)`;
  `Plugin.Log` → `PluginLogFile` (`Info`, `Warning`, `Error`, `Verbose`).
  Методов управления амплитудой в API нет.
* Схема сборки (net10.0, `PluginApi.dll` с `Private=false`, `package/` рядом с `bin/`,
  `pluginFolderWin: bin`) взята из официального `Logitech/actions-sdk/HapticsPlugin`.
* Протокол Kando — по https://kando.menu/ipc-interface/. Семантика `path` уточнена по
  исходникам Kando (`src/menu-renderer/menu.ts`): `closeSubmenu` несёт путь закрываемого
  подменю, `hoverParent` — путь родителя, `hoverCenter` — путь текущего центра. В main-ветке
  Kando есть ещё `scrollUp`/`scrollDown` — поддержаны как опциональные (выключены).

## Что реализовано

* Плагин Logi Actions SDK (`KandoHapticsPlugin.dll`) без внешнего EXE: `Load` запускает
  фоновые компоненты, `Unload` останавливает их (с ожиданием до 8 с).
* `KandoIpcClient`: поиск `ipc-info.json`, проверка JSON/порта/`apiVersion`, подключение
  только к `ws://127.0.0.1:{port}` (прокси отключён), `start-observing`, сборка фрагментов,
  ограничение 64 КБ на сообщение, обработка `error`, игнор неизвестных типов, корректное
  закрытие (`stop-observing` + close frame), переподключение 1→2→4→8→10 с, наблюдение за
  `ipc-info.json`, журналирование смены порта и неподдерживаемой версии.
* `InteractionProcessor` + `MenuInteractionState`: сеансы меню, путь и глубина, дедупликация,
  cooldown с отложенным сигналом, подавление дрожания, приоритеты, нормализация пропущенных
  событий, неявный сеанс при подключении к уже открытому меню.
* `HapticEngine` + `LogitechHapticProvider`: проверка waveform, изоляция и учёт ошибок SDK.
* Каталог из 15 событий `waveform_*` в YAML для смены эффектов без пересборки.
* `SettingsManager`: создание `settings.json`, проверка значений, горячая перезагрузка,
  сохранение последней рабочей конфигурации, безопасное обновление.
* `FileLogger` (Debug/Info/Warning/Error, асинхронная запись, ротация 1 МБ × 3) и
  дублирование Info+ в журнал Logi Plugin Service.
* Предупреждение в журнале, если плагин загружен из каталога с не-ASCII символами.
* Необязательные действия «Toggle Kando Haptics» и «Test Kando Haptic».
* `build/build.ps1` (сборка, тесты, `.lplug4`, `-Install`, `-LinkTo`), документация.

## Автоматическая проверка

`build\build.ps1` (Release): сборка с `-warnaserror` — 0 предупреждений, 0 ошибок;
**111 тестов xUnit пройдены**, повторные прогоны подряд — без сбоев.

| Набор | Что покрыто |
|---|---|
| IPC (`IpcTests.cs`) | чтение `ipc-info.json`, отсутствие файла/каталога, невалидный JSON, неверный порт, неподдерживаемая и отсутствующая версия API, BOM; разбор всех 10 событий, неизвестные события и типы, некорректные пути; на локальном WebSocket-сервере: подключение и `start-observing`, приём событий, фрагментированные и слишком большие сообщения, обрыв и корректное закрытие сервером, переподключение, переход на новый порт, ожидание запуска Kando, неподдерживаемая версия, корректная остановка, исключение в обработчике; сквозной тест Kando → `MockHapticProvider` |
| Обработка (`InteractionProcessorTests.cs`) | все сценарии ТЗ, а также быстрый проход, дрожание на границе, конкурирующие эффекты, нормализация глубины, сбой провайдера, реальная последовательность из журнала Kando |
| Движок (`HapticEngineTests.cs`) | вызов провайдера, неизвестный waveform, исключения SDK, каталог waveform, правила имён событий, согласованность YAML-файлов пакета с кодом, наличие `ClientApplication`, проверка пути установки |
| Конфигурация (`ConfigurationTests.cs`) | первый запуск, пользовательские настройки, невалидный waveform, границы значений, повреждённый JSON, перезагрузка без перезапуска (в т.ч. атомарная замена файла), отключение всех эффектов, обновление без перезаписи, `SetEnabled`, соответствие `config/settings.json` умолчаниям; ротация и уровни журнала |

`logiplugintool pack` + `verify` + `metadata` — пакет корректен (`isUniversalPlugin: true`,
`isWindowsSupported: true`).

## Проверка в живом Logi Plugin Service и на MX Master 4

* **Вибрация на MX Master 4 работает** при реальной работе с меню Kando. Журнал плагина в
  Debug показал полный поток: `openMenu → Played`, наведения на сектора, `openSubmenu` с
  подавлением одновременного наведения (`Guarded`), `closeSubmenu`.
* Журнал сервиса подтверждает загрузку: `Plugin 'KandoHaptics' version '1.0.0' loaded`,
  `Event source added … with 15 events and capabilities 'HasHapticMapping'`.

Проблемы, найденные только в живом сервисе, и их решения:

1. **Плагин без `ClientApplication` не загружается.** Сервис отвечал «Cannot load plugin
   from …KandoHapticsPlugin.dll» и вносил плагин в список отключённых. Добавлен
   `KandoHapticsApplication` (как `HapticsApplication.cs` в официальном примере) и тест.
2. **Двойное объявление событий.** При объявлении событий в YAML и вызове `AddEvent` сервис
   регистрировал 30 событий вместо 15. Теперь события берутся из `DefaultEventSource.yaml`,
   `AddEvent` вызывается, только если YAML отсутствует.
3. **Нет вибрации, если путь к плагину содержит не-ASCII символы.** Сервис передаёт
   Logi Options+ не сопоставления waveform, а `pluginDataDirectoryPath` (каталог
   `events\extra`), и Options+ читает `eventMapping.yaml` сам — это видно в
   `Logs\messages\eventSources\…EventSourceArrived` при включённом журнале сервиса.
   Из каталога в профиле пользователя с кириллическим именем события доходили до сервиса,
   но мышь не вибрировала; из `C:\ProgramData\KandoHaptics\` — вибрирует. Код
   `EventSourceManager.RaisePluginEvent` проверен по IL: условий, отсекающих события, нет.
   Решение — подключать плагин из каталога без не-ASCII символов
   (`build\build.ps1 -LinkTo C:\ProgramData\KandoHaptics`); плагин предупреждает в журнале.
4. **`logiplugintool install` 6.4.2.3599 не подключается к сервису 6.4.2.3414**
   («Cannot connect to Logi Plugin Service»). `build.ps1 -Install` использует установленный
   `LogiPluginServiceTool.exe install-plugin` — тот же обработчик, что и двойной щелчок.
5. **Установщик не может заменить копию плагина в Program Files**, положенную туда с правами
   администратора: сервис работает от имени пользователя и отвечает `FunctionFailed`
   («Install plugin method failed»). Такую копию нужно удалить вручную от администратора.

## Что не проверено

* Ручной чек-лист из `docs/TROUBLESHOOTING.md` целиком (быстрый проход, перезапуски Kando и
  Logi Options+, выключение через настройки).
* Установка `.lplug4` на чистую машину с ASCII-именем пользователя.
* Отображение 15 событий `waveform_*` в интерфейсе Logi Options+ и их переназначение там.
* `scrollUp`/`scrollDown` — только тестами: в выпущенном Kando их может не быть.
* Другие устройства с тактильной отдачей, кроме MX Master 4.

## Ограничения

* Logi Options+ не читает `eventMapping.yaml` из каталога с не-ASCII символами в пути.
* Амплитуду программно менять нельзя — в SDK нет такого API; сила задаётся в Logi Options+.
* `eventMapping.yaml` читается при загрузке плагина; динамика реализована выбором одного из
  15 заранее зарегистрированных событий.
* Если в Options+ переназначить waveform события `waveform_X`, это затронет все события
  Kando, использующие `X`.
* Команда «Toggle Kando Haptics» перезаписывает settings.json через JSON-модель —
  комментарии в файле при этом теряются.
* Только Windows (`pluginFolderWin`); путь к `ipc-info.json` на macOS не реализован.
* Если Kando оставит устаревший `ipc-info.json`, плагин пытается подключиться раз в 10 с —
  это лишь отказ в соединении на loopback, нагрузки нет.
