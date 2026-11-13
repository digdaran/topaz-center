import { Section } from '../components/Section';
import { section } from '../nav';
import DemandPage from './DemandPage';
import FuelPage from './FuelPage';
import ItemsPage from './ItemsPage';
import PlansPage from './PlansPage';
import StationCompare from './StationCompare';

/** «Продажи»: fuel, shop, demand by hour, stations side by side, plan vs fact. */
export default function SalesSection() {
  return (
    <Section section={section('/sales')} panes={{
      fuel: () => <FuelPage />,
      shop: () => <ItemsPage view="sales" />,
      demand: () => <DemandPage />,
      stations: () => <StationCompare />,
      plans: () => <PlansPage />,
    }} />
  );
}
