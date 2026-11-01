using System.Security.Claims;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Central.Processing;

namespace Topaz.Central.Ui;

public sealed record TankSettingsBody(decimal MinLiters, decimal LeadTimeDays, decimal SafetyDays);
public sealed record AlertStatusBody(string Status);
public sealed record RuleBody(bool Enabled, decimal? Threshold, string? Severity);
public sealed record ChannelBody(string Target, string? Title, string MinSeverity, bool DailyDigest, bool? Platform = null);

/// <summary>Stage 4: fuel stock planning, alerts (anomalies), alert rules, notification channels.</summary>
public static class ControlEndpoints
{
    public static void MapUiControl(this RouteGroupBuilder ui)
    {
        ui.MapGet("/analytics/tanks", Tanks);
        ui.MapPut("/tanks/{station:guid}/{tank:long}", SetTank).RequireAuthorization(Policies.Manager);
        ui.MapGet("/alerts", Alerts);
        ui.MapGet("/alerts/summary", Summary);
        ui.MapPost("/alerts/{id:long}/status", SetStatus);
        ui.MapPost("/alerts/run", async (AnomalyDetector d, CancellationToken ct) => Results.Ok(new { found = await d.RunAsync(ct) })).RequireAuthorization(Policies.Owner);

        var owner = ui.MapGroup("").RequireAuthorization(Policies.Owner);
        owner.MapGet("/alert-rules", Rules);
        owner.MapPut("/alert-rules/{code}", SetRule);
        owner.MapDelete("/alert-rules/{code}", ResetRule);
        ui.MapPut("/alert-rules/{code}/default", SetDefaultRule).RequireAuthorization(Policies.Super);
        owner.MapGet("/notifications", Channels);
        owner.MapPost("/notifications", AddChannel);
        owner.MapDelete("/notifications/{id:long}", DeleteChannel);
        owner.MapPost("/notifications/{id:long}/test", TestChannel);
    }

    // ---------------------------------------------------------------- tanks

    private static async Task<IResult> Tanks(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        await using var c = await db.OpenConnectionAsync();
        var rows = (await TankPlanner.ComputeAsync(c, f.Stations)).Select(t => new
        {
            station_id = t.StationId, station = t.Station, tank_id = t.TankId, tank_num = t.TankNum, fuel_name = t.FuelName, book_volume = t.BookVolume,
            capacity = t.Capacity, fill_share = t.FillShare, dead_rest = t.DeadRest, min_liters = t.MinLiters, lead_time_days = t.LeadTimeDays, safety_days = t.SafetyDays,
            per_day = t.PerDay, days_to_min = t.DaysToMin, order_by = t.OrderBy?.ToString("yyyy-MM-dd"), recommended_order = t.RecommendedOrder, status = t.Status,
            has_gauge = t.HasGauge, last_fact_volume = t.LastFactVolume, last_fact_at = t.LastFactAt, last_fact_session = t.LastFactSession, last_fact_diff = t.LastFactDiff,
        });
        // Book stock at the end of each shift in the period (start + receipts − dispensing).
        var history = await c.QueryAsync("""
            SELECT t.station_id, t.tank_id, t.tank_num, t.fuel_name, s.session_id, s.session_num,
                   to_char(COALESCE(s.ended_at, now()) AT TIME ZONE sn.time_zone, 'YYYY-MM-DD"T"HH24:MI') AS at,
                   t.start_volume + t.receipt_volume - t.pump_volume AS book_volume, t.receipt_volume, t.pump_volume
            FROM tank_shift_states t
            JOIN shifts s ON s.station_id = t.station_id AND s.session_id = t.session_id
            JOIN v_station_names sn ON sn.station_id = t.station_id
            WHERE t.station_id = ANY(@st) AND s.started_at >= @utcFrom AND s.started_at < @utcTo
              AND (s.started_at AT TIME ZONE sn.time_zone)::date BETWEEN @from::date AND @to::date
            ORDER BY s.started_at
            """, f.Args);
        var receipts = await c.QueryAsync("""
            SELECT r.station_id, sn.station, r.receipt_id, r.received_at, r.tank_id, vt.num AS tank_num, sf.fuel_name, r.doc_number, r.car_number,
                   r.doc_volume, r.measured_volume, r.discrepancy,
                   round(100 * (r.doc_volume - COALESCE(r.measured_volume, r.doc_volume + r.discrepancy)) / NULLIF(r.doc_volume, 0), 2) AS shortage_pct
            FROM fuel_receipts r
            JOIN v_station_names sn ON sn.station_id = r.station_id
            LEFT JOIN v_tanks vt ON vt.station_id = r.station_id AND vt.tank_id = r.tank_id
            LEFT JOIN v_station_fuels sf ON sf.station_id = r.station_id AND sf.fuel_id = r.fuel_id
            WHERE r.station_id = ANY(@st) AND r.received_at >= @utcFrom AND r.received_at < @utcTo
            ORDER BY r.received_at DESC
            """, f.Args);
        // Book against measurement at each shift close of the period (tanks with a gauge, or measured by hand).
        var measures = await c.QueryAsync("""
            SELECT m.station_id, sn.station, m.tank_id, m.tank_num, m.fuel_name, m.session_id, m.session_num, m.ended_at,
                   m.book_volume, m.fact_volume, m.diff, m.pump_volume, m.receipt_volume, m.density, m.temperature, m.height,
                   round(100 * m.diff / NULLIF(m.pump_volume, 0), 2) AS diff_pct, m.diff > COALESCE(r.threshold, 200) AS big_surplus
            FROM v_tank_measurements m JOIN v_station_names sn ON sn.station_id = m.station_id
            LEFT JOIN v_station_rules r ON r.station_id = m.station_id AND r.code = 'tank_surplus'
            WHERE m.station_id = ANY(@st) AND m.started_at >= @utcFrom AND m.started_at < @utcTo
              AND (m.started_at AT TIME ZONE sn.time_zone)::date BETWEEN @from::date AND @to::date
            ORDER BY m.ended_at
            """, f.Args);
        // Losses per tank over the period: shortages and surpluses at the shift closes, against what was dispensed.
        // Surpluses above the tank_surplus threshold (usually a delivery not entered in Topaz) are shown apart and left out
        // of the loss figures, so one unrecorded truck does not hide the everyday losses.
        var losses = await c.QueryAsync("""
            SELECT m.station_id, sn.station, m.tank_id, m.tank_num, max(m.fuel_name) AS fuel_name, count(*) AS measurements,
                   sum(m.pump_volume) AS pumped,
                   COALESCE(-sum(m.diff) FILTER (WHERE m.diff < 0), 0) AS shortage,
                   COALESCE(sum(m.diff) FILTER (WHERE m.diff > 0 AND m.diff <= COALESCE(r.threshold, 200)), 0) AS surplus,
                   COALESCE(sum(m.diff) FILTER (WHERE m.diff <= COALESCE(r.threshold, 200)), 0) AS net,
                   round(100 * COALESCE(sum(m.diff) FILTER (WHERE m.diff <= COALESCE(r.threshold, 200)), 0) / NULLIF(sum(m.pump_volume), 0), 3) AS net_pct,
                   count(*) FILTER (WHERE m.diff > COALESCE(r.threshold, 200)) AS big_surplus_count,
                   COALESCE(sum(m.diff) FILTER (WHERE m.diff > COALESCE(r.threshold, 200)), 0) AS big_surplus
            FROM v_tank_measurements m JOIN v_station_names sn ON sn.station_id = m.station_id
            LEFT JOIN v_station_rules r ON r.station_id = m.station_id AND r.code = 'tank_surplus'
            WHERE m.station_id = ANY(@st) AND m.started_at >= @utcFrom AND m.started_at < @utcTo
              AND (m.started_at AT TIME ZONE sn.time_zone)::date BETWEEN @from::date AND @to::date
            GROUP BY m.station_id, sn.station, m.tank_id, m.tank_num
            ORDER BY sn.station, m.tank_num
            """, f.Args);
        return Results.Ok(new { tanks = rows, history, receipts, measures, losses });
    }

    private static async Task<IResult> SetTank(Guid station, long tank, TankSettingsBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, station)) return Results.Forbid();
        if (body.MinLiters < 0 || body.LeadTimeDays is < 0 or > 60 || body.SafetyDays is < 0 or > 60) return Results.Problem(statusCode: 400, title: "Недопустимые значения");
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO tank_settings (station_id, tank_id, min_liters, lead_time_days, safety_days, updated_by) VALUES (@station, @tank, @MinLiters, @LeadTimeDays, @SafetyDays, @by)
            ON CONFLICT (station_id, tank_id) DO UPDATE SET min_liters = EXCLUDED.min_liters, lead_time_days = EXCLUDED.lead_time_days,
                safety_days = EXCLUDED.safety_days, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, new { station, tank, body.MinLiters, body.LeadTimeDays, body.SafetyDays, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "tank_settings", $"station:{station}/tank:{tank}", body);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- alerts

    private static async Task<IResult> Alerts(HttpContext ctx, StationScope scope, UiDb db, string? status, string? severity, string? rule)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        // Alerts without a station (connection requests) are for owners and superadmins.
        var owner = ctx.User.IsInRole(Roles.Superadmin) && ctx.Request.Query["stations"].Count == 0; // connection requests: platform only
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT a.id, a.station_id, COALESCE(sn.station, 'Новая АЗС') AS station, a.rule_code, r.title AS rule_title, a.severity, a.title, a.details, a.value,
                   a.session_id, a.registration_id, a.occurred_at, a.detected_at, a.status, a.status_by, COALESCE(sn.time_zone, 'Europe/Moscow') AS time_zone
            FROM alerts a LEFT JOIN v_station_names sn ON sn.station_id = a.station_id JOIN alert_rules r ON r.code = a.rule_code
            WHERE (a.station_id = ANY(@st) OR (a.station_id IS NULL AND @owner))
              AND COALESCE(a.occurred_at, a.detected_at) >= @utcFrom AND COALESCE(a.occurred_at, a.detected_at) < @utcTo
              AND (@status::text IS NULL OR (@status = 'open' AND a.status <> 'closed') OR a.status = @status)
              AND (@severity::text IS NULL OR a.severity = @severity) AND (@rule::text IS NULL OR a.rule_code = @rule)
            ORDER BY CASE a.severity WHEN 'critical' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END, COALESCE(a.occurred_at, a.detected_at) DESC
            LIMIT 1000
            """, new { st = f.Stations, f.UtcFrom, f.UtcTo, owner, status = string.IsNullOrEmpty(status) ? null : status, severity = string.IsNullOrEmpty(severity) ? null : severity, rule = string.IsNullOrEmpty(rule) ? null : rule });
        var byRule = await c.QueryAsync("""
            SELECT a.rule_code, r.title, a.severity, count(*) AS total, count(*) FILTER (WHERE a.status = 'new') AS new
            FROM alerts a JOIN alert_rules r ON r.code = a.rule_code
            WHERE (a.station_id = ANY(@st) OR (a.station_id IS NULL AND @owner))
              AND COALESCE(a.occurred_at, a.detected_at) >= @utcFrom AND COALESCE(a.occurred_at, a.detected_at) < @utcTo
            GROUP BY 1, 2, 3, r.sort ORDER BY r.sort
            """, new { st = f.Stations, f.UtcFrom, f.UtcTo, owner });
        return Results.Ok(new { alerts = rows, by_rule = byRule });
    }

    /// <summary>Counts for the menu badge and the overview.</summary>
    private static async Task<IResult> Summary(ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        var st = await scope.AllowedAsync(user);
        var owner = user.IsInRole(Roles.Superadmin); // alerts without a station (connection requests) are the platform's
        await using var c = await db.OpenConnectionAsync();
        var counts = await c.QuerySingleAsync("""
            SELECT count(*) FILTER (WHERE status = 'new') AS new, count(*) FILTER (WHERE status = 'new' AND severity = 'critical') AS critical
            FROM alerts WHERE station_id = ANY(@st) OR (station_id IS NULL AND @owner)
            """, new { st, owner });
        var latest = await c.QueryAsync("""
            SELECT a.id, COALESCE(sn.station, 'Новая АЗС') AS station, a.severity, a.title, a.session_id, a.station_id, a.registration_id,
                   COALESCE(a.occurred_at, a.detected_at) AS at
            FROM alerts a LEFT JOIN v_station_names sn ON sn.station_id = a.station_id
            WHERE (a.station_id = ANY(@st) OR (a.station_id IS NULL AND @owner)) AND a.status = 'new'
            ORDER BY CASE a.severity WHEN 'critical' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END, COALESCE(a.occurred_at, a.detected_at) DESC LIMIT 8
            """, new { st, owner });
        return Results.Ok(new { counts, latest });
    }

    private static async Task<IResult> SetStatus(long id, AlertStatusBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (body.Status is not ("new" or "seen" or "closed")) return Results.Problem(statusCode: 400, title: "status: new | seen | closed");
        await using var c = await db.OpenConnectionAsync();
        var found = (await c.QueryAsync<(long Id, Guid? Station)>("SELECT id, station_id FROM alerts WHERE id = @id", new { id })).ToList();
        if (found.Count == 0) return Results.NotFound();
        if (found[0].Station is { } station ? !await scope.CanSeeAsync(user, station) : !user.IsInRole(Roles.Superadmin)) return Results.NotFound();
        await c.ExecuteAsync("UPDATE alerts SET status = @s, status_by = @by WHERE id = @id", new { id, s = body.Status, by = user.Identity?.Name });
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- rules and channels (owner)

    /// <summary>Rules handled by the platform (not by organizations).</summary>
    private static readonly string[] PlatformRules = ["station_registration", "agent_update_failed"];

    /// <summary>Rules as the user's organization applies them: its override on top of the shared default.</summary>
    private static async Task<IResult> Rules(ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT r.code, r.title, r.description, r.unit, r.sort,
                   COALESCE(o.enabled, r.enabled) AS enabled, COALESCE(o.threshold, r.threshold) AS threshold, COALESCE(o.severity, r.severity) AS severity,
                   r.enabled AS default_enabled, r.threshold AS default_threshold, r.severity AS default_severity,
                   o.code IS NOT NULL AS overridden, r.code = ANY(@platform) AS platform
            FROM alert_rules r LEFT JOIN alert_rule_overrides o ON o.code = r.code AND o.organization_id = @org
            WHERE @super OR NOT r.code = ANY(@platform)
            ORDER BY r.sort
            """, new { org = user.OrganizationId(), super = user.IsInRole(Roles.Superadmin), platform = PlatformRules });
        return Results.Ok(rows);
    }

    /// <summary>The organization's own setting of a rule (platform rules: their defaults, superadmin only).</summary>
    private static async Task<IResult> SetRule(string code, RuleBody body, ClaimsPrincipal user, UiDb db)
    {
        if (body.Severity is not null && !AnomalyDetector.SeverityRank.ContainsKey(body.Severity)) return Results.Problem(statusCode: 400, title: "severity: critical | warning | info");
        if (PlatformRules.Contains(code)) return user.IsInRole(Roles.Superadmin) ? await SetDefaultRule(code, body, user, db) : Results.NotFound();
        if (user.OrganizationId() is not { } org) return Results.Problem(statusCode: 400, title: "Выберите организацию");
        await using var c = await db.OpenConnectionAsync();
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM alert_rules WHERE code = @code)", new { code })) return Results.NotFound();
        await c.ExecuteAsync("""
            INSERT INTO alert_rule_overrides (organization_id, code, enabled, threshold, severity)
            SELECT @org, @code, @Enabled, CASE WHEN r.threshold IS NULL THEN NULL ELSE COALESCE(@Threshold, r.threshold) END, COALESCE(@Severity, r.severity)
            FROM alert_rules r WHERE r.code = @code
            ON CONFLICT (organization_id, code) DO UPDATE SET enabled = EXCLUDED.enabled,
                threshold = COALESCE(@Threshold, alert_rule_overrides.threshold, EXCLUDED.threshold), severity = EXCLUDED.severity
            """, new { org, code, body.Enabled, body.Threshold, body.Severity });
        await Audit.WriteAsync(db, user, "alert_rule_updated", "rule:" + code, body);
        return Results.NoContent();
    }

    /// <summary>Back to the shared default for this organization.</summary>
    private static async Task<IResult> ResetRule(string code, ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM alert_rule_overrides WHERE organization_id = @org AND code = @code", new { org = user.OrganizationId(), code });
        await Audit.WriteAsync(db, user, "alert_rule_reset", "rule:" + code, null);
        return Results.NoContent();
    }

    /// <summary>Shared defaults (every organization without its own setting), superadmin only.</summary>
    private static async Task<IResult> SetDefaultRule(string code, RuleBody body, ClaimsPrincipal user, UiDb db)
    {
        if (body.Severity is not null && !AnomalyDetector.SeverityRank.ContainsKey(body.Severity)) return Results.Problem(statusCode: 400, title: "severity: critical | warning | info");
        await using var c = await db.OpenConnectionAsync();
        var n = await c.ExecuteAsync("""
            UPDATE alert_rules SET enabled = @Enabled, threshold = CASE WHEN threshold IS NULL THEN NULL ELSE COALESCE(@Threshold, threshold) END,
                severity = COALESCE(@Severity, severity) WHERE code = @code
            """, new { code, body.Enabled, body.Threshold, body.Severity });
        if (n == 0) return Results.NotFound();
        await Audit.WritePlatformAsync(db.System, user, "alert_rule_default_updated", "rule:" + code, body);
        return Results.NoContent();
    }

    /// <summary>The organization's Telegram chats; a superadmin also sees the platform's own chats.</summary>
    private static async Task<IResult> Channels(ClaimsPrincipal user, UiDb db, IOptions<CentralOptions> options)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT id, kind, target, title, min_severity, daily_digest, enabled, last_digest, created_at, organization_id IS NULL AS platform
            FROM notification_channels WHERE organization_id = @org OR (organization_id IS NULL AND @super) ORDER BY organization_id IS NULL, id
            """, new { org = user.OrganizationId(), super = user.IsInRole(Roles.Superadmin) });
        return Results.Ok(new { token_configured = !string.IsNullOrWhiteSpace(options.Value.TelegramBotToken), channels = rows });
    }

    private static async Task<IResult> AddChannel(ChannelBody body, ClaimsPrincipal user, UiDb db)
    {
        if (string.IsNullOrWhiteSpace(body.Target) || body.Target.Length > 64) return Results.Problem(statusCode: 400, title: "Укажите chat id Telegram");
        if (!AnomalyDetector.SeverityRank.ContainsKey(body.MinSeverity)) return Results.Problem(statusCode: 400, title: "min_severity: critical | warning | info");
        var platform = body.Platform == true && user.IsInRole(Roles.Superadmin);
        var org = platform ? null : user.OrganizationId();
        if (!platform && org is null) return Results.Problem(statusCode: 400, title: "Выберите организацию");
        await using var c = await db.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO notification_channels (target, title, min_severity, daily_digest, organization_id) VALUES (@Target, @Title, @MinSeverity, @DailyDigest, @org) RETURNING id",
            new { Target = body.Target.Trim(), body.Title, body.MinSeverity, body.DailyDigest, org });
        await Audit.WriteAsync(db, user, "channel_added", "channel:" + id, body);
        return Results.Ok(new { id });
    }

    private const string OwnChannel = "id = @id AND (organization_id = @org OR (organization_id IS NULL AND @super))";

    private static async Task<IResult> DeleteChannel(long id, ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var n = await c.ExecuteAsync($"DELETE FROM notification_channels WHERE {OwnChannel}", new { id, org = user.OrganizationId(), super = user.IsInRole(Roles.Superadmin) });
        if (n == 0) return Results.NotFound();
        await Audit.WriteAsync(db, user, "channel_deleted", "channel:" + id, null);
        return Results.NoContent();
    }

    private static async Task<IResult> TestChannel(long id, ClaimsPrincipal user, UiDb db, AnomalyDetector detector, IOptions<CentralOptions> options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.TelegramBotToken)) return Results.Problem(statusCode: 400, title: "Токен бота не задан (Topaz__TelegramBotToken)");
        await using var c = await db.OpenConnectionAsync();
        var target = await c.QuerySingleOrDefaultAsync<string>($"SELECT target FROM notification_channels WHERE {OwnChannel}", new { id, org = user.OrganizationId(), super = user.IsInRole(Roles.Superadmin) });
        if (target is null) return Results.NotFound();
        var ok = await detector.SendAsync(options.Value.TelegramBotToken, target, "✅ Topaz Center: тестовое сообщение. Уведомления работают.", ct);
        return ok ? Results.NoContent() : Results.Problem(statusCode: 502, title: "Telegram не принял сообщение: проверьте chat id и что бот добавлен в чат");
    }
}
