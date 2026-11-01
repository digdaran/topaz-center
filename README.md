# Topaz Center

Сбор данных с АЗС под управлением **АСУ «Топаз АЗС»** (Firebird 2.5) в центральную базу PostgreSQL.

- **Агент** (`TopazAgent.exe`, Windows-служба) стоит на каждой АЗС. Он **только читает** базу Топаза и отправляет изменения по HTTPS. Пока нет связи, изменения копятся в локальной очереди, а потом отправляются повторно без потерь и без дублей.
- **Центральный сервис** (ASP.NET Core 8 + PostgreSQL 16 за Caddy в Docker) принимает пакеты, хранит их сырыми, раскладывает в нормализованные таблицы, следит за АЗС и отдаёт данные в BI, 1С и Excel.
- **Веб-интерфейс** с ролями (владелец, управляющий, просмотр): обзор, топливо и цены, спрос по часам и прогноз, товары с ABC/XYZ и запасами, финансы и маржа (себестоимость вводится в интерфейсе), план-факт, запасы топлива, контроль аномалий с уведомлениями в Telegram, смены, данные с выгрузкой в Excel.

Выгружаются 105 таблиц Топаза: смены, отпуск топлива, резервуары и ТРК по сменам, приём топлива, чеки магазина, касса, регистры и остатки, журнал событий, все справочники.

## Документация

| | |
|---|---|
| [docs/architecture.md](docs/architecture.md) | архитектура, схема обмена, безопасность, тесты, план внедрения |
| [docs/web.md](docs/web.md) | веб-интерфейс: роли, разделы, определения всех показателей, правила контроля |
| [docs/api/openapi.yaml](docs/api/openapi.yaml) | контракт API |
| [docs/database.md](docs/database.md) | схема центральной БД, примеры запросов |
| [docs/topaz-schema.md](docs/topaz-schema.md) | что известно о БД Топаза |
| [deploy/central/README.md](deploy/central/README.md) | установка сервера |
| [deploy/agent/README.md](deploy/agent/README.md) | установка агента на АЗС |

## Быстрый старт для разработки

Нужны .NET SDK 8 и Docker.

```bash
dotnet build
dotnet test                       # unit-тесты; интеграционные пропускаются без БД (см. ниже)

# Postgres и Firebird 2.5 для интеграционных тестов
docker run -d --name topaz-pg -p 5432:5432 -e POSTGRES_PASSWORD=dev postgres:16-alpine
docker run -d --name topaz-fb -p 3050:3050 -e ISC_PASSWORD=dev jacobalberty/firebird:2.5-ss
export TOPAZ_TEST_PG="Host=localhost;Username=postgres;Password=dev;Database=postgres"
export TOPAZ_TEST_FB_HOST=localhost TOPAZ_TEST_FB_PASSWORD=dev
dotnet test
dotnet test --filter "FullyQualifiedName~SenderTests"            # один класс
dotnet test --filter "FullyQualifiedName~Batches_are_idempotent" # один тест
```

Локальный прогон на копии реальной базы АЗС: положите копию `.fdb` в каталог, смонтированный в контейнер Firebird (`/firebird/data`), затем:

```bash
# сервер
ConnectionStrings__Postgres="Host=localhost;Database=topaz;Username=postgres;Password=dev" \
Topaz__AdminToken=dev-admin-token-0123456789abcdef Topaz__BootstrapOwnerLogin=admin Topaz__BootstrapOwnerPassword=dev-password-1 \
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/Topaz.Central

# агент
TOPAZ_AGENT_Firebird__DataSource=localhost TOPAZ_AGENT_Firebird__Database=/firebird/data/topaz.fdb \
TOPAZ_AGENT_Firebird__Password=dev TOPAZ_AGENT_Station__Name="АЗС-тест" TOPAZ_AGENT_Station__TimeZone=Europe/Moscow \
TOPAZ_AGENT_Server__Url=http://localhost:8080 TOPAZ_AGENT_Server__AllowInsecureHttp=true \
TOPAZ_AGENT_StateDirectory=./.agent-state \
dotnet run --project src/Topaz.Agent
# агент пришлёт заявку и напишет код подтверждения: одобрите её в http://localhost:8080 → АЗС → Подключение
```

Файлы баз Топаза (`*.fdb`) в git не попадают: в них реальные данные АЗС.

Веб-интерфейс в режиме разработки (сервер запущен на 8080, первый владелец — через `Topaz__BootstrapOwnerLogin/Password`):

```bash
cd web && npm ci && npm run dev          # http://localhost:5173, /api проксируется на :8080
npm run lint && npm test                 # типы, eslint, vitest
dotnet run --project tools/DemoData -- --server http://localhost:8080 --admin-token dev-admin-token-0123456789abcdef --stations 5 --days 180
E2E_BASE_URL=http://localhost:8080 E2E_LOGIN=admin E2E_PASSWORD=... npx playwright test
```

## Сборка поставки

Готовые сборки агента (zip с `TopazAgent.exe` и скриптами установки, `win-x64` и `win-x86`) лежат на странице [Releases](https://github.com/digdaran/topaz-center/releases/latest). Новая версия выпускается тегом: CI прогонит тесты, создаст релиз с архивами и `SHA256SUMS.txt` и опубликует образ сервера в GHCR.

```bash
git tag v1.0.1 && git push origin v1.0.1      # тег с «-» (v1.1.0-rc1) — предварительный релиз
```

Собрать вручную:

```bash
# агент: один exe для Windows
dotnet publish src/Topaz.Agent -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out/TopazAgent
cp deploy/agent/*.ps1 out/TopazAgent/

# сервер: Docker-образ
docker build -f src/Topaz.Central/Dockerfile -t topaz-central .
```

CI (`.github/workflows/ci.yml`) прогоняет все тесты на PostgreSQL 16 и Firebird 2.5, собирает агент под `win-x64` и `win-x86`, а по тегу `v*` публикует релиз агента и образ сервера в GHCR.
