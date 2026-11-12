/** Alert importance as the owner sees it («Контроль», Telegram settings). */
export const SEVERITY: Record<string, { color: string; label: string }> = {
  critical: { color: 'red', label: 'критично' },
  warning: { color: 'orange', label: 'внимание' },
  info: { color: 'blue', label: 'информация' },
};

export const severityOptions = Object.entries(SEVERITY).map(([value, v]) => ({ value, label: v.label }));
