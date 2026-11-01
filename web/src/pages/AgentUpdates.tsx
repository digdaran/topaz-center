import { CloudDownloadOutlined, ReloadOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Col, Flex, InputNumber, Popconfirm, Progress, Row, Select, Space, Switch, Table, Tag, Tooltip, Typography } from 'antd';
import { api } from '../api/client';
import { QueryState } from '../components/ui';
import { dateTime } from '../lib/format';
import { versionAtLeast } from '../lib/version';

interface UpdStation {
  id: string; name: string; agent_version: string | null; agent_rid: string | null; update_group: 'pilot' | 'normal' | 'excluded'; update_now: boolean;
  last_seen_at: string | null; report_version: string | null; report_status: string | null; report_error: string | null; report_at: string | null;
}
interface UpdState {
  settings: { mode: 'auto' | 'off'; paused: boolean; pilot_delay_hours: number; release_now_version: string | null; updated_by: string | null; updated_at: string };
  latest: string | null; latest_first_seen_at: string | null;
  packages: { version: string; rid: string; size: number }[];
  stations: UpdStation[];
  progress: { total: number; on_latest: number; pilots: number };
}

const GROUP = { pilot: 'пилотная', normal: 'обычная', excluded: 'не обновлять' };
const STATUS: Record<string, { color: string; label: string }> = {
  downloading: { color: 'processing', label: 'скачивает' },
  installing: { color: 'processing', label: 'устанавливает' },
  succeeded: { color: 'success', label: 'обновлено' },
  failed: { color: 'error', label: 'ошибка' },
};

export default function AgentUpdates() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useQuery({ queryKey: ['agent-updates'], queryFn: () => api<UpdState>('/agent-updates'), refetchInterval: 30_000 });
  const call = async (fn: () => Promise<unknown>, ok?: string) => {
    try {
      await fn();
      if (ok) message.success(ok);
      qc.invalidateQueries({ queryKey: ['agent-updates'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const settings = (body: Record<string, unknown>) => call(() => api('/agent-updates', { method: 'PUT', body }));
  const station = (id: string, body: Record<string, unknown>) => call(() => api(`/stations/${id}/update`, { method: 'PATCH', body }));

  return (
    <QueryState error={error} loading={isLoading}>
      {data && (
        <>
          <Typography.Paragraph type="secondary">
            Новая версия агента приходит вместе с обновлением сервера. Пакеты подписаны при сборке, и агент ставит только пакет с верной подписью.
            Сначала обновляются пилотные АЗС, остальные — через заданное время и только если на пилотных всё прошло успешно.
            Если новая версия не запустилась, агент сам возвращается к прежней.
          </Typography.Paragraph>
          {!data.latest && (
            <Alert type="info" showIcon style={{ marginBottom: 12 }} message="На сервере нет пакетов обновления агента"
              description="Они появляются после обновления сервера на версию, собранную с подписанными пакетами." />
          )}
          <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
            <Col xs={24} lg={10}>
              <Card size="small" title="Версия" style={{ height: '100%' }}>
                {data.latest ? (
                  <>
                    <Typography.Title level={4} style={{ margin: 0 }}>{data.latest}</Typography.Title>
                    <Typography.Text type="secondary">на сервере с {dateTime(data.latest_first_seen_at)} · {[...new Set(data.packages.map((p) => p.rid))].join(', ')}</Typography.Text>
                    <Progress percent={data.progress.total ? Math.round((100 * data.progress.on_latest) / data.progress.total) : 0}
                      format={() => `${data.progress.on_latest} из ${data.progress.total}`} style={{ marginTop: 8 }} />
                  </>
                ) : '—'}
              </Card>
            </Col>
            <Col xs={24} lg={14}>
              <Card size="small" title="Раскатка" style={{ height: '100%' }}>
                <Flex vertical gap={8}>
                  <Space wrap><Switch checked={data.settings.mode === 'auto'} onChange={(c) => settings({ mode: c ? 'auto' : 'off' })} /> автоматическое обновление</Space>
                  <Space wrap><Switch checked={data.settings.paused} onChange={(c) => settings({ paused: c })} /> пауза (уже начатые обновления завершатся)</Space>
                  <Space wrap>
                    Обычные АЗС — через
                    <InputNumber min={0} max={720} value={data.settings.pilot_delay_hours} style={{ width: 80 }} onBlur={(e) => {
                      const n = Number(e.target.value);
                      if (Number.isFinite(n) && n !== data.settings.pilot_delay_hours) settings({ pilot_delay_hours: n });
                    }} />
                    <span>{`ч после появления версии${data.progress.pilots > 0 ? ', если пилотные обновились успешно' : ''}`}</span>
                  </Space>
                  {data.progress.pilots === 0 && <Typography.Text type="warning">Пилотных АЗС нет: все обновятся одновременно после задержки. Отметьте 1–2 АЗС как пилотные.</Typography.Text>}
                  <Space wrap>
                    <Popconfirm title={`Обновить все АЗС до ${data.latest} без ожидания пилотных?`} onConfirm={() => call(() => api('/agent-updates/release-now', { method: 'POST' }), 'Все АЗС получат обновление при следующем выходе на связь')} okText="Обновить" cancelText="Отмена" disabled={!data.latest}>
                      <Button icon={<CloudDownloadOutlined />} disabled={!data.latest || data.settings.release_now_version === data.latest}>Обновить все сейчас</Button>
                    </Popconfirm>
                    <Button icon={<ReloadOutlined />} onClick={() => call(() => api('/agent-updates/rescan', { method: 'POST' }), 'Пакеты перечитаны')}>Перечитать пакеты</Button>
                  </Space>
                </Flex>
              </Card>
            </Col>
          </Row>
          <Table<UpdStation> size="small" rowKey="id" dataSource={data.stations} pagination={false} scroll={{ x: 900 }}
            columns={[
              { title: 'АЗС', dataIndex: 'name' },
              {
                title: 'Версия агента', dataIndex: 'agent_version', render: (v: string | null, r) => (
                  <>{v ?? '—'} {versionAtLeast(v, data.latest) ? <Tag color="green">актуальная</Tag> : null}
                    {!r.agent_rid && v && <Tooltip title="Агент до версии 1.2 не умеет обновляться сам: один раз обновите его вручную (install.ps1)."><Tag color="orange">вручную</Tag></Tooltip>}</>
                ),
              },
              { title: 'Платформа', dataIndex: 'agent_rid', render: (v) => v ?? '—' },
              {
                title: 'Группа', dataIndex: 'update_group', render: (v: UpdStation['update_group'], r) => (
                  <Select size="small" style={{ width: 150 }} value={v} onChange={(g) => station(r.id, { update_group: g })}
                    options={Object.entries(GROUP).map(([value, label]) => ({ value, label }))} />
                ),
              },
              {
                title: 'Обновление', key: 'rep', render: (_, r) => (r.report_status ? (
                  <Tooltip title={r.report_error}>
                    <Tag color={STATUS[r.report_status]?.color}>{STATUS[r.report_status]?.label ?? r.report_status}</Tag>
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>{r.report_version} · {dateTime(r.report_at)}</Typography.Text>
                    {r.report_error && <div style={{ fontSize: 12, maxWidth: 360 }}>{r.report_error}</div>}
                  </Tooltip>
                ) : r.update_now ? <Tag color="blue">ждёт выхода на связь</Tag> : '—'),
              },
              {
                title: '', key: 'now', width: 170, render: (_, r) => (
                  <Button size="small" disabled={!data.latest || !r.agent_rid || versionAtLeast(r.agent_version, data.latest) || r.update_now}
                    onClick={() => station(r.id, { update_now: true })}>Обновить сейчас</Button>
                ),
              },
            ]} />
        </>
      )}
    </QueryState>
  );
}
