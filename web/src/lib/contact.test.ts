import { describe, expect, it } from 'vitest';
import { contactLink } from './contact';

describe('contactLink', () => {
  it('turns what a visitor typed into a link', () => {
    expect(contactLink('+7 900 123-45-67')).toBe('tel:+79001234567');
    expect(contactLink('8 (900) 123-45-67')).toBe('tel:+79001234567');
    expect(contactLink('@ars08')).toBe('https://t.me/ars08');
    expect(contactLink('t.me/ars08')).toBe('https://t.me/ars08');
    expect(contactLink('ivan@example.ru')).toBe('mailto:ivan@example.ru');
  });
  it('leaves anything else as text', () => {
    expect(contactLink('звоните вечером')).toBeNull();
    expect(contactLink('javascript:alert(1)')).toBeNull();
    expect(contactLink('123')).toBeNull();
  });
});
