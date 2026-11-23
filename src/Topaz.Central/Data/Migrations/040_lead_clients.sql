-- 1.32: from a site request to a client. A request remembers the organization made from it (or attached to it);
-- such an organization gets a trial period once its first station sends data; its first owner has a temporary
-- password and must change it at the first sign-in.
ALTER TABLE leads
    ADD COLUMN organization_id uuid REFERENCES organizations (id) ON DELETE SET NULL,
    ADD COLUMN client_at timestamptz,               -- when «Создать клиента» made or attached the organization
    ADD COLUMN client_created boolean NOT NULL DEFAULT false, -- the organization was made from this request (gets a trial)
    ADD COLUMN connected_announced_at timestamptz; -- "the client's first station is sending data" reached Telegram
CREATE INDEX leads_organization ON leads (organization_id) WHERE organization_id IS NOT NULL;

ALTER TABLE organizations
    ADD COLUMN trial_until       date,          -- the last day of the free month; null = no trial (existing clients)
    ADD COLUMN trial_reminded_at timestamptz;   -- the superadmins were told the trial is ending

ALTER TABLE users ADD COLUMN must_change_password boolean NOT NULL DEFAULT false;

-- leads now carries organization_id: platform data like station_registrations (the web role has no grant anyway, 037).
SELECT app_enable_rls('leads', '(SELECT app_platform())');
