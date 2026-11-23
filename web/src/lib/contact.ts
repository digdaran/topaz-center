/** A link for a contact typed by a site visitor: phone → tel:, @name or t.me/… → Telegram, e-mail → mailto:. */
export function contactLink(raw: string): string | null {
  const v = raw.trim();
  const tg = /^(?:@|(?:https?:\/\/)?t\.me\/)([A-Za-z0-9_]{4,32})$/.exec(v);
  if (tg) return `https://t.me/${tg[1]}`;
  if (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v)) return `mailto:${v}`;
  const digits = v.replace(/\D/g, '');
  if (/^[+\d][\d\s()-]*$/.test(v) && digits.length >= 5) {
    const phone = digits.length === 11 && digits[0] === '8' ? '7' + digits.slice(1) : digits;
    return `tel:+${phone}`;
  }
  return null;
}
