import { DatePicker, Flex, Segmented, Select } from 'antd';
import dayjs from 'dayjs';
import { useFilter, useMeta } from '../api/client';
import { Compare, Granularity, presets } from '../lib/filter';

const { RangePicker } = DatePicker;

interface Props {
  granularity?: boolean;
  compare?: boolean;
  fuel?: boolean;
}

/** Stations, period, granularity, comparison and fuel — the common filter of analytics pages. */
export function FilterBar({ granularity = true, compare = true, fuel = false }: Props) {
  const [filter, setFilter] = useFilter();
  const meta = useMeta();
  const stations = meta.data?.stations ?? [];

  return (
    <Flex wrap gap={8} align="center" style={{ marginBottom: 16 }}>
      {stations.length > 1 && (
        <Select
          mode="multiple"
          allowClear
          placeholder="Все АЗС"
          style={{ minWidth: 220, maxWidth: 520 }}
          value={filter.stations}
          onChange={(v) => setFilter({ stations: v })}
          options={stations.map((s) => ({ value: s.id, label: s.name }))}
          optionFilterProp="label"
          maxTagCount="responsive"
        />
      )}
      <RangePicker
        allowClear={false}
        format="DD.MM.YYYY"
        value={[dayjs(filter.from), dayjs(filter.to)]}
        presets={presets().map((p) => ({ label: p.label, value: [dayjs(p.from), dayjs(p.to)] }))}
        onChange={(v) => v && v[0] && v[1] && setFilter({ from: v[0].format('YYYY-MM-DD'), to: v[1].format('YYYY-MM-DD'), g: undefined })}
      />
      {granularity && (
        <Segmented<Granularity | 'auto'>
          value={filter.g ?? 'auto'}
          onChange={(v) => setFilter({ g: v === 'auto' ? undefined : v })}
          options={[
            { value: 'auto', label: 'Авто' },
            { value: 'hour', label: 'Час' },
            { value: 'day', label: 'День' },
            { value: 'week', label: 'Неделя' },
            { value: 'month', label: 'Месяц' },
          ]}
        />
      )}
      {compare && (
        <Select<Compare>
          style={{ width: 210 }}
          value={filter.compare}
          onChange={(v) => setFilter({ compare: v })}
          options={[
            { value: 'prev', label: 'К предыдущему периоду' },
            { value: 'yoy', label: 'К прошлому году' },
            { value: 'none', label: 'Без сравнения' },
          ]}
        />
      )}
      {fuel && (meta.data?.fuels.length ?? 0) > 1 && (
        <Select
          allowClear
          placeholder="Всё топливо"
          style={{ width: 160 }}
          value={filter.fuel}
          onChange={(v) => setFilter({ fuel: v })}
          options={meta.data!.fuels.map((f) => ({ value: f, label: f }))}
        />
      )}
    </Flex>
  );
}
