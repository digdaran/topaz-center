import { SettingOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { App, Button, Card, Col, Form, InputNumber, Modal, Progress, Row, Table, Tag, Tooltip, Typography } from 'antd';
import { useState } from 'react';
import { api, useFiltered } from '../api/client';
import { useMe } from '../auth/auth';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, numCol, PageTitle, QueryState } from '../components/ui';
import { dateTime, dec, liters, pct } from '../lib/format';

type R = Record<string, any>;
const STATUS: Record<string, { color: string; label: string }> = {
  critical: { color: 'red', label: 'заказать срочно' },
  warning: { color: 'orange', label: 'скоро заказ' },
  ok: { color: 'green', label: 'достаточно' },
  idle: { color: 'default', label: 'нет продаж' },
};

export default function TanksPage() {
  const me = useMe();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useFiltered<{ tanks: R[]; history: R[]; receipts: R[] }>('/analytics/tanks');
  const [edit, setEdit] = useState<R | null>(null);
  const [form] = Form.useForm();

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
  return (
    <>
      <PageTitle title="Запасы топлива" />
      <FilterBar granularity={false} compare={false} />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            <Typography.Paragraph type="secondary">
              Остаток — по учёту Топаза (начало смены + приход − отпуск): замеры уровня при закрытии смен не вводятся. Расход — средний за последние 28 дней.
            </Typography.Paragraph>
            <Row gutter={[12, 12]}>
              {data.tanks.map((t) => (
                <Col key={`${t.station_id}/${t.tank_id}`} xs={24} sm={12} lg={8} xl={6}>
                  <Card size="small" title={<>{t.fuel_name} <Typography.Text type="secondary">· резервуар {t.tank_num}</Typography.Text></>}
                    extra={<Tag color={STATUS[t.status]?.color}>{STATUS[t.status]?.label}</Tag>}>
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t.station}</Typography.Text>
                    <Progress percent={Math.round((t.fill_share ?? 0) * 100)} status={t.status === 'critical' ? 'exception' : 'normal'}
                      strokeColor={t.status === 'warning' ? '#d48806' : undefined} format={() => liters(t.book_volume)} />
                    <div style={{ fontSize: 13, lineHeight: 1.7 }}>
                      Вместимость {liters(t.capacity)}, расход {liters(t.per_day)}/день<br />
                      Хватит на <b>{t.days_to_min != null ? `${dec(t.days_to_min, 1)} дн.` : '—'}</b>
                      {t.order_by && <> · заказать до <b>{t.order_by.split('-').reverse().join('.')}</b></>}<br />
                      {t.recommended_order > 0 && <>Рекомендуемый заказ: <b>{liters(t.recommended_order)}</b></>}
                    </div>
                    {me.role !== 'viewer' && (
                      <Button size="small" type="link" icon={<SettingOutlined />} style={{ padding: 0 }} onClick={() => { setEdit(t); form.setFieldsValue({ min_liters: Number(t.min_liters), lead_time_days: Number(t.lead_time_days), safety_days: Number(t.safety_days) }); }}>
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
              <Col xs={24}>
                <Card size="small" title="Приём топлива">
                  <Table size="small" rowKey={(r) => `${r.station_id}/${r.receipt_id}`} dataSource={data.receipts} pagination={{ pageSize: 15 }} scroll={{ x: 900 }}
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
          </>
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
