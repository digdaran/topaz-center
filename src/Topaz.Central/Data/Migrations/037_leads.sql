-- 1.26: requests from the public site (azshub.ru): a prospect leaves a name and a contact, the superadmins hear
-- about it in Telegram (Processing/LeadNotifier). Platform data: no organization, not readable by the web UI role.
CREATE TABLE leads (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at   timestamptz NOT NULL DEFAULT now(),
    name         text NOT NULL,
    contact      text NOT NULL,
    stations     integer,
    city         text,
    comment      text,
    page         text,
    ip           text,
    user_agent   text,
    consent_at   timestamptz NOT NULL,
    notified_at  timestamptz,
    notify_attempts integer NOT NULL DEFAULT 0
);
CREATE INDEX leads_unnotified ON leads (id) WHERE notified_at IS NULL;
CREATE INDEX leads_ip_time ON leads (ip, created_at);

-- Default privileges (018) grant every new table to topaz_ui; prospects' contacts are not tenant data.
REVOKE ALL ON leads FROM topaz_ui;
