import { describe, expect, it } from 'vitest';
import { approvalOrganizations, clientMessage, loginFromName } from './leadClient';

describe('leadClient', () => {
  it('suggests a login from the name', () => {
    expect(loginFromName('Иван Петров')).toBe('ivan.petrov');
    expect(loginFromName('  Щукин   Юрий Ёлкин ')).toBe('schukin.yuriy');
    expect(loginFromName('John')).toBe('john');
    expect(loginFromName('Я')).toBe('');
  });
  it('writes the message for the client', () => {
    const text = clientMessage({ serviceUrl: 'https://topaz.08u.ru', login: 'ivan.petrov', password: 'Abc23456789x' });
    expect(text).toContain('Адрес: https://topaz.08u.ru');
    expect(text).toContain('Логин: ivan.petrov');
    expect(text).toContain('Временный пароль: Abc23456789x');
    expect(text).toContain('releases/latest');
  });
  it('puts clients from requests without stations first in the approval', () => {
    const list = approvalOrganizations([
      { id: '1', name: 'Альфа', is_active: true, stations: 2, lead_id: null },
      { id: '2', name: 'Яблоко', is_active: true, stations: 0, lead_id: 7 },
      { id: '3', name: 'Бета', is_active: false, stations: 0, lead_id: 8 },
      { id: '4', name: 'Вега', is_active: true, stations: 1, lead_id: 5 },
    ]);
    expect(list).toEqual([
      { value: '2', label: 'Яблоко — из заявки №7' },
      { value: '1', label: 'Альфа' },
      { value: '4', label: 'Вега' },
    ]);
  });
});
