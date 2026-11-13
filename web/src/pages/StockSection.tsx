import { Section } from '../components/Section';
import { section } from '../nav';
import ItemsPage from './ItemsPage';
import TanksPage from './TanksPage';

/** «Склад»: fuel in the tanks (when to order, receipts, losses) and shop stock. */
export default function StockSection() {
  return (
    <Section section={section('/stock')} panes={{
      tanks: () => <TanksPage view="tanks" />,
      receipts: () => <TanksPage view="receipts" />,
      losses: () => <TanksPage view="losses" />,
      items: () => <ItemsPage view="stock" />,
    }} />
  );
}
