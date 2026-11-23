-- 1.33: operators' pay (Деньги → Зарплаты). An employee is a Topaz login of a station (sysUsers.Login, the operator of
-- v_workplace_shifts); the owner sets a scheme per station (and, if needed, per login), the service accrues it per shift.
-- Only the owner sees any of it (Policies.Owner); RLS keeps it inside the organization's stations.

-- A scheme of pay: station default (login NULL) or a login's own scheme, in force from valid_from (station-local date
-- of the shift opening). Parts combine freely; a zero part does not count.
CREATE TABLE pay_schemes (
    id               bigserial     PRIMARY KEY,
    station_id       uuid          NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    login            text,
    valid_from       date          NOT NULL,
    shift_fixed      numeric(12, 2) NOT NULL DEFAULT 0 CHECK (shift_fixed >= 0),        -- ₽ per Topaz shift
    fuel_pct         numeric(6, 3) NOT NULL DEFAULT 0 CHECK (fuel_pct BETWEEN 0 AND 100), -- % of the operator's fuel revenue
    shop_pct         numeric(6, 3) NOT NULL DEFAULT 0 CHECK (shop_pct BETWEEN 0 AND 100), -- % of the operator's shop revenue
    per_liter        numeric(10, 4) NOT NULL DEFAULT 0 CHECK (per_liter >= 0),          -- ₽ per litre sold
    min_shift_hours  numeric(5, 2) NOT NULL DEFAULT 1 CHECK (min_shift_hours BETWEEN 0 AND 48), -- shorter closed shifts: no fixed part
    excluded_groups  bigint[]      NOT NULL DEFAULT '{}',                                 -- shop item groups (dcItemsGrps) without the %
    updated_by       text,
    updated_at       timestamptz   NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_pay_schemes ON pay_schemes (station_id, COALESCE(login, ''), valid_from);

-- A login that gets no pay (an administrator's or a shared login): its sales stay in the reports, not in the payroll.
CREATE TABLE pay_employees (
    station_id  uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    login       text        NOT NULL,
    no_pay      boolean     NOT NULL DEFAULT false,
    updated_by  text,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, login)
);

-- Manual lines of a month: bonus (+), fine (−), advance (paid already, − from what is left to pay).
CREATE TABLE pay_adjustments (
    id          bigserial     PRIMARY KEY,
    station_id  uuid          NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    login       text          NOT NULL,
    month       date          NOT NULL CHECK (extract(day FROM month) = 1),
    kind        text          NOT NULL CHECK (kind IN ('bonus', 'fine', 'advance')),
    amount      numeric(12, 2) NOT NULL CHECK (amount > 0),
    on_date     date          NOT NULL,
    comment     text,
    created_by  text,
    created_at  timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX ix_pay_adjustments ON pay_adjustments (station_id, month);

-- Shifts the owner took out of the payroll (opened by mistake, a test): no part is accrued for them.
CREATE TABLE pay_shift_exclusions (
    station_id   uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    session_id   bigint      NOT NULL,
    excluded_by  text,
    excluded_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, session_id)
);

-- A closed month of a station: the payroll as it was when closed (employees and their shifts), so later data from
-- Topaz does not change what was paid; the live figures are compared with it.
CREATE TABLE pay_months (
    station_id  uuid        NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    month       date        NOT NULL CHECK (extract(day FROM month) = 1),
    closed_at   timestamptz NOT NULL DEFAULT now(),
    closed_by   text,
    snapshot    jsonb       NOT NULL,
    PRIMARY KEY (station_id, month)
);

-- «Выплачено»: the rest of a closed month handed to the employee.
CREATE TABLE pay_payouts (
    station_id  uuid          NOT NULL REFERENCES stations (id) ON DELETE CASCADE,
    month       date          NOT NULL,
    login       text          NOT NULL,
    paid_on     date          NOT NULL,
    amount      numeric(12, 2) NOT NULL,
    marked_by   text,
    marked_at   timestamptz   NOT NULL DEFAULT now(),
    PRIMARY KEY (station_id, month, login)
);

SELECT app_enable_rls('pay_schemes', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('pay_employees', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('pay_adjustments', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('pay_shift_exclusions', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('pay_months', 'station_id = ANY ((SELECT app_stations())::uuid[])');
SELECT app_enable_rls('pay_payouts', 'station_id = ANY ((SELECT app_stations())::uuid[])');

-- The payroll reads a shift's shop receipts (there was only an index by date).
CREATE INDEX IF NOT EXISTS ix_shop_receipts_session ON shop_receipts (station_id, session_id);
