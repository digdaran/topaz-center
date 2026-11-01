import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Checkbox, Form, InputNumber, Radio, Select, Space, Switch, Table, Tag, Tooltip, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { int } from '../lib/format';
import { PAY, PayCategory } from '../lib/payments';

interface OrgSettingsData { id: string; name: string; margin_vat: 'gross' | 'net'; vat_rate: number }

/** The organization's VAT mode for margins (owner, or the superadmin for the organization picked in the header). */
export function OrganizationSettings() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error } = useQuery({ queryKey: ['org-settings'], queryFn: () => api<OrgSettingsData>('/organization/settings') });
  const [form] = Form.useForm<{ margin_vat: 'gross' | 'net'; vat_rate: number }>();
  const mode = Form.useWatch('margin_vat', form);
  const [saving, setSaving] = useState(false);
  useEffect(() => { if (data) form.setFieldsValue({ margin_vat: data.margin_vat, vat_rate: Number(data.vat_rate) }); }, [data, form]);

  const save = async () => {
    const v = await form.validateFields();
    setSaving(true);
    try {
      await api('/organization/settings', { method: 'PUT', body: v });
      message.success('Сохранено. Маржа во всех отчётах пересчитывается — это займёт до минуты.');
      qc.invalidateQueries();
    } catch (e) {
      message.error((e as Error).message);
    } finally {
      setSaving(false);
    }
  };

  if (error) return <Alert type="error" showIcon message={(error as Error).message} />;
  return (
    <Card size="small" title={data ? `Организация «${data.name}»` : 'Организация'} style={{ maxWidth: 720 }}>
      <Form form={form} layout="vertical" disabled={!data}>
        <Form.Item name="margin_vat" label="Маржа и наценка">
          <Radio.Group>
            <Radio value="gross" style={{ display: 'flex', marginBottom: 8 }}>
              <b>С НДС</b> — выручка как в Топазе, закупочные цены вводятся с НДС, как в накладной
            </Radio>
            <Radio value="net" style={{ display: 'flex' }}>
              <b>Без НДС</b> — из выручки продаж <u>с чеком</u> вычитается НДС по ставке, закупочные цены вводятся без НДС
            </Radio>
          </Radio.Group>
        </Form.Item>
        {mode === 'net' && (
          <Form.Item name="vat_rate" label="Ставка НДС, %" rules={[{ required: true, type: 'number', min: 0, max: 99 }]}
            extra="НДС вычитается только из продаж, у которых в Топазе есть фискальный чек (топливо и товары). Продажи без чека входят в маржу полной суммой.">
            <InputNumber min={0} max={99} step={1} style={{ width: 160 }} addonAfter="%" />
          </Form.Item>
        )}
        <Typography.Paragraph type="secondary">
          Выручка в отчётах и сверка с Топазом не меняются — режим влияет только на маржу, наценку и подписи цен закупки.
        </Typography.Paragraph>
        <Button type="primary" onClick={save} loading={saving}>Сохранить</Button>
      </Form>
    </Card>
  );
}

interface Mode {
  station_id: string; station: string; payment_mode_id: number; name: string; is_discount: boolean; manual: boolean; sales_30d: number;
  category: PayCategory; category_manual: boolean; category_guess: PayCategory; paid_90d: number; in_cash_register: number;
}

/** Does the cash drawer evidence contradict the category? (cash goes into Topaz's cash drawer, cards and contracts never do) */
function registerHint(m: Mode): { text: string; warn: boolean } | null {
  if (m.paid_90d === 0) return null;
  const share = m.in_cash_register / m.paid_90d;
  const text = share >= 0.95 ? 'в кассе' : share <= 0.05 ? 'не в кассе' : `в кассе ${Math.round(share * 100)} %`;
  const warn = (m.category === 'cash' && share <= 0.05) || (m.category !== 'cash' && m.category !== 'none' && share >= 0.95);
  return { text, warn };
}

/**
 * Payment modes of the stations: how each is paid (cash, bank card, contract, fuel card, unpaid) and whether it gives
 * a discount. Topaz has no type for a payment mode, so both are guessed from the name; the owner can correct them.
 */
export function PaymentModes() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, isLoading } = useQuery({ queryKey: ['payment-modes'], queryFn: () => api<Mode[]>('/payment-modes') });
  const [station, setStation] = useState<string | undefined>();
  const [used, setUsed] = useState(true);
  const stations = [...new Map((data ?? []).map((m) => [m.station_id, m.station])).entries()];
  const rows = (data ?? []).filter((m) => (!station || m.station_id === station) && (!used || m.sales_30d > 0 || m.paid_90d > 0 || m.manual || m.category_manual));

  const save = async (m: Mode, body: Record<string, unknown>) => {
    try {
      await api(`/payment-modes/${m.station_id}/${m.payment_mode_id}`, { method: 'PUT', body });
      qc.invalidateQueries();
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  return (
    <>
      <Typography.Paragraph type="secondary">
        <b>Способ оплаты</b> решает, куда попадают деньги в отчётах: наличные, банковские карты, продажи по договорам (ведомости)
        или топливные карты. В Топазе у вида оплаты нет такого признака, поэтому сервис определяет его по названию
        («Ведомости» — договор, «Банк. Терминал» — карта, «E1CARD» — топливная карта). Если определил неверно — выберите правильный способ.
        Подсказка «в кассе / не в кассе» показывает, проводит ли Топаз эти отпуски через кассу: наличные проходят через кассу,
        карты и договоры — нет.
      </Typography.Paragraph>
      <Typography.Paragraph type="secondary">
        <b>Дисконтный</b> вид оплаты отпускает со скидкой: цена на колонке ниже цены заказа — это законная скидка. На остальных видах
        такое снижение цены мимо скидок Топаза попадает в «Контроль». Скидка, которую записал сам Топаз, событием не считается.
      </Typography.Paragraph>
      <Space wrap style={{ marginBottom: 12 }}>
        {stations.length > 1 && (
          <Select allowClear placeholder="Все АЗС" style={{ width: 260 }} value={station} onChange={setStation}
            options={stations.map(([id, name]) => ({ value: id, label: name }))} />
        )}
        <Checkbox checked={used} onChange={(e) => setUsed(e.target.checked)}>Только используемые</Checkbox>
      </Space>
      <Table<Mode> size="small" rowKey={(m) => `${m.station_id}/${m.payment_mode_id}`} loading={isLoading} dataSource={rows} pagination={false} scroll={{ x: 900 }}
        columns={[
          { title: 'АЗС', dataIndex: 'station' },
          { title: 'Вид оплаты', dataIndex: 'name' },
          { title: 'Отпусков за 30 дней', dataIndex: 'sales_30d', align: 'right', render: (v) => int(v) },
          {
            title: 'Способ оплаты', dataIndex: 'category', width: 300, render: (v: PayCategory, m) => {
              const hint = registerHint(m);
              return (
                <Space size={4} wrap>
                  <Select size="small" style={{ width: 200 }} value={v} onChange={(c) => save(m, { category: c })}
                    options={(Object.keys(PAY) as PayCategory[]).map((k) => ({ value: k, label: PAY[k].label }))} />
                  {m.category_manual
                    ? <Button size="small" type="link" onClick={() => save(m, { category: null })} title={`По названию: ${PAY[m.category_guess].label}`}>авто</Button>
                    : <Tag>по названию</Tag>}
                  {hint && (
                    <Tooltip title={hint.warn ? 'Похоже, способ определён неверно: проверьте' : `Оплаченных отпусков за 90 дней: ${int(m.paid_90d)}`}>
                      <Tag color={hint.warn ? 'orange' : undefined}>{hint.text}</Tag>
                    </Tooltip>
                  )}
                </Space>
              );
            },
          },
          {
            title: 'Дисконтный', dataIndex: 'is_discount', width: 170, render: (v: boolean, m) => (
              <>
                <Switch size="small" checked={v} onChange={(c) => save(m, { is_discount: c })} />{' '}
                {m.manual
                  ? <Button size="small" type="link" onClick={() => save(m, { is_discount: null })} title="Вернуть отметку по названию">авто</Button>
                  : <Tag style={{ marginInlineStart: 4 }}>по названию</Tag>}
              </>
            ),
          },
        ]} />
    </>
  );
}
