import { useMeta } from '../api/client';
import { Section } from '../components/Section';
import { section } from '../nav';
import AgentUpdates from './AgentUpdates';
import Organizations from './Organizations';
import StationRequests from './StationRequests';

/** «Платформа» (superadmin): the whole server — organizations, connecting stations, agent updates. */
export default function PlatformPage() {
  const requests = useMeta().data?.pending_registrations ?? 0;
  return (
    <Section section={section('/platform')} badges={{ connect: requests }} panes={{
      organizations: () => <Organizations />,
      connect: () => <StationRequests />,
      updates: () => <AgentUpdates />,
    }} />
  );
}
