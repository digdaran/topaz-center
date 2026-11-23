import { useMeta } from '../api/client';
import { Section } from '../components/Section';
import { section } from '../nav';
import AgentUpdates from './AgentUpdates';
import Leads from './Leads';
import Organizations from './Organizations';
import StationRequests from './StationRequests';

/** «Платформа» (superadmin): the whole server — organizations, connecting stations, agent updates, site requests. */
export default function PlatformPage() {
  const meta = useMeta().data;
  const requests = meta?.pending_registrations ?? 0;
  return (
    <Section section={section('/platform')} badges={{ connect: requests, leads: meta?.new_leads ?? 0 }} panes={{
      organizations: () => <Organizations />,
      connect: () => <StationRequests />,
      updates: () => <AgentUpdates />,
      leads: () => <Leads />,
    }} />
  );
}
