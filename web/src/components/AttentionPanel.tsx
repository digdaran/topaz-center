import { CheckCircleFilled, RightOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Badge, Card, Flex, Tag, theme, Typography } from 'antd';
import { ReactNode } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { api, isOwner, isSuper, useFiltered, useMeta } from '../api/client';
import { useMe } from '../auth/auth';
import { allowed, MONEY_ROLES } from '../nav';
import { dateTime, dec, liters } from '../lib/format';
import { SEVERITY } from '../lib/severity';

type R = Record<string, any>;

export interface StationProblem {
  station_id: string; station: string; last_seen_at: string | null; silent_seconds: number | null;
  agent_queue_dead: number | null; topaz_db_ok: boolean | null;
}

interface Item {
  key: string;
  tone: 'critical' | 'warning' | 'info';
  title: ReactNode;
  /** Up to a few lines of detail (which stations, which tanks). */
  lines?: ReactNode[];
  to: string;
  action: string;
}

/**
 * «Требует внимания» (1.14): one list of what waits for the owner's decision, each with a way to it — new events,
 * tanks to order, stations without connection, fuel deliveries without a price, fuels without a type, connection requests.
 */
export function AttentionPanel({ problems }: { problems: StationProblem[] }) {
  const me = useMe();
  const meta = useMeta();
  const { token } = theme.useToken();
  const { search } = useLocation();
  const alerts = useQuery({ queryKey: ['alerts-summary'], queryFn: () => api<{ counts: { new: number; critical: number }; latest: R[] }>('/alerts/summary'), refetchInterval: 60_000 });
  const tanks = useFiltered<{ tanks: R[] }>('/analytics/tanks');

  const items: Item[] = [];
  const counts = alerts.data?.counts;
  if (counts && counts.new > 0) {
    items.push({
      key: 'alerts', tone: counts.critical > 0 ? 'critical' : 'warning',
      title: <>Новые события: {counts.new}{counts.critical > 0 && <>, из них критичных: {counts.critical}</>}</>,
      lines: alerts.data!.latest.slice(0, 4).map((a) => (
        <span key={a.id}>
          <Tag color={SEVERITY[a.severity]?.color} style={{ marginInlineEnd: 6 }}>{SEVERITY[a.severity]?.label}</Tag>
          <b>{a.station}</b>: {a.title}
          {a.session_id != null && <> · <Link to={`/shifts/${a.station_id}/${a.session_id}`}>смена</Link></>}
        </span>
      )),
      to: `/control/alerts${search}`, action: 'Разобрать',
    });
  }
  const order = (tanks.data?.tanks ?? []).filter((t) => t.status === 'critical' || t.status === 'warning')
    .sort((a, b) => Number(a.days_to_min ?? 999) - Number(b.days_to_min ?? 999));
  if (order.length > 0) {
    items.push({
      key: 'tanks', tone: order.some((t) => t.status === 'critical') ? 'critical' : 'warning',
      title: <>Пора заказать топливо: {order.length} {order.length === 1 ? 'резервуар' : order.length < 5 ? 'резервуара' : 'резервуаров'}</>,
      lines: order.slice(0, 4).map((t) => (
        <span key={`${t.station_id}/${t.tank_id}`}>
          <b>{t.station}</b> · {t.fuel_name} (рез. {t.tank_num}): хватит на {t.days_to_min != null ? `${dec(t.days_to_min, 1)} дн.` : '—'}
          {t.order_by && <>, заказать до {t.order_by.split('-').reverse().join('.')}</>}
          {t.recommended_order > 0 && <>, около {liters(t.recommended_order)}</>}
        </span>
      )),
      to: `/stock/tanks${search}`, action: 'Резервуары',
    });
  }
  if (problems.length > 0) {
    items.push({
      key: 'offline', tone: 'critical',
      title: <>Проблемы со связью или данными: {problems.length} АЗС</>,
      lines: problems.slice(0, 4).map((s) => (
        <span key={s.station_id}>
          <b>{s.station}</b>:{' '}
          {s.silent_seconds === null ? 'ещё не выходила на связь' : s.silent_seconds > 900 ? `нет связи с ${dateTime(s.last_seen_at)}` : 'на связи'}
          {s.topaz_db_ok === false && ' · нет доступа к базе Топаза'}
          {(s.agent_queue_dead ?? 0) > 0 && ` · отклонённых пакетов: ${s.agent_queue_dead}`}
        </span>
      )),
      to: '/stations/status', action: 'Состояние АЗС',
    });
  }
  const unpriced = allowed(MONEY_ROLES, me.role) ? meta.data?.unpriced_receipts ?? 0 : 0;
  if (unpriced > 0) {
    items.push({
      key: 'unpriced', tone: 'warning', title: <>Сливы топлива без закупочной цены за 30 дней: {unpriced}</>,
      lines: ['Пока цены нет, литры считаются по средней цене резервуара — маржа может быть неточной.'],
      to: '/money/costs', action: 'Ввести цены',
    });
  }
  const unmapped = isOwner(me) ? meta.data?.unmapped_fuels ?? 0 : 0;
  if (unmapped > 0) {
    items.push({
      key: 'fuels', tone: 'info', title: <>Топливо АЗС без вида из справочника: {unmapped}</>,
      lines: ['Без вида топливо не попадёт в отчёты по видам (Аи-92, Аи-95, ДТ…).'],
      to: '/settings/fuels', action: 'Сопоставить',
    });
  }
  const requests = isSuper(me) ? meta.data?.pending_registrations ?? 0 : 0;
  if (requests > 0) {
    items.push({
      key: 'connect', tone: 'warning', title: <>АЗС ожидают подключения: {requests}</>,
      lines: ['Агенты уже установлены и копят данные; одобрите заявки, сверив код подтверждения.'],
      to: '/platform/connect', action: 'Открыть заявки',
    });
  }

  const color = { critical: token.colorError, warning: token.colorWarning, info: token.colorInfo };
  const loading = alerts.isLoading || tanks.isLoading;
  return (
    <Card size="small" style={{ marginBottom: 12 }}
      title={<>Требует внимания {items.length > 0 && <Badge count={items.length} color={items.some((i) => i.tone === 'critical') ? 'red' : 'orange'} />}</>}>
      {!loading && items.length === 0 && (
        <Flex align="center" gap={8}>
          <CheckCircleFilled style={{ color: token.colorSuccess, fontSize: 18 }} />
          <Typography.Text>Всё в порядке: новых событий нет, топлива хватает, все АЗС на связи.</Typography.Text>
        </Flex>
      )}
      {items.map((i, n) => (
        <Flex key={i.key} gap={12} align="start" justify="space-between" wrap
          style={{ padding: '10px 0', borderTop: n > 0 ? `1px solid ${token.colorBorderSecondary}` : undefined }}>
          <Flex gap={10} align="start" style={{ minWidth: 0, flex: '1 1 320px' }}>
            <span style={{ width: 4, alignSelf: 'stretch', borderRadius: 2, background: color[i.tone], flex: 'none' }} />
            <div style={{ minWidth: 0 }}>
              <Typography.Text strong>{i.title}</Typography.Text>
              {i.lines?.map((l, k) => <div key={k} style={{ fontSize: 13, color: token.colorTextSecondary, marginTop: 2 }}>{l}</div>)}
            </div>
          </Flex>
          <Link to={i.to} style={{ whiteSpace: 'nowrap' }}>{i.action} <RightOutlined style={{ fontSize: 11 }} /></Link>
        </Flex>
      ))}
    </Card>
  );
}
