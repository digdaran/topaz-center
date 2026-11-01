import { useSyncExternalStore } from 'react';
import changelog from './changelog.md?raw';

export interface Release { version: string; date: string }

/** Releases from the changelog headings "## 1.6.1 — 01.11.2026" (newest first). */
export function releases(md: string = changelog): Release[] {
  return [...md.matchAll(/^## (\d+\.\d+\.\d+)(?: — \d+\.\d+\.\d+)? — (\d{2}\.\d{2}\.\d{4})\s*$/gm)].map((m) => ({ version: m[1], date: m[2] }));
}

export const latestVersion = releases()[0]?.version ?? '';

const SEEN_KEY = 'topaz-help-seen-version';
const EVENT = 'topaz-help-seen';

export function seenVersion(): string | null {
  try { return localStorage.getItem(SEEN_KEY); } catch { return null; }
}

export function markSeen(version = latestVersion) {
  try { localStorage.setItem(SEEN_KEY, version); } catch { /* private mode: the badge just stays */ }
  window.dispatchEvent(new Event(EVENT));
}

/** True while the latest release has not been opened in «Что нового» on this browser (menu badge). */
export function useHelpUnseen(): boolean {
  return useSyncExternalStore(
    (cb) => { window.addEventListener(EVENT, cb); window.addEventListener('storage', cb); return () => { window.removeEventListener(EVENT, cb); window.removeEventListener('storage', cb); }; },
    () => !!latestVersion && seenVersion() !== latestVersion,
  );
}
