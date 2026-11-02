import { describe, expect, it } from 'vitest';
import { isChunkLoadError, isOutdated, shouldReloadAfterChunkError } from './appVersion';

describe('isOutdated', () => {
  it('is false while the server runs the same release or says nothing', () => {
    expect(isOutdated('1.7.3', '1.7.3')).toBe(false);
    expect(isOutdated('1.7.3', null)).toBe(false);
    expect(isOutdated('1.7.3', '')).toBe(false);
  });
  it('is true once the server runs another release', () => {
    expect(isOutdated('1.7.3', '1.7.4')).toBe(true);
    expect(isOutdated('1.7.4', '1.7.3')).toBe(true); // a rollback also needs the matching page
  });
  it('never fires in a development build', () => {
    expect(isOutdated('dev', '1.7.4')).toBe(false);
  });
});

describe('shouldReloadAfterChunkError', () => {
  it('reloads the first time and again only after 30 s', () => {
    expect(shouldReloadAfterChunkError(null, 1_000_000)).toBe(true);
    expect(shouldReloadAfterChunkError(1_000_000, 1_010_000)).toBe(false);
    expect(shouldReloadAfterChunkError(1_000_000, 1_030_000)).toBe(true);
  });
  it('is not stuck by a broken or future stamp', () => {
    expect(shouldReloadAfterChunkError(Number('x'), 1_000_000)).toBe(true);
    expect(shouldReloadAfterChunkError(2_000_000, 1_000_000)).toBe(true);
  });
});

describe('isChunkLoadError', () => {
  it('recognizes failed section downloads in Chrome, Firefox and Safari', () => {
    expect(isChunkLoadError(new TypeError('Failed to fetch dynamically imported module: https://x/assets/A-1.js'))).toBe(true);
    expect(isChunkLoadError(new TypeError('error loading dynamically imported module'))).toBe(true);
    expect(isChunkLoadError(new TypeError('Importing a module script failed.'))).toBe(true);
  });
  it('ignores other errors', () => {
    expect(isChunkLoadError(new Error('Ошибка 500'))).toBe(false);
  });
});
