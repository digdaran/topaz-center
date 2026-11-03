const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

/**
 * Where to go after a superadmin switched the organization: the same section, but not a page of one station
 * (e.g. a shift card) — that station belongs to the previous organization and would answer 403.
 */
export function pathAfterOrgSwitch(pathname: string): string {
  if (!UUID.test(pathname)) return pathname;
  const section = pathname.split('/').filter(Boolean)[0];
  return section && !UUID.test(section) ? `/${section}` : '/';
}
