import { Section } from '../components/Section';
import { section } from '../nav';
import DataPage from './DataPage';
import StationsPage from './StationsPage';

/** «АЗС»: connection and PCs of the stations, the raw Topaz data. */
export default function StationsSection() {
  return (
    <Section section={section('/stations')} panes={{
      status: () => <StationsPage />,
      data: () => <DataPage />,
    }} />
  );
}
