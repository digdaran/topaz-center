# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Data collection from gas stations running "АСУ Топаз АЗС" (Firebird 2.5) into a central PostgreSQL service. A Windows-service agent per station reads the Topaz DB **read-only** and pushes changes over HTTPS; the central ASP.NET Core service ingests, normalizes, monitors and exports. Docs are in Russian (`docs/`); code and identifiers are English.

## Commands

.NET SDK 8 is not installed on the dev box used so far; everything was run via the SDK container. Either works:

```bash
dotnet build                     # TreatWarningsAsErrors is on (Directory.Build.props)
dotnet test                      # integration tests auto-skip without the env vars below
dotnet test --filter "FullyQualifiedName~SenderTests"
dotnet test --filter "FullyQualifiedName~Batches_are_idempotent"

# integration test prerequisites
docker run -d --name topaz-pg -p 5432:5432 -e POSTGRES_PASSWORD=dev postgres:16-alpine
docker run -d --name topaz-fb -p 3050:3050 -e ISC_PASSWORD=dev jacobalberty/firebird:2.5-ss
export TOPAZ_TEST_PG="Host=localhost;Username=postgres;Password=dev;Database=postgres"
export TOPAZ_TEST_FB_HOST=localhost TOPAZ_TEST_FB_PASSWORD=dev

# without a local SDK
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test

# agent publish (single exe for stations)
dotnet publish src/Topaz.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# central stack
cd deploy/central && cp .env.example .env && docker compose up -d --build
```

Agent CLI: `TopazAgent.exe [run|check|status|requeue-dead|--version]`. Config via `appsettings.json` next to the exe plus env vars `TOPAZ_AGENT_<Section>__<Key>`; central config via `ConnectionStrings__Postgres` and `Topaz__*` env vars.

## Architecture (big picture)

Projects: `src/Topaz.Contracts` (wire DTOs + `BatchValidator`, shared by both sides — change the protocol here), `src/Topaz.Agent`, `src/Topaz.Central`, tests in `tests/`.

**Change detection is hash-based, not timestamp-based.** Topaz transactional tables have no modification date and rows are updated after insert (an `flOrders` row is inserted with volume 0, then updated). The agent canonicalizes each row to JSON (`Source/RowSerializer.cs`), SHA-256s it, and compares against `row_state` in its local SQLite (`State/StateStore.cs`). Three dataset kinds (`Configuration/DatasetDefinition.cs`, catalog in `src/Topaz.Agent/datasets.json`):
- `session`: rows of a shift window (open + last N closed + backfill chunk, daily full rescan via `session_cursor` reset); deletes detected within the window. Child tables without `SessionID` use `parent` (JOIN).
- `incremental`: append-only logs (`sysEvents`), `id > watermark − overlap` (overlap catches Firebird generator ids committed out of order).
- `snapshot`: whole table on an interval; deletes detected.

`Collection/Collector.cs` does one cycle in one read-only snapshot Firebird transaction. `StateStore.Commit` queues batches **and** advances `row_state`/watermarks in a single SQLite transaction — keep that atomicity. `Sending/Sender.cs` sends the outbox strictly FIFO, one batch at a time (head-of-line blocking is intentional: the server relies on order). 4xx-permanent → dead letter + forget row hashes so rows get re-read; 401 → clear token, re-enroll; everything else → backoff retry, never drop.

Central (`src/Topaz.Central`): `POST /api/v1/batches` stores the batch verbatim in `raw_batches` (idempotency key `UNIQUE(station_id, batch_id)`, payload SHA-256 for 409 on reuse) and returns 202; `Processing/BatchProcessor.cs` applies batches per station in id order (a station's `failed` batch blocks only that station) into `records` (generic current state for any dataset, upsert only when `hash` differs), `record_versions` (history), and typed tables via declarative `Processing/Projections.cs`. Unknown datasets are still stored in `records` (forward compatibility with newer agents).

Schema lives only in embedded `src/Topaz.Central/Data/Migrations/NNN_*.sql`, applied at startup by `Data/Migrator.cs`. Add a new numbered file; never edit an applied one. Adding a normalized table = new migration + a `Projection` entry.

Auth: station enrolls with name + shared `Topaz__EnrollmentKey` → per-station token (server stores SHA-256; agent stores it DPAPI-encrypted). Admin/export endpoints use `Topaz__AdminToken`.

## Topaz DB specifics (see docs/topaz-schema.md)

- Identifiers are mixed-case and must be double-quoted (`"flOrders"."SessionID"`); `Configuration/SqlIdentifier.cs` validates + quotes everything coming from config.
- Timestamps are local wall-clock without zone; the agent attaches the station offset (`Station:TimeZone`), central stores `timestamptz`.
- Topaz triggers call UDFs (`MD5`) that do not exist outside a Topaz install, so writing to a Topaz DB copy in a plain Firebird container fails — irrelevant for the agent (read-only), but matters when simulating changes in tests: use tables without such triggers (e.g. `flSesTanks`) or synthetic schemas.

## Web UI (web/ + src/Topaz.Central/Ui)

- `web/`: Vite + React 18 + TypeScript + Ant Design (ru_RU) + TanStack Query + tree-shaken ECharts (`components/echarts.ts`). `npm run lint` (tsc + eslint), `npm test` (vitest, `src/**/*.test.ts`), `npm run e2e` (Playwright against a running stack; in this sandbox run it in `mcr.microsoft.com/playwright:v1.49.1-jammy`). The Dockerfile builds it into `wwwroot`.
- API under `/api/v1/ui`: cookie auth (`Ui/UiAuth.cs`), every mutation needs header `X-Topaz-Ui: 1`, roles owner/manager/viewer. **Every query must be restricted by `StationScope`** (via `UiFilter.ParseAsync` or `CanSeeAsync`); viewers must never receive costs/margins (403 or `Kpi.Redact()`). `UiTests.Managers_see_only_their_stations_everywhere` loops over endpoints — add new ones there.
- Long-period figures read the daily rollups `agg_fuel_daily` / `agg_shop_daily` / `agg_shop_receipts_daily` (rebuilt by `Processing/RollupRefresher` from `rollup_dirty`, marked by the batch processor; costs are applied during the rebuild, so cost edits mark stations `all`). Hour/hose/order-level views read raw tables for the period. Keep both consistent: `AnalyticsTests` asserts rollup = raw.
- The SPA fallback answers only GET outside `/api` (a 200 for an unknown API path would make an agent drop batches).
- Metric definitions live in `docs/web.md`; keep them in sync with the SQL.
- Demo data: `tools/DemoData` (synthetic stations through the real agent protocol, with planted anomalies).

## Reconciliation with Topaz reports

`tools/reconcile/` compares central data with Topaz's own XML exports (period `DataPacket`, aggregated shift export); usage in its README. Put exports in `samples/` (git-ignored, real data). Rules learned from it, encoded in `003_shift_report.sql` views (`v_shift_*`, `item_rests_at`) and covered by `Shift_report_views_follow_topaz_rules`:
- cash vs cashless comes from `flOrders.Remark = 'Б'` / `AmountCashless > 0` (`fuel_sales.payment_kind`), not from the payment mode name;
- revenue is `Amount`; `AmountCash`/`AmountCashless` are the prepaid amounts before the per-litre discount;
- Topaz groups payment modes by name (several ids share one name) and counts zero-volume orders;
- shift item rests are as of the shift end; period reports cut by operation time, not by shift.
Re-run the reconciliation after changing projections or views.

## Hard rules

- Never write to the Topaz database. Agent SQL must stay `SELECT`-only (`SqlBuilder`) inside the read-only transaction (`FirebirdTopazSource`).
- Never commit `*.fdb`/`*.gdb`/`*.fbk` (real station data); `.gitignore` covers them.
- Secrets only via env vars (central `.env`, agent per-service environment in the registry set by `deploy/agent/install.ps1`). Never log row data or tokens.
- Excluded secret columns (`dcAgents.Password`, `sysUsers.PswHash/Psw/SecurityData`, `dcPaymentModes.Config`) must stay excluded in `datasets.json`; a test asserts this.
- .NET 8 means the agent does not run on Windows 7 (accepted by the owner).
