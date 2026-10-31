import { ConfigProvider, theme as antTheme } from 'antd';
import ruRU from 'antd/locale/ru_RU';
import dayjs from 'dayjs';
import 'dayjs/locale/ru';
import { createContext, ReactNode, useContext, useEffect, useState } from 'react';

dayjs.locale('ru');

type Mode = 'light' | 'dark' | 'system';
const ThemeContext = createContext<{ mode: Mode; dark: boolean; setMode: (m: Mode) => void }>({ mode: 'system', dark: false, setMode: () => {} });
export const useThemeMode = () => useContext(ThemeContext);

const read = (): Mode => {
  try {
    const v = localStorage.getItem('topaz.theme');
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch {
    return 'system';
  }
};

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [mode, setModeState] = useState<Mode>(read);
  const media = window.matchMedia('(prefers-color-scheme: dark)');
  const [systemDark, setSystemDark] = useState(media.matches);
  useEffect(() => {
    const on = (e: MediaQueryListEvent) => setSystemDark(e.matches);
    media.addEventListener('change', on);
    return () => media.removeEventListener('change', on);
  }, [media]);
  const dark = mode === 'dark' || (mode === 'system' && systemDark);
  useEffect(() => {
    document.body.style.background = dark ? '#141414' : '#f5f5f5';
  }, [dark]);
  const setMode = (m: Mode) => {
    setModeState(m);
    try {
      localStorage.setItem('topaz.theme', m);
    } catch {
      /* private mode */
    }
  };
  return (
    <ThemeContext.Provider value={{ mode, dark, setMode }}>
      <ConfigProvider
        locale={ruRU}
        theme={{ algorithm: dark ? antTheme.darkAlgorithm : antTheme.defaultAlgorithm, token: { colorPrimary: '#1668dc', borderRadius: 8 } }}
      >
        {children}
      </ConfigProvider>
    </ThemeContext.Provider>
  );
}
