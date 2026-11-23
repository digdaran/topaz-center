-- 1.30: site requests in the web UI («Платформа → Заявки с сайта»): a superadmin marks a request as handled.
ALTER TABLE leads ADD COLUMN handled_at timestamptz, ADD COLUMN handled_by text;
CREATE INDEX leads_new ON leads (id) WHERE handled_at IS NULL;
