-- 1.31: a site request goes through stages (new → work → connected | refused), with the superadmin's note,
-- a reason for a refusal and a one-time reminder about a request left "new" for a day. Replaces handled_at (038).
ALTER TABLE leads
    ADD COLUMN stage          text NOT NULL DEFAULT 'new' CHECK (stage IN ('new', 'work', 'connected', 'refused')),
    ADD COLUMN stage_at       timestamptz,
    ADD COLUMN stage_by       text,
    ADD COLUMN refuse_reason  text CHECK (refuse_reason IN ('price', 'not_topaz', 'no_answer', 'spam', 'other')),
    ADD COLUMN refuse_comment text,
    ADD COLUMN note           text,
    ADD COLUMN reminded_at    timestamptz;
UPDATE leads SET stage = 'work', stage_at = handled_at, stage_by = handled_by WHERE handled_at IS NOT NULL;
DROP INDEX leads_new;
ALTER TABLE leads DROP COLUMN handled_at, DROP COLUMN handled_by;
CREATE INDEX leads_stage_new ON leads (id) WHERE stage = 'new';
