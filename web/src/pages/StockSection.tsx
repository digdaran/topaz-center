import { Section } from '../components/Section';
import { section } from '../nav';
import ItemsPage from './ItemsPage';
import PurchasePage from './PurchasePage';
import TanksPage from './TanksPage';

/** «Склад»: fuel in the tanks (when to order, receipts, losses), shop stock and the shopping list. */
export default function StockSection() {
  return (
    <Section section={section('/stock')} panes={{
      tanks: () => <TanksPage view="tanks" />,
      receipts: () => <TanksPage view="receipts" />,
      losses: () => <TanksPage view="losses" />,
      items: () => <ItemsPage view="stock" />,
      purchase: () => <PurchasePage />,
    }} />
  );
}
