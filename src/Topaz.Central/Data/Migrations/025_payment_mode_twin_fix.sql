-- 1.7.5: "payment mode twin" alerts were raised also for shifts that ended before the namesake mode started giving
-- the discount (nothing was missed then). Open ones are removed; the next detection round raises the valid ones again.
DELETE FROM alerts WHERE rule_code = 'payment_mode_twin' AND status <> 'closed';
