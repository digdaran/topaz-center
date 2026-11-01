# Агент АЗС: установка

**Требования:** Windows 10/11 или Server 2016+ x64 (для 32-битных систем есть сборка `win-x86`). **Windows 7 не поддерживается** — это ограничение .NET 8. Устанавливать .NET не нужно: агент поставляется одним exe-файлом. Нужен доступ к Firebird Топаза (обычно `127.0.0.1:3050`) и исходящий HTTPS к серверу.

Агент **только читает** базу Топаза: все запросы — `SELECT` в read-only транзакции. Входящих портов он не открывает.

## Установка

1. Скачайте `TopazAgent-<версия>-win-x64.zip` (для 32-битной Windows — `win-x86`) со страницы [Releases](https://github.com/digdaran/topaz-center/releases/latest) и распакуйте во временную папку.
2. Откройте PowerShell **от имени администратора** в распакованной папке `TopazAgent-<версия>-win-x64`:

```powershell
# необязательно: сверить контрольную сумму с SHA256SUMS.txt из релиза
Get-FileHash ..\TopazAgent-*-win-x64.zip -Algorithm SHA256
# снять пометку «скачано из интернета», иначе PowerShell может не запустить скрипт
Get-ChildItem -Recurse | Unblock-File
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1 -StationName "АЗС-5 Северная" `
    -ServerUrl https://topaz.example.com `
    -EnrollmentKey <TOPAZ_ENROLLMENT_KEY с сервера>
# пароль Firebird будет запрошен (для Топаза обычно это пароль SYSDBA)
```

**Где скрипт ищет базу Топаза** (если не указан `-DatabasePath`):
1. при обновлении — путь из прежней установки;
2. в стандартной папке `C:\Program Files (x86)\TopazAZS-*\TopazAZS.fdb` (и в `C:\Program Files\TopazAZS-*` на 32-битной Windows). Если найдено несколько баз, скрипт покажет список и попросит выбрать номер (по умолчанию самая свежая);
3. если ничего не найдено — попросит ввести путь к `TopazAZS.fdb` (можно вставить с кавычками из «Копировать как путь»).

Указать путь явно: `-DatabasePath "D:\Topaz\TopazAZS.fdb"`. Если Firebird стоит на другом компьютере (`-FirebirdHost`), путь не ищется: укажите его так, как он виден на том компьютере, или алиас Firebird.

Скрипт:
- копирует агент в `C:\Program Files\TopazAgent`;
- записывает `appsettings.json` (без секретов);
- сохраняет пароль и ключ как переменные окружения только этой службы;
- закрывает папку состояния `%ProgramData%\TopazAgent` для всех, кроме SYSTEM и администраторов;
- регистрирует службу `TopazAgent` (автозапуск, перезапуск при сбое);
- выполняет `check` и запускает службу.

Название АЗС должно быть уникальным: по нему АЗС видна на сервере.

`TopazAgent.exe` не подписан цифровой подписью, поэтому при ручном запуске Windows SmartScreen может показать предупреждение («Подробнее → Выполнить в любом случае»). Службу это не затрагивает.

Дополнительные параметры: `-DatabasePath`, `-FirebirdHost`, `-FirebirdPort`, `-FirebirdUser`, `-TimeZone` (например, `"Ekaterinburg Standard Time"`; по умолчанию берётся пояс Windows).

## Команды

```powershell
& "C:\Program Files\TopazAgent\TopazAgent.exe" check          # конфиг, БД Топаза, связь с сервером, регистрация
& "C:\Program Files\TopazAgent\TopazAgent.exe" status         # очередь, курсоры, dead-letter
& "C:\Program Files\TopazAgent\TopazAgent.exe" requeue-dead   # вернуть отклонённые пакеты в очередь
Get-Content "$env:ProgramData\TopazAgent\logs\agent-$(Get-Date -f yyyyMMdd).log" -Tail 50
```

`check` и `status` читают те же переменные окружения, что и служба. При ручном запуске `check` после установки задайте их в сессии (`$env:TOPAZ_AGENT_Firebird__Password = '...'`).

## Обновление и переустановка

- **Обновление:** запустите `install.ps1` новой версии с теми же параметрами. Очередь и состояние сохраняются. Изменённый `datasets.json` не перезаписывается, новая версия кладётся рядом как `datasets.json.new`.
- **Переустановка на другой ПК или с чистым состоянием:** администратор сервера вызывает `POST /api/v1/admin/stations/<id>/reset`, после чего выполняется `install.ps1` с тем же названием. Агент получит новый токен и перечитает данные; сервер не создаст дублей.
- **Удаление:** `.\uninstall.ps1` (с `-RemoveState` удаляется и локальная очередь).

## Настройки (`appsettings.json`)

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `Collection:IntervalSeconds` | 60 | период чтения смен и журналов |
| `Collection:SnapshotIntervalSeconds` | 900 | период чтения справочников |
| `Collection:ClosedSessionsWindow` | 3 | сколько последних закрытых смен перечитывать каждый цикл |
| `Collection:FullRescanHours` | 24 | полный перепроход всех смен (уходят только изменения) |
| `Collection:MaxItemsPerBatch` | 500 | строк в пакете |
| `Collection:MaxQueueBytes` | 2 ГБ | выше этого сбор на паузе (данные остаются в Топазе) |
| `Server:RetryMaxSeconds` | 900 | максимальная пауза между повторами |

Любой ключ можно переопределить переменной окружения `TOPAZ_AGENT_<Секция>__<Ключ>`.
