import { DownloadOutlined } from '@ant-design/icons';
import { Button, Flex, Input, Table, Tabs, Tag } from 'antd';
import type { SorterResult } from 'antd/es/table/interface';
import { useState } from 'react';
import { csvUrl, useFilter, useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { QueryState } from '../components/ui';
import { dec, int, liters, money, PAYMENT_KIND } from '../lib/format';

interface Page { total: number; page: number; page_size: number; columns: string[]; rows: Record<string, any>[] }

const TITLES: Record<string, string> = {
  station: 'АЗС', local_time: 'Время', session_id: 'Смена', order_num: '№', fuel_name: 'Топливо', tank_num: 'Резерв.', hose_num: 'Рукав',
  payment_mode: 'Вид оплаты', pay_category: 'Способ оплаты', remark: 'Пометка', payment_kind: 'Нал/безнал (Топаз)', volume: 'Литры', price_paid: 'Цена', price: 'Цена без скидки', amount: 'Сумма',
  amount_cash: 'Предопл. нал.', amount_cashless: 'Безнал', amount_discount: 'Скидка', card_code: 'Карта', doc_num: 'Чек', item_name: 'Товар',
  item_code: 'Штрихкод', group_name: 'Группа', is_return: 'Возврат', quantity: 'Кол-во', receipt_id: 'ID', doc_number: 'Документ',
  car_number: 'Машина', doc_volume: 'По документу', measured_volume: 'Факт', discrepancy: 'Расхождение', doc_mass: 'Масса',
  doc_density: 'Плотность', doc_price: 'Цена док.', event_id: 'ID', category: 'Категория', message: 'Событие', source: 'Источник',
  object: 'Объект', computer_name: 'Компьютер', doc_type: 'Операция', cash_name: 'Касса',
};

const MONEY = new Set(['price', 'price_paid', 'amount', 'amount_cash', 'amount_cashless', 'amount_discount', 'doc_price']);
const LITERS = new Set(['volume', 'doc_volume', 'measured_volume', 'discrepancy']);

function render(col: string) {
  if (MONEY.has(col)) return (v: number) => <span style={{ whiteSpace: 'nowrap' }}>{money(v, true)}</span>;
  if (LITERS.has(col)) return (v: number) => <span style={{ whiteSpace: 'nowrap' }}>{liters(v)}</span>;
  if (col === 'quantity' || col === 'doc_mass') return (v: number) => dec(v, col === 'quantity' ? 0 : 2);
  if (col === 'payment_kind') return (v: string) => PAYMENT_KIND[v] ?? v;
  if (col === 'is_return') return (v: boolean) => (v ? <Tag color="red">да</Tag> : '');
  if (col === 'message') return (v: string) => <span style={{ whiteSpace: 'pre-wrap' }}>{v}</span>;
  return undefined;
}

function DataTable({ kind }: { kind: string }) {
  const [filter] = useFilter();
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState<{ sort?: string; dir?: string }>({});
  const extra = { page, page_size: 50, q: search || undefined, ...sort };
  const { data, error, isLoading, isFetching } = useFiltered<Page>(`/data/${kind}`, extra);
  return (
    <>
      <Flex gap={8} wrap style={{ marginBottom: 8 }}>
        <Input.Search allowClear placeholder="Поиск" style={{ width: 260 }} onSearch={(v) => { setSearch(v); setPage(1); }} />
        <Button icon={<DownloadOutlined />} href={csvUrl(`/data/${kind}`, filter, { q: search || undefined, ...sort })}>
          Скачать CSV (Excel)
        </Button>
        {data && <span style={{ alignSelf: 'center', opacity: 0.7 }}>Строк: {int(data.total)}</span>}
      </Flex>
      <QueryState error={error} loading={isLoading}>
        <Table
          size="small"
          loading={isFetching}
          rowKey={(_, i) => String(i)}
          dataSource={data?.rows}
          scroll={{ x: 'max-content' }}
          onChange={(p, _f, s) => {
            const sr = s as SorterResult<any>;
            setPage(p.current ?? 1);
            setSort(sr.order ? { sort: String(sr.field), dir: sr.order === 'ascend' ? 'asc' : 'desc' } : {});
          }}
          pagination={{ current: page, pageSize: 50, total: data?.total, showSizeChanger: false }}
          columns={(data?.columns ?? []).map((c) => ({
            title: TITLES[c] ?? c, dataIndex: c, key: c, sorter: true,
            align: MONEY.has(c) || LITERS.has(c) || c === 'quantity' ? ('right' as const) : undefined,
            render: render(c),
          }))}
        />
      </QueryState>
    </>
  );
}

export default function DataPage() {
  return (
    <>
      <FilterBar granularity={false} compare={false} />
      <Tabs destroyInactiveTabPane items={[
        { key: 'fuel-sales', label: 'Отпуск топлива', children: <DataTable kind="fuel-sales" /> },
        { key: 'shop-lines', label: 'Продажи магазина', children: <DataTable kind="shop-lines" /> },
        { key: 'fuel-receipts', label: 'Приём топлива', children: <DataTable kind="fuel-receipts" /> },
        { key: 'cash', label: 'Касса', children: <DataTable kind="cash" /> },
        { key: 'events', label: 'Журнал событий', children: <DataTable kind="events" /> },
      ]} />
    </>
  );
}
