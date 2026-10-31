import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Card, Col, DatePicker, InputNumber, Progress, Row, Select, Table, Typography } from 'antd';
import dayjs from 'dayjs';
import { useState } from 'react';
import { api } from '../api/client';
import { useMe } from '../auth/auth';
import { ChartCard, CHART_COLORS, numCol, PageTitle, QueryState } from '../components/ui';
import { bucketLabel, compact, liters, money, Num, pct } from '../lib/format';

type R = Record<string, any>;
const METRIC: Record<string, { label: string; fmt: (v: Num) => string }> = {
  fuel_liters: { label: 'Топливо, литры', fmt: liters },
  fuel_revenue: { label: 'Выручка топливо', fmt: money },
  shop_revenue: { label: 'Выручка магазин', fmt: money },
};

export default function PlansPage() {
  const me = useMe();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [month, setMonth] = useState(dayjs().format('YYYY-MM'));
  const [metric, setMetric] = useState('fuel_liters');
  const { data, error, isLoading } = useQuery({ queryKey: ['plans', month], queryFn: () => api<{ rows: R[] }>(`/plans?month=${month}`) });
  const rows = (data?.rows ?? []).filter((r) => r.metric === metric && r.fuel_type === '');
  const fmt = METRIC[metric].fmt;
  const canEdit = me.role !== 'viewer';

  const save = async (r: R, value: number | null) => {
    try {
      await api('/plans', { method: 'PUT', body: { station_id: r.station_id, month, metric, fuel_type: '', value } });
      qc.invalidateQueries({ queryKey: ['plans', month] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  const total = rows.reduce((a, r) => ({ plan: a.plan + Number(r.plan ?? 0), fact: a.fact + Number(r.fact), forecast: a.forecast + Number(r.forecast) }), { plan: 0, fact: 0, forecast: 0 });
  const track = rows[0]?.track?.map((_: R, i: number) => ({
    day: rows[0].track[i].day,
    fact: rows.some((r) => r.track[i].fact != null) ? rows.reduce((a, r) => a + Number(r.track[i].fact ?? 0), 0) : null,
    plan: rows.some((r) => r.plan) ? rows.reduce((a, r) => a + Number(r.track[i].plan ?? 0), 0) : null,
  })) ?? [];

  return (
    <>
      <PageTitle title="План-факт" extra={
        <>
          <Select style={{ width: 200 }} value={metric} onChange={setMetric} options={Object.entries(METRIC).map(([k, v]) => ({ value: k, label: v.label }))} />{' '}
          <DatePicker picker="month" allowClear={false} format="MMMM YYYY" value={dayjs(month + '-01')} onChange={(d) => d && setMonth(d.format('YYYY-MM'))} />
        </>
      } />
      <QueryState error={error} loading={isLoading}>
        <Row gutter={[12, 12]}>
          <Col xs={24} lg={8}>
            <Card size="small" title="Сеть за месяц" style={{ height: '100%' }}>
              <Typography.Paragraph>План: <b>{total.plan ? fmt(total.plan) : 'не задан'}</b></Typography.Paragraph>
              <Typography.Paragraph>Факт: <b>{fmt(total.fact)}</b></Typography.Paragraph>
              <Typography.Paragraph>Прогноз на конец месяца: <b>{fmt(total.forecast)}</b> <Typography.Text type="secondary">(оценка)</Typography.Text></Typography.Paragraph>
              {total.plan > 0 && <Progress percent={Math.round((total.fact / total.plan) * 100)} success={{ percent: Math.min(100, Math.round((total.forecast / total.plan) * 100)) }} />}
            </Card>
          </Col>
          <Col xs={24} lg={16}>
            <ChartCard title="Нарастающим итогом: факт и план" empty={!track.length} height={260} option={{
              color: [CHART_COLORS[0], CHART_COLORS[3]],
              tooltip: { trigger: 'axis', valueFormatter: (v: any) => (v == null ? '—' : fmt(Number(v))) },
              legend: { top: 0 },
              grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
              xAxis: { type: 'category', data: track.map((t: R) => bucketLabel(t.day, 'day').slice(0, 5)) },
              yAxis: { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
              series: [
                { name: 'Факт', type: 'line', areaStyle: { opacity: 0.15 }, data: track.map((t: R) => t.fact) },
                { name: 'План (равномерно)', type: 'line', lineStyle: { type: 'dashed' }, symbol: 'none', data: track.map((t: R) => t.plan) },
              ],
            }} />
          </Col>
          <Col xs={24}>
            <Card size="small">
              <Table size="small" rowKey="station_id" pagination={false} dataSource={rows} scroll={{ x: 900 }}
                columns={[
                  { title: 'АЗС', dataIndex: 'station', fixed: 'left', width: 200 },
                  {
                    title: 'План', dataIndex: 'plan', align: 'right', render: (v: number | null, r) => canEdit ? (
                      <InputNumber style={{ width: 150 }} min={0} step={1000} defaultValue={v ?? undefined} placeholder="не задан"
                        formatter={(x) => (x ? Number(x).toLocaleString('ru-RU') : '')} parser={(x) => Number((x ?? '').replace(/\s/g, '').replace(',', '.'))}
                        onBlur={(e) => { const n = Number(e.target.value.replace(/\s/g, '').replace(',', '.')); if ((n || null) !== (v ?? null)) save(r, n || null); }} />
                    ) : fmt(v),
                  },
                  numCol<R>('Факт', 'fact', fmt),
                  { title: 'Выполнение', dataIndex: 'done', render: (v: number | null) => (v == null ? '—' : <Progress size="small" percent={Math.round(v * 100)} style={{ width: 140, margin: 0 }} />) },
                  { ...numCol<R>('Отставание от графика', 'gap', fmt), render: (v: number | null) => (v == null ? '—' : <span style={{ color: v < 0 ? '#cf1322' : '#389e0d' }}>{fmt(v)}</span>) },
                  numCol<R>('Прогноз', 'forecast', fmt),
                  numCol<R>('Прогноз, % плана', 'forecast_done', pct),
                ]} />
            </Card>
          </Col>
        </Row>
      </QueryState>
    </>
  );
}
