/** Helpers for «Создать клиента» (Платформа → Заявки с сайта) and connecting the client's first station. */

export const INSTALLER_URL = 'https://github.com/digdaran/topaz-center/releases/latest';

const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y', к: 'k', л: 'l', м: 'm', н: 'n', о: 'o',
  п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'h', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'sch', ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya',
};

/** A login suggested from the person's name: "Иван Петров" → "ivan.petrov". The superadmin can change it. */
export function loginFromName(name: string): string {
  const words = name.toLowerCase().split(/\s+/).filter(Boolean).slice(0, 2)
    .map((w) => [...w].map((ch) => TRANSLIT[ch] ?? (/[a-z0-9]/.test(ch) ? ch : '')).join(''))
    .filter(Boolean);
  const login = words.join('.');
  return login.length >= 3 ? login.slice(0, 50) : '';
}

/** The text the superadmin sends to the new client: where to sign in and how to install the agent. */
export function clientMessage(p: { serviceUrl: string; login: string; password: string }): string {
  return [
    'Здравствуйте! Доступ к АЗС Хаб готов.',
    '',
    `Адрес: ${p.serviceUrl}`,
    `Логин: ${p.login}`,
    `Временный пароль: ${p.password}`,
    'При первом входе сервис попросит придумать свой пароль.',
    '',
    'Чтобы подключить АЗС, на компьютере АЗС с «Топазом»:',
    `1. Скачайте установщик агента: ${INSTALLER_URL} (файл TopazAgent-Setup-…-win-x64.exe; для 32-битной Windows — win-x86).`,
    '2. Запустите его, введите название АЗС и нажмите «Установить».',
    '3. В конце появится код подтверждения — продиктуйте или пришлите его нам. Через минуту после подтверждения данные появятся в сервисе.',
  ].join('\n');
}

export interface OrgOption { id: string; name: string; is_active: boolean; stations?: number; lead_id?: number | null }

/** Active organizations for the station approval: clients from site requests still without stations come first. */
export function approvalOrganizations(orgs: OrgOption[]): { value: string; label: string }[] {
  const waiting = (o: OrgOption) => o.lead_id != null && (o.stations ?? 0) === 0;
  return orgs.filter((o) => o.is_active)
    .sort((a, b) => Number(waiting(b)) - Number(waiting(a)) || a.name.localeCompare(b.name, 'ru'))
    .map((o) => ({ value: o.id, label: waiting(o) ? `${o.name} — из заявки №${o.lead_id}` : o.name }));
}
