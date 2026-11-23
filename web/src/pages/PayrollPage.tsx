import { DeleteOutlined, DownloadOutlined, PlusOutlined, PrinterOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, App, Button, Card, Checkbox, Col, DatePicker, Drawer, Empty, Form, Input, InputNumber, Modal, Popconfirm, Row, Segmented, Select, Space,
  Switch, Table, Tag, Tooltip, Typography,
} from 'antd';
import dayjs, { Dayjs } from 'dayjs';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, useFilter } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { FigureList, numCol, QueryState } from '../components/ui';
import { filterQuery } from '../lib/filter';
import { dateTime, dec, liters, money } from '../lib/format';
import { ADJUSTMENT_KINDS, adjustmentLabel, monthLabel, PayScheme, schemeOn, schemeText, slipHtml } from '../lib/payroll';

type R = Record<string, any>;

const thisMonth = () => dayjs().format('YYYY-MM');

/** Деньги → Зарплаты (1.33): the month's payroll («Ведомость») and the schemes of pay («Схемы оплаты»). Owner only. */
export default function PayrollPage() {
  const [params, setParams] = useSearchParams();
  const view = params.get('view') === 'schemes' ? 'schemes' : 'sheet';
  const setView = (v: string) => {
    const p = new URLSearchParams(params);
    if (v === 'sheet') p.delete('view'); else p.set('view', v);
    setParams(p, { replace: true });
  };
  return (
    <>
      <Space wrap style={{ marginBottom: 4 }} align="start">
        <Segmented value={view} onChange={(v) => setView(String(v))}
          options={[{ value: 'sheet', label: 'Ведомость' }, { value: 'schemes', label: 'Схемы оплаты' }]} />
        <FilterBar period={false} granularity={false} compare={false} />
      </Space>
      {view === 'sheet' ? <Sheet onSchemes={() => setView('schemes')} /> : <Schemes />}
    </>
  );
}

// ---------------------------------------------------------------- the payroll

function Sheet({ onSchemes }: { onSchemes: () => void }) {
  const [filter] = useFilter();
  const [params, setParams] = useSearchParams();
  const month = params.get('month') ?? thisMonth();
  const setMonth = (m: string) => {
    const p = new URLSearchParams(params);
    p.set('month', m);
    setParams(p, { replace: true });
  };
  const qs = filterQuery(filter, { month });
  const { data, error, isLoading } = useQuery({ queryKey: ['/payroll', qs], queryFn: () => api<R>(`/payroll?${qs}`), placeholderData: (p) => p });
  const [slip, setSlip] = useState<{ station_id: string; login: string } | null>(null);
  const [adjust, setAdjust] = useState<{ station_id: string; login: string } | null>(null);
  const totals = data?.totals ?? {};

  return (
    <>
      <Space wrap style={{ marginBottom: 12 }}>
        <DatePicker picker="month" allowClear={false} format="MMMM YYYY" value={dayjs(month + '-01')}
          disabledDate={(d) => d.isAfter(dayjs(), 'month')} onChange={(d) => d && setMonth(d.format('YYYY-MM'))} />
        <Button icon={<DownloadOutlined />} href={`/api/v1/ui/payroll?${qs}&format=csv`}>Скачать CSV (Excel)</Button>
      </Space>
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            <Card size="small" style={{ marginBottom: 12 }}>
              <Row gutter={[24, 12]}>
                <Col xs={12} md={6}><Big label="Начислено за месяц" value={money(totals.total)} /></Col>
                <Col xs={12} md={6}><Big label="Из них премии − штрафы" value={money(Number(totals.bonus) - Number(totals.fine))} /></Col>
                <Col xs={12} md={6}><Big label="Выдано авансов" value={money(totals.advance)} /></Col>
                <Col xs={12} md={6}><Big label="Осталось выдать" value={money(totals.to_pay)} /></Col>
              </Row>
            </Card>
            {(data.stations as R[]).map((s) => (
              <StationSheet key={s.station_id} s={s} month={month} onSlip={(login) => setSlip({ station_id: s.station_id, login })}
                onAdjust={(login) => setAdjust({ station_id: s.station_id, login })} onSchemes={onSchemes} />
            ))}
          </>
        )}
      </QueryState>
      {slip && <SlipDrawer {...slip} month={month} onClose={() => setSlip(null)} />}
      {adjust && <AdjustmentModal {...adjust} month={month} onClose={() => setAdjust(null)} />}
    </>
  );
}

/** "−1 000,00 ₽", or a dash for nothing. */
const minus = (v: unknown) => (Number(v) ? `−${money(Number(v), true)}` : '—');

const Big = ({ label, value }: { label: string; value: string }) => (
  <>
    <Typography.Text type="secondary">{label}</Typography.Text>
    <div style={{ fontSize: 22, fontWeight: 600, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>{value}</div>
  </>
);

function StationSheet({ s, month, onSlip, onAdjust, onSchemes }: {
  s: R; month: string; onSlip: (login: string) => void; onAdjust: (login: string) => void; onSchemes: () => void;
}) {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const closed = !!s.closed_at;
  const ended = month < thisMonth();
  const run = async (path: string, body: unknown, ok: string) => {
    try {
      await api(path, { method: path.includes('payouts') ? 'PUT' : 'POST', body });
      message.success(ok);
      qc.invalidateQueries({ queryKey: ['/payroll'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const employees = s.employees as R[];

  return (
    <Card size="small" style={{ marginBottom: 12 }}
      title={<Space wrap>{s.station}
        {closed
          ? <Tooltip title={`${dateTime(s.closed_at)}${s.closed_by ? `, ${s.closed_by}` : ''}`}><Tag color="green">Месяц закрыт</Tag></Tooltip>
          : <Tag>Предварительно</Tag>}
      </Space>}
      extra={closed
        ? <Popconfirm title="Открыть месяц заново?" description="Суммы снова будут считаться по данным, отметки «Выплачено» снимутся."
            onConfirm={() => run('/payroll/reopen', { station_id: s.station_id, month }, 'Месяц открыт заново')}>
            <Button size="small">Открыть заново</Button>
          </Popconfirm>
        : <Tooltip title={!ended ? 'Закрыть месяц можно с 1-го числа следующего' : s.open_shifts > 0 ? 'Есть незакрытая смена' : 'Суммы зафиксируются; потом можно отмечать выплаты'}>
            <Popconfirm title="Закрыть месяц?" description="Суммы зафиксируются и не изменятся, даже если данные из Топаза придут позже."
              disabled={!ended || s.open_shifts > 0 || !employees.length}
              onConfirm={() => run('/payroll/close', { station_id: s.station_id, month }, 'Месяц закрыт')}>
              <Button size="small" type="primary" disabled={!ended || s.open_shifts > 0 || !employees.length}>Закрыть месяц</Button>
            </Popconfirm>
          </Tooltip>}>
      {!s.has_scheme && (
        <Alert type="info" showIcon style={{ marginBottom: 8 }} message="Схема оплаты не задана — зарплата на этой АЗС не начисляется"
          action={<Button size="small" onClick={onSchemes}>Задать</Button>} />
      )}
      {s.changed && (
        <Alert type="warning" showIcon style={{ marginBottom: 8 }}
          message={`После закрытия данные смен изменились: было начислено ${money(s.changed.was, true)}, сейчас было бы ${money(s.changed.now, true)}`}
          description="В ведомости — суммы на момент закрытия. Чтобы пересчитать, откройте месяц заново." />
      )}
      {s.open_shifts > 0 && <Typography.Paragraph type="secondary" style={{ marginBottom: 8 }}>Идёт смена — её выручка учтена на текущий момент.</Typography.Paragraph>}
      <Table size="small" rowKey="login" pagination={false} dataSource={employees} scroll={{ x: 'max-content' }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={`Смен за ${monthLabel(month)} нет`} /> }}
        columns={[
          { title: 'Сотрудник', dataIndex: 'login', fixed: 'left', render: (v, r) => <><Tooltip title="Расчётный листок"><a style={{ whiteSpace: 'nowrap' }} onClick={() => onSlip(v)}>{v}</a></Tooltip>{r.unpaid > 0 && <Tooltip title="Для части смен нет схемы оплаты на их дату"><Tag color="orange" style={{ marginLeft: 6 }}>без схемы: {r.unpaid}</Tag></Tooltip>}</> },
          { title: 'Смен', dataIndex: 'shifts', align: 'right' },
          numCol<R>('Часов', 'hours', (v) => dec(v, 0)),
          numCol<R>('Топливо', 'fuel_revenue', money),
          numCol<R>('Магазин', 'shop_revenue', money),
          numCol<R>('Фикс', 'fixed', (v) => (Number(v) ? money(v) : '—')),
          { title: 'Проценты и литры', key: 'pct', align: 'right', render: (_, r) => (Number(r.fuel_pay) + Number(r.shop_pay) + Number(r.liter_pay) ? money(Number(r.fuel_pay) + Number(r.shop_pay) + Number(r.liter_pay)) : '—') },
          { title: 'Премии − штрафы', key: 'adj', align: 'right', render: (_, r) => (Number(r.bonus) || Number(r.fine) ? money(Number(r.bonus) - Number(r.fine)) : '—') },
          { title: 'Начислено', dataIndex: 'total', align: 'right', render: (v) => <b>{money(v)}</b> },
          numCol<R>('Авансы', 'advance', (v) => (Number(v) ? money(v) : '—')),
          { title: 'К выдаче', dataIndex: 'to_pay', align: 'right', render: (v) => <b>{money(v)}</b> },
          {
            title: 'Выплачено', key: 'paid', render: (_, r) => (
              <Tooltip title={closed ? undefined : 'Отметить выплату можно после закрытия месяца'}>
                <Checkbox checked={!!r.paid} disabled={!closed}
                  onChange={(e) => run('/payroll/payouts', { station_id: s.station_id, month, login: r.login, paid: e.target.checked }, e.target.checked ? 'Отмечено' : 'Отметка снята')}>
                  {r.paid ? r.paid.paid_on.slice(0, 10).split('-').reverse().join('.') : ''}
                </Checkbox>
              </Tooltip>
            ),
          },
          {
            title: '', key: 'a', render: (_, r) => (
              <Tooltip title={closed ? 'Месяц закрыт' : 'Премия, штраф или аванс'}>
                <Button size="small" icon={<PlusOutlined />} disabled={closed} onClick={() => onAdjust(r.login)} aria-label="Премия, штраф или аванс" />
              </Tooltip>
            ),
          },
        ]}
        summary={() => employees.length > 1 ? (
          <Table.Summary.Row>
            <Table.Summary.Cell index={0}><b>Итого</b></Table.Summary.Cell>
            <Table.Summary.Cell index={1} colSpan={7} />
            <Table.Summary.Cell index={8} align="right"><b>{money(s.totals.total)}</b></Table.Summary.Cell>
            <Table.Summary.Cell index={9} align="right">{Number(s.totals.advance) ? money(s.totals.advance) : '—'}</Table.Summary.Cell>
            <Table.Summary.Cell index={10} align="right"><b>{money(s.totals.to_pay)}</b></Table.Summary.Cell>
            <Table.Summary.Cell index={11} colSpan={2} />
          </Table.Summary.Row>
        ) : null} />
    </Card>
  );
}

function AdjustmentModal({ station_id, login, month, onClose }: { station_id: string; login: string; month: string; onClose: () => void }) {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const today = dayjs();
  const inMonth = today.format('YYYY-MM') === month ? today : dayjs(month + '-01').endOf('month');
  const submit = async () => {
    const v = await form.validateFields();
    try {
      await api('/payroll/adjustments', { method: 'POST', body: { station_id, login, month, kind: v.kind, amount: v.amount, on_date: (v.on_date as Dayjs).format('YYYY-MM-DD'), comment: v.comment } });
      message.success('Добавлено');
      qc.invalidateQueries({ queryKey: ['/payroll'] });
      qc.invalidateQueries({ queryKey: ['payroll-slip'] });
      onClose();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  return (
    <Modal open title={`${login}: строка за ${monthLabel(month)}`} okText="Добавить" onOk={submit} onCancel={onClose} destroyOnClose>
      <Form form={form} layout="vertical" initialValues={{ kind: 'bonus', on_date: inMonth }}>
        <Form.Item name="kind"><Segmented options={ADJUSTMENT_KINDS} /></Form.Item>
        <Form.Item name="amount" label="Сумма, ₽" rules={[{ required: true, message: 'Укажите сумму' }]}>
          <InputNumber min={1} max={10_000_000} step={100} style={{ width: 200 }} />
        </Form.Item>
        <Form.Item name="on_date" label="Дата"><DatePicker format="DD.MM.YYYY" allowClear={false} /></Form.Item>
        <Form.Item name="comment" label="Комментарий"><Input maxLength={300} placeholder="За что" /></Form.Item>
      </Form>
    </Modal>
  );
}

function SlipDrawer({ station_id, login, month, onClose }: { station_id: string; login: string; month: string; onClose: () => void }) {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const qs = new URLSearchParams({ station: station_id, login, month }).toString();
  const { data, error, isLoading } = useQuery({ queryKey: ['payroll-slip', qs], queryFn: () => api<R>(`/payroll/slip?${qs}`) });
  const closed = !!data?.closed_at;
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['/payroll'] });
    qc.invalidateQueries({ queryKey: ['payroll-slip'] });
  };
  const act = async (fn: () => Promise<unknown>) => {
    try {
      await fn();
      refresh();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const print = () => {
    const w = window.open('', '_blank');
    if (!w) return message.error('Браузер не дал открыть окно печати');
    w.document.write(slipHtml(data!));
    w.document.close();
    w.focus();
    w.print();
  };
  const e = data?.employee ?? {};
  return (
    <Drawer open width={Math.min(1100, window.innerWidth)} onClose={onClose}
      title={<>Расчётный листок: {login} <Typography.Text type="secondary" style={{ fontWeight: 400 }}>· {data?.station} · {monthLabel(month)}</Typography.Text></>}
      extra={<Button icon={<PrinterOutlined />} disabled={!data} onClick={print}>Печать</Button>}>
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            {!closed && <Alert type="info" showIcon style={{ marginBottom: 12 }} message="Месяц не закрыт: суммы предварительные и пересчитываются по данным смен" />}
            <Row gutter={[24, 12]} style={{ marginBottom: 12 }}>
              <Col xs={24} md={12}>
                <FigureList rows={[
                  { key: 'f', label: 'Фикс за смены', value: money(e.fixed, true) },
                  { key: 'fp', label: `Процент с топлива (выручка ${money(e.fuel_revenue)})`, value: money(e.fuel_pay, true) },
                  { key: 'sp', label: `Процент с магазина (выручка ${money(e.shop_revenue)})`, value: money(e.shop_pay, true) },
                  { key: 'lp', label: `За литры (${liters(e.liters)})`, value: money(e.liter_pay, true) },
                ]} total={{ key: 'a', label: 'Начислено по схеме', value: money(e.accrued, true) }} />
              </Col>
              <Col xs={24} md={12}>
                <FigureList rows={[
                  { key: 'b', label: 'Премии', value: Number(e.bonus) ? money(e.bonus, true) : '—' },
                  { key: 'fi', label: 'Штрафы', value: minus(e.fine) },
                  { key: 't', label: 'Итого начислено', value: money(e.total, true), strong: true },
                  { key: 'ad', label: 'Авансы', value: minus(e.advance) },
                ]} total={{ key: 'p', label: 'К выдаче', value: money(e.to_pay, true) }} />
              </Col>
            </Row>
            {(data.schemes as R[]).length > 0 && (
              <Typography.Paragraph type="secondary">
                Схема: {(data.schemes as R[]).map((s) => `с ${s.valid_from.slice(0, 10).split('-').reverse().join('.')} — ${schemeText(s as PayScheme)}`).join('; ')}
              </Typography.Paragraph>
            )}
            <Table size="small" rowKey="session_id" pagination={false} dataSource={data.shifts} scroll={{ x: 1000 }}
              columns={[
                { title: 'Дата', dataIndex: 'local_date', render: (v) => v.slice(0, 10).split('-').reverse().join('.') },
                { title: 'Смена', dataIndex: 'session_num', render: (v, r) => <Link to={`/shifts/${station_id}/${r.session_id}`}>№{v ?? r.session_id}</Link> },
                numCol<R>('Часов', 'hours', (v) => dec(v, 1)),
                numCol<R>('Топливо', 'fuel_revenue', money),
                numCol<R>('Литры', 'liters', liters),
                { title: 'Магазин', key: 'shop', align: 'right', render: (_, r) => (Number(r.shop_base) !== Number(r.shop_revenue)
                  ? <Tooltip title={`С процентом: ${money(r.shop_base, true)} (без исключённых групп товаров)`}>{money(r.shop_revenue)}*</Tooltip> : money(r.shop_revenue)) },
                { title: 'Фикс', key: 'fixed', align: 'right', render: (_, r) => <>{money(r.fixed)}{Number(r.fixed_share) > 0 && Number(r.fixed_share) < 1 && <Typography.Text type="secondary"> ({Math.round(Number(r.fixed_share) * 100)} %)</Typography.Text>}</> },
                { title: 'Проценты и литры', key: 'p', align: 'right', render: (_, r) => money(Number(r.fuel_pay) + Number(r.shop_pay) + Number(r.liter_pay)) },
                { title: 'Итого', dataIndex: 'total', align: 'right', render: (v) => <b>{money(v, true)}</b> },
                {
                  title: 'Отметки', key: 'm', render: (_, r) => (
                    <Space size={4} wrap>
                      {r.open && <Tag color="processing">идёт</Tag>}
                      {r.short && <Tooltip title="Смена короче, чем задано в схеме: без фикса"><Tag>короткая</Tag></Tooltip>}
                      {r.scheme_id == null && !r.excluded && <Tag color="orange">нет схемы</Tag>}
                      {r.alerts > 0 && <Link to={`/shifts/${station_id}/${r.session_id}`}><Tag color="red">событий: {r.alerts}</Tag></Link>}
                    </Space>
                  ),
                },
                {
                  title: <Tooltip title="Смена не оплачивается: например, открыта по ошибке">Не оплачивать</Tooltip>, key: 'x', align: 'center',
                  render: (_, r) => <Checkbox checked={!!r.excluded} disabled={closed}
                    onChange={(ev) => act(() => api('/payroll/exclusions', { method: 'PUT', body: { station_id, session_id: r.session_id, excluded: ev.target.checked } }))} />,
                },
              ]} />
            <Typography.Title level={5} style={{ marginTop: 16 }}>Премии, штрафы и авансы</Typography.Title>
            <Table size="small" rowKey="id" pagination={false} dataSource={data.adjustments}
              locale={{ emptyText: 'Нет' }}
              columns={[
                { title: 'Дата', dataIndex: 'on_date', render: (v) => v.slice(0, 10).split('-').reverse().join('.') },
                { title: 'Строка', dataIndex: 'kind', render: (v) => <Tag color={v === 'bonus' ? 'green' : v === 'fine' ? 'red' : 'blue'}>{adjustmentLabel(v)}</Tag> },
                { title: 'Комментарий', dataIndex: 'comment' },
                numCol<R>('Сумма', 'amount', (v) => money(v, true)),
                { title: 'Внёс', dataIndex: 'created_by' },
                {
                  title: '', key: 'd', render: (_, r) => !closed && (
                    <Popconfirm title="Удалить строку?" onConfirm={() => act(() => api(`/payroll/adjustments/${r.id}`, { method: 'DELETE' }))}>
                      <Button size="small" type="text" icon={<DeleteOutlined />} />
                    </Popconfirm>
                  ),
                },
              ]} />
          </>
        )}
      </QueryState>
    </Drawer>
  );
}

// ---------------------------------------------------------------- schemes of pay

function Schemes() {
  const [filter] = useFilter();
  const qs = filterQuery(filter);
  const { data, error, isLoading } = useQuery({ queryKey: ['/payroll/schemes', qs], queryFn: () => api<R[]>(`/payroll/schemes?${qs}`) });
  const [edit, setEdit] = useState<{ station: R; login: string | null; base?: R } | null>(null);
  return (
    <QueryState error={error} loading={isLoading}>
      <Typography.Paragraph type="secondary" style={{ maxWidth: 900 }}>
        У каждой АЗС своя схема оплаты: фикс за смену, процент с выручки за топливо и магазин, рубли за литр — в любом сочетании.
        Сотруднику можно задать личную схему. Новая схема действует с выбранной даты; смены считаются по схеме, которая действовала в день их открытия.
      </Typography.Paragraph>
      {(data ?? []).map((st) => <StationSchemes key={st.station_id} st={st} onEdit={(login, base) => setEdit({ station: st, login, base })} />)}
      {edit && <SchemeModal {...edit} onClose={() => setEdit(null)} />}
    </QueryState>
  );
}

function StationSchemes({ st, onEdit }: { st: R; onEdit: (login: string | null, base?: R) => void }) {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const today = dayjs().format('YYYY-MM-DD');
  const current = schemeOn(st.schemes as R[] as PayScheme[], today);
  const groupName = (id: number) => (st.groups as R[]).find((g) => g.id === id)?.name ?? `#${id}`;
  const act = async (fn: () => Promise<unknown>) => {
    try {
      await fn();
      qc.invalidateQueries({ queryKey: ['/payroll/schemes'] });
      qc.invalidateQueries({ queryKey: ['/payroll'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const remove = (id: number) => act(() => api(`/payroll/schemes/${id}`, { method: 'DELETE' }));
  const history = (rows: R[]) => (
    <Table size="small" rowKey="id" pagination={false} dataSource={rows} scroll={{ x: 800 }}
      columns={[
        { title: 'Действует с', dataIndex: 'valid_from', render: (v) => v.slice(0, 10).split('-').reverse().join('.') },
        { title: 'Оплата', key: 't', render: (_, r) => schemeText(r as PayScheme) },
        { title: 'Без фикса, если смена короче', dataIndex: 'min_shift_hours', render: (v) => `${dec(v, 1)} ч` },
        { title: 'Группы без процента', dataIndex: 'excluded_groups', render: (v: number[]) => (v.length ? v.map(groupName).join(', ') : '—') },
        {
          title: '', key: 'a', render: (_, r) => (
            <Space size={4}>
              <Button size="small" onClick={() => onEdit(r.login ?? null, r)}>Изменить</Button>
              <Popconfirm title="Удалить схему?" description="Закрытые месяцы не изменятся." onConfirm={() => remove(r.id)}>
                <Button size="small" type="text" icon={<DeleteOutlined />} />
              </Popconfirm>
            </Space>
          ),
        },
      ]} />
  );
  return (
    <Card size="small" title={st.station} style={{ marginBottom: 12 }}
      extra={<Button size="small" type="primary" icon={<PlusOutlined />} onClick={() => onEdit(null, current)}>{st.schemes.length ? 'Новая схема АЗС' : 'Задать схему АЗС'}</Button>}>
      {st.schemes.length === 0
        ? <Alert type="info" showIcon message="Схема не задана — зарплата на этой АЗС не начисляется" style={{ marginBottom: 12 }} />
        : <>
            <Typography.Paragraph>Сейчас: <b>{current ? schemeText(current) : 'ещё не действует'}</b></Typography.Paragraph>
            {history(st.schemes)}
          </>}
      <Typography.Title level={5} style={{ marginTop: 16 }}>Сотрудники (пользователи Топаза)</Typography.Title>
      <Table<R> size="small" rowKey="login" pagination={false} dataSource={st.employees as R[]} scroll={{ x: 800 }}
        locale={{ emptyText: 'Топаз ещё не прислал пользователей этой АЗС' }}
        expandable={{
          rowExpandable: (r) => r.schemes.length > 0,
          expandedRowRender: (r) => history(r.schemes),
        }}
        columns={[
          { title: 'Логин в Топазе', dataIndex: 'login' },
          { title: 'Последняя смена', dataIndex: 'last_shift', render: (v) => (v ? dateTime(v) : '—') },
          {
            title: 'Схема', key: 's', render: (_, r) => {
              if (r.no_pay) return <Typography.Text type="secondary">не начисляется</Typography.Text>;
              const own = schemeOn(r.schemes as PayScheme[], today);
              return own ? <Tag color="blue">личная: {schemeText(own)}</Tag> : <Typography.Text type="secondary">как у АЗС</Typography.Text>;
            },
          },
          {
            title: <Tooltip title="Например, логин администратора или общий логин: его продажи остаются в отчётах, но в ведомость он не попадает">Не начислять зарплату</Tooltip>,
            key: 'n', align: 'center',
            render: (_, r) => <Switch size="small" checked={r.no_pay}
              onChange={(v) => act(() => api('/payroll/employees', { method: 'PUT', body: { station_id: st.station_id, login: r.login, no_pay: v } }))} />,
          },
          { title: '', key: 'a', render: (_, r) => <Button size="small" disabled={r.no_pay} onClick={() => onEdit(r.login, schemeOn(r.schemes as PayScheme[], today) ?? current)}>Личная схема</Button> },
        ]} />
    </Card>
  );
}

function SchemeModal({ station, login, base, onClose }: { station: R; login: string | null; base?: R; onClose: () => void }) {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const editing = base && (base.login ?? null) === login;
  const submit = async () => {
    const v = await form.validateFields();
    try {
      await api('/payroll/schemes', {
        method: 'POST',
        body: {
          station_id: station.station_id, login, valid_from: (v.valid_from as Dayjs).format('YYYY-MM-DD'),
          shift_fixed: v.shift_fixed ?? 0, fuel_pct: v.fuel_pct ?? 0, shop_pct: v.shop_pct ?? 0, per_liter: v.per_liter ?? 0,
          min_shift_hours: v.min_shift_hours ?? 1, excluded_groups: v.excluded_groups ?? [],
        },
      });
      message.success('Схема сохранена');
      qc.invalidateQueries({ queryKey: ['/payroll/schemes'] });
      qc.invalidateQueries({ queryKey: ['/payroll'] });
      onClose();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  return (
    <Modal open title={login ? `Личная схема: ${login} (${station.station})` : `Схема оплаты АЗС ${station.station}`} okText="Сохранить" onOk={submit} onCancel={onClose} destroyOnClose width={560}>
      <Form form={form} layout="vertical" initialValues={{
        valid_from: editing ? dayjs(base!.valid_from.slice(0, 10)) : dayjs().startOf('month'),
        shift_fixed: base?.shift_fixed ?? 0, fuel_pct: base?.fuel_pct ?? 0, shop_pct: base?.shop_pct ?? 0, per_liter: base?.per_liter ?? 0,
        min_shift_hours: base?.min_shift_hours ?? 1, excluded_groups: base?.excluded_groups ?? [],
      }}>
        <Form.Item name="valid_from" label="Действует с" extra="Смены, открытые с этого дня, считаются по этой схеме. Та же дата — схема заменяется.">
          <DatePicker format="DD.MM.YYYY" allowClear={false} />
        </Form.Item>
        <Row gutter={12}>
          <Col span={12}><Form.Item name="shift_fixed" label="Фикс за смену, ₽"><InputNumber min={0} max={1_000_000} step={100} style={{ width: '100%' }} /></Form.Item></Col>
          <Col span={12}><Form.Item name="per_liter" label="За литр, ₽"><InputNumber min={0} max={1000} step={0.1} style={{ width: '100%' }} /></Form.Item></Col>
          <Col span={12}><Form.Item name="fuel_pct" label="% с выручки за топливо"><InputNumber min={0} max={100} step={0.5} style={{ width: '100%' }} /></Form.Item></Col>
          <Col span={12}><Form.Item name="shop_pct" label="% с выручки магазина"><InputNumber min={0} max={100} step={1} style={{ width: '100%' }} /></Form.Item></Col>
        </Row>
        <Form.Item name="min_shift_hours" label="Не платить фикс, если смена короче, ч" extra="Для смен, открытых по ошибке или сразу закрытых. Проценты за такую смену начисляются.">
          <InputNumber min={0} max={48} step={0.5} style={{ width: 200 }} />
        </Form.Item>
        <Form.Item name="excluded_groups" label="Группы товаров без процента" extra="Например, сигареты: с них процент обычно не платят.">
          <Select mode="multiple" allowClear placeholder="Процент со всех товаров" optionFilterProp="label"
            options={(station.groups as R[]).map((g) => ({ value: g.id, label: g.name }))} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
