import { describe, expect, it } from 'vitest';
import { auditAction, auditDetails, auditEntity } from './audit';

describe('audit log in plain words', () => {
  it('names actions and objects', () => {
    expect(auditAction('organization_switched')).toBe('Переключился на организацию');
    expect(auditAction('station_access_block')).toBe('Заблокировал АЗС');
    expect(auditAction('something_new')).toBe('something new');
    expect(auditEntity('station:abc', { abc: 'Центр' })).toBe('АЗС «Центр»');
    expect(auditEntity('user:zzz', {})).toBe('Пользователь');
    expect(auditEntity('rule:service_volume', {})).toBe('Правило: service_volume');
  });

  it('reads details stored as an object or as a JSON string', () => {
    expect(auditDetails('{"ip": "::ffff:10.0.0.5"}', {})).toBe('IP: 10.0.0.5');
    expect(auditDetails({ Login: 'x', Role: 'manager', stations: ['a'], is_active: false }, { a: 'Центр' })).toBe('логин: x · роль: управляющий · АЗС: Центр · активен: нет');
    expect(auditDetails(null, {})).toBe('');
  });
});
