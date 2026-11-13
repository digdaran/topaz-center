import { useMeta } from '../api/client';
import { Section } from '../components/Section';
import { section } from '../nav';
import CostsPage from './CostsPage';
import FinancePage from './FinancePage';

/** «Деньги»: margin and money by day, contract sales, purchase prices (not for viewers). */
export default function MoneySection() {
  const unpriced = useMeta().data?.unpriced_receipts ?? 0;
  return (
    <Section section={section('/money')} badges={{ costs: unpriced }} panes={{
      margin: () => <FinancePage view="margin" />,
      contracts: () => <FinancePage view="contracts" />,
      costs: () => <CostsPage />,
    }} />
  );
}
