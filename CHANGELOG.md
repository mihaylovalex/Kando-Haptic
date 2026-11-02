# Changelog

## 1.0.0 — 2026-11-02

Первая версия.

* Плагин Logi Actions SDK (`plugin4`, `HasHapticMapping`) для Logi Plugin Service 6.x / .NET 10.
* Автоматическое обнаружение Kando через `%APPDATA%\kando\ipc-info.json`, подключение к
  WebSocket IPC API v2 на 127.0.0.1, `start-observing`, сборка фрагментированных сообщений.
* Переподключение с экспоненциальной задержкой 1–10 с, отслеживание смены порта,
  мгновенная реакция на появление `ipc-info.json`, диагностика неподдерживаемой версии API.
* Обработка всех документированных событий (`openMenu`, `hoverButton`, `hoverSubmenu`,
  `hoverCenter`, `hoverParent`, `openSubmenu`, `closeSubmenu`, `selectButton`,
  `activateMenu`, `closeMenu`) и опциональных `scrollUp`/`scrollDown`.
* Дедупликация, cooldown наведения с отложенным сигналом, подавление дрожания на границе
  секторов, приоритеты эффектов, отслеживание сеанса, глубины и пути меню.
* Каталог из 15 событий `waveform_*` для смены эффектов без пересборки.
* `settings.json` с проверкой значений, горячей перезагрузкой и сохранением последней
  рабочей конфигурации.
* Журнал с ротацией (1 МБ × 3 архива) и дублированием важных записей в журнал сервиса.
* Необязательные команды «Toggle Kando Haptics» и «Test Kando Haptic».
* Предупреждение в журнале при установке в каталог с не-ASCII символами (Options+ не читает
  оттуда eventMapping.yaml); параметр `build.ps1 -LinkTo`; `-Install` использует
  установленный `LogiPluginServiceTool.exe`.
* 111 автоматических тестов (xUnit), скрипт `build/build.ps1` с упаковкой через LogiPluginTool.
* Класс `KandoHapticsApplication : ClientApplication` — без него Logi Plugin Service 6.4 не
  загружает плагин. События объявляются только в YAML (без дублей через `AddEvent`).
