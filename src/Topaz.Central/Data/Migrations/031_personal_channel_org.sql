-- A user's own Telegram chat belongs to the user's organization. Before 1.12.1 a user moved to another organization
-- left the chat in the old one (listed there, hidden from the user); move such chats to where their users are now.
UPDATE notification_channels ch SET organization_id = u.organization_id
FROM users u
WHERE ch.user_id = u.id AND ch.organization_id IS DISTINCT FROM u.organization_id;
