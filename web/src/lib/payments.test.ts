import { describe, expect, it } from 'vitest';
import { tillNote } from './payments';

describe('tillNote', () => {
  it('explains a card mode paid in cash through the till', () => {
    expect(tillNote('cash', 'card', 'in')).toMatch(/карта не прошла/);
  });
  it('explains a cash mode paid past the till', () => {
    expect(tillNote('card', 'cash', 'out')).toMatch(/не прошли через кассу/);
  });
  it('stays silent when the mode decided', () => {
    expect(tillNote('card', 'card', 'out')).toBeNull();
    expect(tillNote('cash', 'cash', 'in')).toBeNull();
    expect(tillNote('card', 'cash', 'unknown')).toBeNull(); // "Б" at a cash mode, not the till
    expect(tillNote('cash', null, 'in')).toBeNull();
  });
});
