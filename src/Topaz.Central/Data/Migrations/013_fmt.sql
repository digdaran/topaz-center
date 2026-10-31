-- Human-readable Russian number formatting for alert texts: 15630.00 → "15 630", 37.50 → "37,5".
CREATE FUNCTION fmt_num(x numeric) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN x IS NULL THEN '—' ELSE
        rtrim(replace(replace(trim(to_char(round(x, 2), 'FM999G999G999G990D99')), ',', ' '), '.', ','), ',') END
$$;
