import { Badge, Tabs } from 'antd';
import { ReactNode } from 'react';
import { Navigate, useLocation, useNavigate, useParams } from 'react-router-dom';
import { useMe } from '../auth/auth';
import { allowed, NavSection } from '../nav';
import { PageTitle } from './ui';

/**
 * A menu section with tabs (1.14): title, one line of what it is for, and tabs addressed as `${path}/${tab}`.
 * Switching tabs keeps the query string, so the period and stations stay as chosen.
 */
export function Section({ section, panes, badges }: {
  section: NavSection;
  panes: Record<string, () => ReactNode>;
  badges?: Record<string, number>;
}) {
  const me = useMe();
  const { tab } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const tabs = (section.tabs ?? []).filter((t) => allowed(t.roles, me.role) && panes[t.key]);
  if (!allowed(section.roles, me.role) || !tabs.length) return <Navigate to="/" replace />;
  if (!tab || !tabs.some((t) => t.key === tab)) return <Navigate to={`${section.path}/${tabs[0].key}${location.search}`} replace />;
  return (
    <>
      <PageTitle title={section.label} description={section.description} />
      <Tabs
        activeKey={tab}
        onChange={(k) => navigate(`${section.path}/${k}${location.search}`)}
        destroyInactiveTabPane
        items={tabs.map((t) => ({
          key: t.key,
          label: badges?.[t.key] ? <>{t.label} <Badge count={badges[t.key]} size="small" color="orange" /></> : t.label,
          children: t.key === tab ? panes[t.key]() : null,
        }))}
      />
    </>
  );
}
