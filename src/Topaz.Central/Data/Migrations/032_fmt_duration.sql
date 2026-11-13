-- Time without connection in alert texts as a person reads it: "52 мин", "5 ч", "13 дн." (not "18727 мин").
CREATE FUNCTION fmt_duration(d interval) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN d IS NULL THEN NULL
        WHEN extract(epoch FROM d) < 90 * 60 THEN fmt_num(round(extract(epoch FROM d) / 60)) || ' мин'
        WHEN extract(epoch FROM d) < 48 * 3600 THEN fmt_num(round(extract(epoch FROM d) / 3600)) || ' ч'
        ELSE fmt_num(round(extract(epoch FROM d) / 86400)) || ' дн.' END
$$;
