import { describe, expect, it } from 'vitest';
import { b64urlToBytes, bytesToB64url, passkeyError } from './webauthn';

describe('webauthn helpers', () => {
  it('base64url round-trips any bytes without padding', () => {
    for (const len of [0, 1, 2, 3, 31, 32, 33, 64]) {
      const bytes = Uint8Array.from({ length: len }, (_, i) => (i * 97 + 251) % 256);
      const s = bytesToB64url(bytes);
      expect(s).not.toMatch(/[+/=]/);
      expect(Array.from(b64urlToBytes(s))).toEqual(Array.from(bytes));
    }
    expect(bytesToB64url(new Uint8Array([0xfb, 0xff]))).toBe('-_8');
  });

  it('explains browser errors in plain words', () => {
    expect(passkeyError({ name: 'NotAllowedError' })).toContain('Отменено');
    expect(passkeyError({ name: 'InvalidStateError' })).toContain('уже добавлен');
    expect(passkeyError(new Error('Пароль неверен'))).toBe('Пароль неверен');
  });
});
