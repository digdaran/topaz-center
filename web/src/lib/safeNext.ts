/** Where to go after login: only a path of this site. "//host" and "/\host" lead to other sites in a browser. */
export function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') && !next.includes('\\') ? next : '/';
}
