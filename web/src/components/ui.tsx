import { ArrowDownOutlined, ArrowUpOutlined, InfoCircleOutlined } from '@ant-design/icons';
import { Alert, Card, Empty, Flex, Spin, theme, Tooltip, Typography } from 'antd';
import type { EChartsOption } from 'echarts';
import ReactEChartsCore from 'echarts-for-react/lib/core';
import { echarts } from './echarts';
import { ReactNode } from 'react';
import { change, Num, pct, toNum } from '../lib/format';
import { useThemeMode } from './theme';

export function PageTitle({ title, extra }: { title: string; extra?: ReactNode }) {
  return (
    <Flex justify="space-between" align="center" wrap gap={8} style={{ marginBottom: 12 }}>
      <Typography.Title level={3} style={{ margin: 0 }}>
        {title}
      </Typography.Title>
      {extra}
    </Flex>
  );
}

interface KpiProps {
  title: string;
  value: Num;
  previous?: Num;
  format: (v: Num) => string;
  hint?: string;
  /** For metrics where growth is bad (aborted fuellings, discounts). */
  inverse?: boolean;
}

/** A metric with its change versus the comparison period. */
export function KpiCard({ title, value, previous, format, hint, inverse }: KpiProps) {
  const { token } = theme.useToken();
  const delta = previous === undefined ? null : change(value, previous);
  const good = delta === null ? null : inverse ? delta < 0 : delta > 0;
  return (
    <Card size="small" style={{ height: '100%' }}>
      <Typography.Text type="secondary" style={{ fontSize: 13 }}>
        {title}{' '}
        {hint && (
          <Tooltip title={hint}>
            <InfoCircleOutlined />
          </Tooltip>
        )}
      </Typography.Text>
      <div style={{ fontSize: 22, fontWeight: 600, margin: '4px 0', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
        {format(value)}
      </div>
      {previous !== undefined && (
        <div style={{ fontSize: 12, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }} title={`было ${format(previous)}`}>
          <span style={{ color: delta === null ? token.colorTextSecondary : good ? token.colorSuccess : token.colorError }}>
            {delta === null ? 'нет данных для сравнения' : <>{delta >= 0 ? <ArrowUpOutlined /> : <ArrowDownOutlined />} {pct(Math.abs(delta))}</>}
          </span>
          {delta !== null && <span style={{ color: token.colorTextSecondary }}> · было {format(previous)}</span>}
        </div>
      )}
    </Card>
  );
}

/** ECharts in a card, themed for light/dark. */
export function ChartCard({ title, option, height = 320, loading, extra, empty }: {
  title: ReactNode;
  option: EChartsOption;
  height?: number;
  loading?: boolean;
  extra?: ReactNode;
  empty?: boolean;
}) {
  const { dark } = useThemeMode();
  return (
    <Card size="small" title={title} extra={extra} style={{ height: '100%' }}>
      {empty ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Нет данных за период" style={{ height: height - 40 }} />
      ) : (
        <Spin spinning={!!loading}>
          <ReactEChartsCore
            echarts={echarts}
            option={{ backgroundColor: 'transparent', ...option }}
            theme={dark ? 'dark' : undefined}
            style={{ height }}
            notMerge
            lazyUpdate
          />
        </Spin>
      )}
    </Card>
  );
}

export function QueryState({ error, loading, children }: { error: unknown; loading: boolean; children: ReactNode }) {
  if (error) return <Alert type="error" showIcon message="Не удалось загрузить данные" description={(error as Error).message} />;
  if (loading) return <Spin style={{ display: 'block', margin: '64px auto' }} size="large" />;
  return <>{children}</>;
}

/** Numeric table column: right-aligned, tabular digits, sortable. */
export function numCol<T>(title: string, key: keyof T & string, format: (v: Num) => string, extra: object = {}) {
  return {
    title,
    dataIndex: key,
    key,
    align: 'right' as const,
    sorter: (a: T, b: T) => (toNum(a[key] as Num) ?? 0) - (toNum(b[key] as Num) ?? 0),
    render: (v: Num) => <span style={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>{format(v)}</span>,
    ...extra,
  };
}

export const CHART_COLORS = ['#1668dc', '#13a8a8', '#d48806', '#cf1322', '#7cb305', '#9254de', '#08979c', '#d4380d', '#595959'];
