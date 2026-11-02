# Настройка

Файл: `%LOCALAPPDATA%\KandoHaptics\settings.json` (например,
`C:\Users\<имя>\AppData\Local\KandoHaptics\settings.json`).

* Создаётся автоматически при первом запуске плагина; шаблон — `config/settings.json`.
* Редактируется любым редактором без прав администратора.
* Перечитывается автоматически примерно через 0,25 с после сохранения (FileSystemWatcher;
  поддерживается и сохранение через временный файл с переименованием).
* Допускаются комментарии `//` и висячие запятые.
* Если JSON повреждён, плагин пишет предупреждение в журнал и продолжает работать на
  последней корректной конфигурации.
* Недопустимые значения не ломают загрузку: числа приводятся к допустимому диапазону,
  неизвестный waveform заменяется эффектом по умолчанию для этого события, неизвестные
  события игнорируются. Каждое такое исправление записывается в журнал.

## Параметры

| Параметр | По умолчанию | Диапазон | Назначение |
|---|---|---|---|
| `enabled` | `true` | — | Главный выключатель. `false` — никаких вибраций (состояние меню всё равно отслеживается). |
| `navigationCooldownMs` | `70` | 0–2000 | Минимальный интервал между сигналами наведения. |
| `navigationDebounceMs` | `100` | 0–2000 | Окно, в котором возврат на только что прочувствованный сектор (A→B→A) не вибрирует. |
| `transitionGuardMs` | `90` | 0–1000 | После перехода или выбора столько миллисекунд не звучат менее важные эффекты. |
| `trailingHoverFeedback` | `true` | — | Если сигнал сектора отсечён cooldown, а курсор на нём остался — сыграть его по истечении интервала. |
| `logLevel` | `Info` | Debug, Info, Warning, Error | Подробность журнала плагина. |
| `events.<событие>.enabled` | см. ниже | — | Включить эффект для события. |
| `events.<событие>.waveform` | см. ниже | список ниже | Какой waveform играть. Если не указан — эффект по умолчанию для события. |

## События

| Событие | Когда | По умолчанию |
|---|---|---|
| `openMenu` | меню открылось | `damp_state_change`, включено |
| `hoverButton` | курсор на кнопке | `subtle_collision`, включено |
| `hoverSubmenu` | курсор на элементе-подменю | `subtle_collision`, включено |
| `openSubmenu` | вход в подменю | `sharp_state_change`, включено |
| `closeSubmenu` | возврат из подменю | `damp_collision`, включено |
| `selectButton` | выбрана команда | `completed`, включено |
| `hoverCenter` | курсор в центре | выключено (при включении — `subtle_collision`) |
| `hoverParent` | курсор на родительском элементе | выключено (`subtle_collision`) |
| `activateMenu` | активирован центр меню | выключено (`completed`) |
| `closeMenu` | меню закрылось | выключено (`damp_collision`) |
| `scrollUp`, `scrollDown` | прокрутка над элементом (есть в исходниках Kando, пока не в документации IPC) | выключено (`subtle_collision`) |

## Waveform

Официальные waveform Logitech (имена чувствительны к регистру):

| Группа | Waveform |
|---|---|
| State Change | `sharp_state_change`, `damp_state_change` |
| Collision | `sharp_collision`, `damp_collision`, `subtle_collision` |
| Alert | `happy_alert`, `angry_alert`, `completed` |
| Special | `square`, `wave`, `firework`, `mad`, `knock`, `jingle`, `ringing` |

Для частых событий (наведение) подходят только короткие `*_collision`. Группы Alert и
Special длинные и предназначены для редких событий — не ставьте их на наведение.

## Как плагин выбирает эффект

Пакет плагина объявляет 15 событий `waveform_<имя>` (`events/DefaultEventSource.yaml`) и
сопоставляет каждому одноимённый waveform (`events/extra/eventMapping.yaml`). Logi Plugin
Service читает eventMapping.yaml только при загрузке плагина, поэтому редактировать его на
лету бессмысленно. Вместо этого плагин при событии Kando вызывает
`PluginEvents.RaiseEvent("waveform_" + waveform)`, где waveform взят из settings.json.

Ограничения этого подхода:

* Сила вибрации задаётся в Logi Options+ для устройства; SDK не даёт программной
  регулировки амплитуды, и плагин её не меняет.
* Если Logi Options+ позволяет переназначить waveform для события плагина в своём
  интерфейсе, переназначение `waveform_subtle_collision` затронет все события Kando, где
  выбран `subtle_collision`. Предпочтительно настраивать эффекты в settings.json.

## Примеры

Тише — только переходы и выбор, без щелчков при наведении:

```json
{
  "events": {
    "hoverButton": { "enabled": false },
    "hoverSubmenu": { "enabled": false }
  }
}
```

Чётче — более заметный щелчок и реже при быстром движении:

```json
{
  "navigationCooldownMs": 110,
  "events": {
    "hoverButton": { "enabled": true, "waveform": "damp_collision" },
    "hoverSubmenu": { "enabled": true, "waveform": "damp_collision" }
  }
}
```

Временно выключить всё: `"enabled": false`. Это же поле переключает необязательная команда
«Toggle Kando Haptics» плагина, если назначить её на кнопку (при этом комментарии в файле
не сохраняются).
