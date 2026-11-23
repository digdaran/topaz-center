import { describe, expect, it } from 'vitest';
import { LEAD_STAGES, REFUSE_REASONS, refusalText } from './leads';

describe('leads', () => {
  it('matches the server stages and reasons (LeadEndpoints)', () => {
    expect(LEAD_STAGES.map((s) => s.value)).toEqual(['new', 'work', 'connected', 'refused']);
    expect(REFUSE_REASONS.map((r) => r.value)).toEqual(['price', 'not_topaz', 'no_answer', 'spam', 'other']);
  });
  it('describes a refusal', () => {
    expect(refusalText('price', null)).toBe('Дорого');
    expect(refusalText('other', 'уже есть свой сервис')).toBe('Другое: уже есть свой сервис');
    expect(refusalText(null, null)).toBe('Причина не указана');
  });
});
