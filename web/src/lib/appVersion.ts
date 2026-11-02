import { useSyncExternalStore } from 'react';

/** The release this page was built for. */
export const clientVersion: string = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : 'dev';

let serverVersion: string | null = null;
const listeners = new Set<() => void>();

/** Called with the X-Topaz-Version header of every API answer. */
export function noteServerVersion(v: string | null | undefined) {
  if (!v || v === serverVersion) return;
  serverVersion = v;
  listeners.forEach((l) => l());
}

/** The server runs another release than this page: the page is from before an update. */
export function isOutdated(client: string, server: string | null | undefined): boolean {
  if (!server || !client || client === 'dev') return false;
  return client !== server;
}

/** True once the server reported another release than this page; stays true until a reload. */
export function pageOutdated(): boolean {
  return isOutdated(clientVersion, serverVersion);
}

/** The server's release when it differs from this page's, else null. */
export function useNewerVersion(): string | null {
  return useSyncExternalStore(
    (cb) => { listeners.add(cb); return () => { listeners.delete(cb); }; },
    () => (pageOutdated() ? serverVersion : null),
  );
}

const RELOAD_KEY = 'topaz-chunk-reload-at';
const RELOAD_GAP_MS = 30_000;

/** A reload after a failed page-part download is allowed once per 30 s (no endless reload loop). */
export function shouldReloadAfterChunkError(lastReloadAt: number | null, now: number): boolean {
  return lastReloadAt === null || !(now - lastReloadAt < RELOAD_GAP_MS) || now < lastReloadAt;
}

/**
 * A part of the page (a section's code) could not be downloaded — usually the server was updated and this tab
 * still asks for the previous release's files. Reloads once; returns false if it already did so just now.
 */
export function reloadForNewVersion(): boolean {
  let last: number | null = null;
  try { const v = sessionStorage.getItem(RELOAD_KEY); last = v ? Number(v) : null; } catch { /* no storage */ }
  const now = Date.now();
  if (!shouldReloadAfterChunkError(last, now)) return false;
  try { sessionStorage.setItem(RELOAD_KEY, String(now)); } catch { /* no storage */ }
  window.location.reload();
  return true;
}

/** Errors of a failed dynamic import (wording differs per browser). */
export function isChunkLoadError(e: unknown): boolean {
  const m = e instanceof Error ? `${e.name} ${e.message}` : String(e);
  return /dynamically imported module|Importing a module script failed|error loading dynamically|ChunkLoadError|Unable to preload CSS/i.test(m);
}
