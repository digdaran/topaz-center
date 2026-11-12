-- 1.10.1: smaller storage for big stations.

-- Exchange packets of Topaz with the office (ipRemitPackets.Packet, a BLOB as base64) are not used here and
-- the agent no longer sends them; drop what was stored (about 130 MB for one station).
UPDATE records SET data = data - 'Packet' WHERE dataset = 'ipRemitPackets' AND data ? 'Packet';
UPDATE record_versions SET data = data - 'Packet' WHERE dataset = 'ipRemitPackets' AND data ? 'Packet';

-- record_versions now holds only replaced states (the current one is in records). Rows written before
-- that duplicate the current state; StationMonitor deletes them in small portions in the background
-- (a migration doing it at once would keep the site down for minutes on a small server).
CREATE TABLE maintenance_state (
    key   text PRIMARY KEY,
    value text NOT NULL
);
INSERT INTO maintenance_state (key, value)
SELECT 'versions_dedup', 'up_to:' || COALESCE(max(id), 0) || ';cursor:0' FROM record_versions;
SELECT app_enable_rls('maintenance_state', '(SELECT app_platform())');
