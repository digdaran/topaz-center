import { SettingOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { App, Button, Card, Col, Empty, Flex, Form, InputNumber, Modal, Progress, Row, Segmented, Table, Tag, theme, Tooltip, Typography } from 'antd';
import { useState } from 'react';
import { api, useFiltered } from '../api/client';
import { useMe } from '../auth/auth';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, FigureList, numCol, QueryState } from '../components/ui';
import { dateTime, dec, int, liters, pct } from '../lib/format';

type R = Record<string, any>;
const STATUS: Record<string, { color: string; label: string }> = {
  critical: { color: 'red', label: 'заказать срочно' },
  warning: { color: 'orange', label: 'скоро заказ' },
  ok: { color: 'green', label: 'достаточно' },
  idle: { color: 'default', label: 'нет продаж' },
};

export default function TanksPage({ view = 'tanks' }: { view?: 'tanks' | 'receipts' | 'losses' }) {
  const me = useMe();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useFiltered<{ tanks: R[]; history: R[]; receipts: R[]; measures: R[]; losses: R[] }>('/analytics/tanks');
  const [edit, setEdit] = useState<R | null>(null);
  const [form] = Form.useForm();
  const [status, setStatus] = useState<string>('all');
  const { token } = theme.useToken();

  const save = async () => {
    const v = await form.validateFields();
    try {
      await api(`/tanks/${edit!.station_id}/${edit!.tank_id}`, { method: 'PUT', body: v });
      setEdit(null);
      qc.invalidateQueries();
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  // At most 6 lines stay readable: the most urgent tanks first; pick a station in the filter to see all of its tanks.
  const urgency: Record<string, number> = { critical: 0, warning: 1, ok: 2, idle: 3 };
  const allSeries = [...new Set((data?.history ?? []).map((h) => `${h.station_id}/${h.tank_id}`))];
  const series = [...allSeries].sort((a, b) => {
    const ta = data!.tanks.find((t) => `${t.station_id}/${t.tank_id}` === a);
    const tb = data!.tanks.find((t) => `${t.station_id}/${t.tank_id}` === b);
    return (urgency[ta?.status] ?? 9) - (urgency[tb?.status] ?? 9) || Number(ta?.days_to_min ?? 999) - Number(tb?.days_to_min ?? 999);
  }).slice(0, 6);
  // The most urgent first: what to order, then the rest by days left.
  const sorted = [...(data?.tanks ?? [])].sort((a, b) => (urgency[a.status] ?? 9) - (urgency[b.status] ?? 9) || Number(a.days_to_min ?? 999) - Number(b.days_to_min ?? 999));
  const counts: Record<string, number> = {};
  for (const t of data?.tanks ?? []) counts[t.status] = (counts[t.status] ?? 0) + 1;
  const rc = data?.receipts ?? [];
  const docL = rc.reduce((a, r) => a + Math.max(Number(r.doc_volume ?? 0), 0), 0);
  const gotL = rc.reduce((a, r) => a + Number(r.measured_volume ?? r.doc_volume ?? 0), 0);
  const short = rc.filter((r) => Number(r.shortage_pct ?? 0) > 0.5);
  return (
    <>
      <FilterBar granularity={false} compare={false} />
      <QueryState error={error} loading={isLoading}>
        {data && view === 'tanks' && (
          <>
            <Typography.Paragraph type="secondary">
              Остаток — по учёту Топаза: начало смены + приход − отпуск. В резервуарах с электронным уровнемером Топаз в начале каждой смены
              берёт остаток по замеру, поэтому там он точный; в резервуарах без уровнемера (виртуальных) — только расчётный. Расход — средний за последние 28 дней.
            </Typography.Paragraph>
            <Segmented<string> style={{ marginBottom: 12 }} value={status} onChange={setStatus}
              options={[
                { value: 'all', label: `Все (${data.tanks.length})` },
                ...(['critical', 'warning', 'ok', 'idle'] as const).filter((k) => counts[k]).map((k) => ({ value: k, label: `${STATUS[k].label} (${counts[k]})` })),
              ]} />
            <Row gutter={[12, 12]}>
              {sorted.filter((t) => status === 'all' || t.status === status).map((t) => (
                <Col key={`${t.station_id}/${t.tank_id}`} xs={24} sm={12} lg={8} xl={6}>
                  <Card size="small" style={{ height: '100%' }}
                    title={<>{t.fuel_name} <Typography.Text type="secondary" style={{ fontWeight: 400 }}>· рез. {t.tank_num}</Typography.Text></>}
                    extra={<Tag color={STATUS[t.status]?.color} style={{ marginInlineEnd: 0 }}>{STATUS[t.status]?.label}</Tag>}>
                    <Flex justify="space-between" align="center" gap={8}>
                      <Typography.Text type="secondary" ellipsis style={{ fontSize: 12 }}>{t.station}</Typography.Text>
                      {t.has_gauge
                        ? <Tooltip title="Остаток уточняется замером уровнемера при каждом закрытии смены"><Tag color="blue" style={{ fontSize: 11, marginInlineEnd: 0 }}>уровнемер</Tag></Tooltip>
                        : <Tooltip title="Уровнемера нет: остаток расчётный, по учёту Топаза"><Tag style={{ fontSize: 11, marginInlineEnd: 0 }}>по учёту</Tag></Tooltip>}
                    </Flex>
                    <div style={{ fontSize: 22, fontWeight: 600, fontVariantNumeric: 'tabular-nums', marginTop: 4 }}>
                      {liters(t.book_volume)} <Typography.Text type="secondary" style={{ fontSize: 13, fontWeight: 400 }}>из {liters(t.capacity)}</Typography.Text>
                    </div>
                    <Progress percent={Math.round((t.fill_share ?? 0) * 100)} size="small" status={t.status === 'critical' ? 'exception' : 'normal'}
                      strokeColor={t.status === 'warning' ? token.colorWarning : undefined} />
                    <FigureList rows={[
                      { key: 'd', label: 'Расход в день', value: liters(t.per_day), hint: 'Средний за последние 28 дней' },
                      { key: 'h', label: 'Хватит на', value: t.days_to_min != null ? `${dec(t.days_to_min, 1)} дн.` : '—', strong: true, hint: 'До минимального остатка' },
                      ...(t.order_by ? [{ key: 'o', label: 'Заказать до', value: t.order_by.split('-').reverse().join('.'), strong: t.status !== 'ok' }] : []),
                      ...(t.recommended_order > 0 ? [{ key: 'r', label: 'Рекомендуемый заказ', value: liters(t.recommended_order) }] : []),
                      ...(t.last_fact_volume != null ? [{
                        key: 'f', label: `Замер ${dateTime(t.last_fact_at).slice(0, 10)}`, value: <>{liters(t.last_fact_volume)}{Number(t.last_fact_diff) !== 0 && <Typography.Text type={Number(t.last_fact_diff) < 0 ? 'danger' : 'secondary'} style={{ fontSize: 12, marginLeft: 6 }}>{Number(t.last_fact_diff) > 0 ? '+' : ''}{liters(t.last_fact_diff)}</Typography.Text>}</>,
                        hint: 'Последний замер при закрытии смены и разница с учётом Топаза',
                      }] : []),
                    ]} />
                    {me.role !== 'viewer' && (
                      <Button size="small" type="link" icon={<SettingOutlined />} style={{ padding: 0, marginTop: 6 }} onClick={() => { setEdit(t); form.setFieldsValue({ min_liters: Number(t.min_liters), lead_time_days: Number(t.lead_time_days), safety_days: Number(t.safety_days) }); }}>
                        Минимум {liters(Math.max(Number(t.min_liters), Number(t.dead_rest)))}, поставка {dec(t.lead_time_days, 0)} + {dec(t.safety_days, 0)} дн.
                      </Button>
                    )}
                  </Card>
                </Col>
              ))}
            </Row>
            <Row gutter={[12, 12]} style={{ marginTop: 12 }}>
              <Col xs={24}>
                <ChartCard title={allSeries.length > series.length ? `Остаток на конец смен: ${series.length} самых срочных из ${allSeries.length} резервуаров (выберите АЗС в фильтре, чтобы увидеть все)` : 'Остаток в резервуарах на конец смен'} empty={!data.history.length} option={{
                  color: CHART_COLORS,
                  tooltip: { trigger: 'axis', valueFormatter: (v: any) => liters(Number(v)) },
                  legend: { top: 0, type: 'scroll' },
                  grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                  xAxis: { type: 'time' },
                  yAxis: { type: 'value', axisLabel: { formatter: (v: number) => `${Math.round(v / 1000)} т.л` } },
                  series: series.map((k) => {
                    const pts = data.history.filter((h) => `${h.station_id}/${h.tank_id}` === k);
                    const t = data.tanks.find((x) => `${x.station_id}/${x.tank_id}` === k);
                    return { name: `${t?.station ?? ''} · ${pts[0]?.fuel_name} (${pts[0]?.tank_num})`, type: 'line', step: 'end', symbol: 'none', data: pts.map((h) => [h.at, h.book_volume]) };
                  }),
                }} />
              </Col>
            </Row>
          </>
        )}
        {data && view === 'losses' && (
          <Row gutter={[12, 12]}>
              {!data.losses.length && !data.measures.length && (
                <Col xs={24}><Empty description="Замеров при закрытии смен за период нет: потери считаются только по резервуарам с уровнемером или ручным замером" /></Col>
              )}
              {data.losses.length > 0 && (
                <Col xs={24}>
                  <Card size="small" title="Потери по замерам за период">
                    <Typography.Paragraph type="secondary" style={{ marginTop: 0 }}>
                      Разница между замером при закрытии смены и учётом Топаза: недостача — топлива меньше, чем по учёту (испарение, неточная
                      градуировка, утечка или недолив); излишек — больше. Крупные излишки (порог правила «Излишек по замеру») обычно означают слив,
                      не оформленный в Топазе приходом: они показаны отдельно и в потери не входят.
                    </Typography.Paragraph>
                    <Table size="small" rowKey={(r) => `${r.station_id}/${r.tank_id}`} dataSource={data.losses} pagination={false} scroll={{ x: 900 }}
                      columns={[
                        { title: 'АЗС', dataIndex: 'station' },
                        { title: 'Резервуар', dataIndex: 'tank_num', render: (v, r) => `${v} · ${r.fuel_name ?? ''}` },
                        numCol<R>('Замеров', 'measurements', int),
                        numCol<R>('Отпущено', 'pumped', liters),
                        numCol<R>('Недостача', 'shortage', liters),
                        numCol<R>('Излишки', 'surplus', liters),
                        {
                          ...numCol<R>('Итог', 'net', liters),
                          render: (v: number, r) => <Tooltip title={`${pct(Number(r.net_pct) / 100)} от отпущенного`}><Typography.Text type={Number(v) < 0 ? 'danger' : undefined}>{liters(v)}</Typography.Text></Tooltip>,
                        },
                        {
                          ...numCol<R>('Крупные излишки', 'big_surplus', liters),
                          render: (v: number, r) => (Number(r.big_surplus_count) > 0
                            ? <Tooltip title="Похоже на неоформленные сливы: проверьте приходы в Топазе"><Tag color="orange">{int(r.big_surplus_count)} × {liters(v)}</Tag></Tooltip> : '—'),
                        },
                      ]} />
                  </Card>
                </Col>
              )}
              {data.measures.length > 0 && (
                <Col xs={24}>
                  <ChartCard title="Потери по замерам нарастающим итогом (без крупных излишков)" empty={!data.measures.length} option={{
                    color: CHART_COLORS,
                    tooltip: { trigger: 'axis', valueFormatter: (v: any) => liters(Number(v)) },
                    legend: { top: 0, type: 'scroll' },
                    grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                    xAxis: { type: 'time' },
                    yAxis: { type: 'value', axisLabel: { formatter: (v: number) => `${Math.round(v)} л` } },
                    series: [...new Set(data.measures.map((m) => `${m.station_id}/${m.tank_id}`))].map((k) => {
                      let sum = 0;
                      const pts = data.measures.filter((m) => `${m.station_id}/${m.tank_id}` === k);
                      return {
                        name: `${pts[0].station} · ${pts[0].fuel_name ?? ''} (${pts[0].tank_num})`, type: 'line', step: 'end', symbol: 'none',
                        data: pts.map((m) => { if (!m.big_surplus) sum += Number(m.diff); return [m.ended_at, Math.round(sum * 100) / 100]; }),
                      };
                    }),
                  }} />
                </Col>
              )}
              {data.measures.length > 0 && (
                <Col xs={24}>
                  <Card size="small" title="Учёт и замер по сменам">
                    <Table size="small" rowKey={(r) => `${r.station_id}/${r.tank_id}/${r.session_id}`} pagination={{ pageSize: 15 }} scroll={{ x: 900 }}
                      dataSource={[...data.measures].reverse()}
                      columns={[
                        { title: 'Конец смены', dataIndex: 'ended_at', render: (v) => dateTime(v) },
                        { title: 'АЗС', dataIndex: 'station' },
                        { title: 'Смена', dataIndex: 'session_num', render: (v) => `№${v}` },
                        { title: 'Резервуар', dataIndex: 'tank_num', render: (v, r) => `${v} · ${r.fuel_name ?? ''}` },
                        numCol<R>('Отпущено', 'pump_volume', liters),
                        numCol<R>('По учёту', 'book_volume', liters),
                        numCol<R>('По замеру', 'fact_volume', liters),
                        {
                          ...numCol<R>('Разница', 'diff', liters),
                          render: (v: number, r) => (r.big_surplus ? <Tooltip title="Крупный излишек: проверьте, оформлен ли слив"><Tag color="orange">+{liters(v)}</Tag></Tooltip>
                            : <Typography.Text type={Number(v) < 0 ? 'danger' : undefined}>{Number(v) > 0 ? '+' : ''}{liters(v)}</Typography.Text>),
                        },
                        numCol<R>('Плотность', 'density', (v) => (v == null ? '—' : dec(v, 1))),
                      ]} />
                  </Card>
                </Col>
              )}
          </Row>
        )}
        {data && view === 'receipts' && (
          <Row gutter={[12, 12]}>
              <Col xs={24} md={12} xl={8}>
                <Card size="small" title="Приём за период" style={{ height: '100%' }}>
                  <FigureList rows={[
                    { key: 'n', label: 'Сливов', value: int(rc.length) },
                    { key: 'd', label: 'По документам', value: liters(docL) },
                    { key: 'g', label: 'Принято по замеру', value: liters(gotL), hint: 'Где замера нет — по документу' },
                    { key: 's', label: 'Разница', value: <Typography.Text type={gotL - docL < -0.5 ? 'danger' : undefined}>{gotL - docL > 0 ? '+' : ''}{liters(gotL - docL)}</Typography.Text>, strong: true },
                    { key: 'k', label: 'Сливов с недостачей больше 0,5 %', value: short.length ? <Tag color="orange" style={{ marginInlineEnd: 0 }}>{short.length}</Tag> : '0' },
                  ]} />
                </Card>
              </Col>
              <Col xs={24}>
                <Card size="small" title="Все сливы">
                  <Table size="small" rowKey={(r) => `${r.station_id}/${r.receipt_id}`} dataSource={data.receipts} pagination={{ pageSize: 15 }} scroll={{ x: 'max-content' }}
                    locale={{ emptyText: 'Приёмов за период нет' }}
                    columns={[
                      { title: 'Когда', dataIndex: 'received_at', render: (v) => dateTime(v) },
                      { title: 'АЗС', dataIndex: 'station' },
                      { title: 'Топливо', dataIndex: 'fuel_name', render: (v, r) => `${v ?? ''} (рез. ${r.tank_num ?? '?'})` },
                      { title: 'Документ', dataIndex: 'doc_number' },
                      { title: 'Машина', dataIndex: 'car_number' },
                      numCol<R>('По документу', 'doc_volume', liters),
                      numCol<R>('Принято', 'measured_volume', liters),
                      {
                        ...numCol<R>('Недостача', 'shortage_pct', pct),
                        render: (v: number | null, r) => (r.doc_volume < 0 ? <Tag color="red">отрицательный приём</Tag>
                          : v != null && v > 0.5 ? <Tooltip title={`${liters(r.doc_volume - (r.measured_volume ?? r.doc_volume + r.discrepancy))}`}><Tag color="orange">{pct(v / 100)}</Tag></Tooltip>
                          : '—'),
                      },
                    ]} />
                </Card>
              </Col>
          </Row>
        )}
      </QueryState>
      <Modal open={!!edit} title={`Резервуар ${edit?.tank_num} · ${edit?.fuel_name} · ${edit?.station}`} onOk={save} onCancel={() => setEdit(null)} okText="Сохранить" destroyOnClose>
        <Form form={form} layout="vertical">
          <Form.Item name="min_liters" label="Минимальный остаток, л" extra={`Мёртвый остаток резервуара по Топазу: ${liters(edit?.dead_rest)}. Берётся большее.`}><InputNumber min={0} step={100} style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="lead_time_days" label="Срок поставки, дней"><InputNumber min={0} max={60} step={0.5} style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="safety_days" label="Страховой запас, дней"><InputNumber min={0} max={60} step={0.5} style={{ width: '100%' }} /></Form.Item>
        </Form>
      </Modal>
    </>
  );
}
