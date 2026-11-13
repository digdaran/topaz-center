import { ArrowDownOutlined, ArrowUpOutlined, InfoCircleOutlined } from '@ant-design/icons';
import { Alert, Card, Empty, Flex, Spin, theme, Tooltip, Typography } from 'antd';
import type { EChartsOption } from 'echarts';
import ReactEChartsCore from 'echarts-for-react/lib/core';
import { echarts } from './echarts';
import { ReactNode } from 'react';
import { change, Num, pct, toNum } from '../lib/format';
import { ApiError } from '../api/client';
import { useThemeMode } from './theme';

export function PageTitle({ title, extra, description }: { title: string; extra?: ReactNode; description?: ReactNode }) {
  return (
    <Flex justify="space-between" align="center" wrap gap={8} style={{ marginBottom: 12 }}>
      <div>
        <Typography.Title level={3} style={{ margin: 0 }}>
          {title}
        </Typography.Title>
        {description && <Typography.Text type="secondary">{description}</Typography.Text>}
      </div>
      {extra}
    </Flex>
  );
}

/** Controls of a tab (metric switch, month), right-aligned above its content. */
export function Toolbar({ children }: { children: ReactNode }) {
  return <Flex justify="flex-end" wrap gap={8} style={{ marginBottom: 12 }}>{children}</Flex>;
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

export interface Figure {
  key: string;
  label: ReactNode;
  value: ReactNode;
  /** A coloured dot before the label (payment method). */
  dot?: string;
  hint?: ReactNode;
  strong?: boolean;
  /** Indented: a part of the row above ("в том числе"). */
  sub?: boolean;
}

/** Label — value lines (a figure list in a card); an optional total under a rule. */
export function FigureList({ rows, total }: { rows: Figure[]; total?: Figure }) {
  const { token } = theme.useToken();
  const line = (r: Figure, isTotal = false) => (
    <Flex key={r.key} justify="space-between" align="baseline" gap={12}
      style={{
        padding: isTotal ? '8px 0 0' : '3px 0', paddingLeft: r.sub ? 16 : 0,
        borderTop: isTotal ? `1px solid ${token.colorBorderSecondary}` : undefined, marginTop: isTotal ? 6 : 0,
      }}>
      <Typography.Text type={r.strong || isTotal ? undefined : 'secondary'} strong={isTotal} style={{ minWidth: 0 }}>
        {r.dot && <span style={{ display: 'inline-block', width: 8, height: 8, borderRadius: 4, background: r.dot, marginRight: 8 }} />}
        {r.label}
        {r.hint && <>{' '}<Tooltip title={r.hint}><InfoCircleOutlined style={{ color: token.colorTextTertiary }} /></Tooltip></>}
      </Typography.Text>
      <span style={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap', fontWeight: r.strong || isTotal ? 600 : undefined, fontSize: isTotal ? 16 : undefined }}>
        {r.value}
      </span>
    </Flex>
  );
  return <div>{rows.map((r) => line(r))}{total && line(total, true)}</div>;
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
  if (error instanceof ApiError && error.status === 403)
    return <Alert type="warning" showIcon message="Нет доступа к этим данным"
      description="Скорее всего, это АЗС другой организации. Выберите нужную организацию вверху страницы или откройте раздел заново из меню." />;
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
