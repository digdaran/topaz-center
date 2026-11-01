/** True if version `v` >= `target` (semantic versions: 1.10.0 > 1.9.0, 1.2.0 > 1.2.0-rc.1). */
export function versionAtLeast(v: string | null | undefined, target: string | null | undefined): boolean {
  if (!v || !target) return false;
  const parse = (s: string) => {
    const [core, ...pre] = s.replace(/^v/i, '').split('+')[0].split('-');
    return { n: core.split('.').map((x) => Number(x) || 0), pre: pre.join('-') };
  };
  const a = parse(v);
  const b = parse(target);
  for (let i = 0; i < 3; i++) if ((a.n[i] ?? 0) !== (b.n[i] ?? 0)) return (a.n[i] ?? 0) > (b.n[i] ?? 0);
  if (!a.pre) return true;
  if (!b.pre) return false;
  return a.pre.localeCompare(b.pre, undefined, { numeric: true }) >= 0;
}
