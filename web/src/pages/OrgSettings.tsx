import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Form, InputNumber, Radio, Select, Switch, Table, Tag, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { int } from '../lib/format';

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

interface Mode { station_id: string; station: string; payment_mode_id: number; name: string; is_discount: boolean; manual: boolean; sales_30d: number }

/** Discount payment modes: a lower pump price there is a legitimate discount, elsewhere it is an alert. */
export function PaymentModes() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, isLoading } = useQuery({ queryKey: ['payment-modes'], queryFn: () => api<Mode[]>('/payment-modes') });
  const [station, setStation] = useState<string | undefined>();
  const stations = [...new Map((data ?? []).map((m) => [m.station_id, m.station])).entries()];
  const rows = (data ?? []).filter((m) => !station || m.station_id === station);

  const set = async (m: Mode, value: boolean | null) => {
    try {
      await api(`/payment-modes/${m.station_id}/${m.payment_mode_id}`, { method: 'PUT', body: { is_discount: value } });
      qc.invalidateQueries({ queryKey: ['payment-modes'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  return (
    <>
      <Typography.Paragraph type="secondary">
        На дисконтном виде оплаты колонка отпускает со скидкой, и цена на колонке ниже цены заказа — это законная скидка.
        На остальных видах оплаты такое снижение цены мимо скидок Топаза попадает в «Контроль». Скидка, которую записал сам Топаз
        (дисконтная программа по карте), событием не считается никогда. Отметка ставится автоматически по названию
        («Дисконтная…», «Скидка…»), её можно исправить.
      </Typography.Paragraph>
      {stations.length > 1 && (
        <Select allowClear placeholder="Все АЗС" style={{ width: 260, marginBottom: 12 }} value={station} onChange={setStation}
          options={stations.map(([id, name]) => ({ value: id, label: name }))} />
      )}
      <Table<Mode> size="small" rowKey={(m) => `${m.station_id}/${m.payment_mode_id}`} loading={isLoading} dataSource={rows} pagination={false} scroll={{ x: 640 }}
        columns={[
          { title: 'АЗС', dataIndex: 'station' },
          { title: 'Вид оплаты', dataIndex: 'name' },
          { title: 'Отпусков за 30 дней', dataIndex: 'sales_30d', align: 'right', render: (v) => int(v) },
          {
            title: 'Дисконтный', dataIndex: 'is_discount', width: 200, render: (v: boolean, m) => (
              <>
                <Switch size="small" checked={v} onChange={(c) => set(m, c)} />{' '}
                {m.manual
                  ? <Button size="small" type="link" onClick={() => set(m, null)} title="Вернуть отметку по названию">авто</Button>
                  : <Tag style={{ marginInlineStart: 4 }}>по названию</Tag>}
              </>
            ),
          },
        ]} />
    </>
  );
}
