import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Card, Flex, Spin, Typography } from 'antd';
import { createContext, ReactNode, useContext } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { api, ApiError, Me } from '../api/client';
import { TotpSetup } from '../components/TotpSetup';

const MeContext = createContext<Me | null>(null);

export const useMe = () => {
  const me = useContext(MeContext);
  if (!me) throw new Error('useMe outside RequireAuth');
  return me;
};

export function useAuthState() {
  return useQuery({
    queryKey: ['me'],
    queryFn: () => api<Me>('/auth/me'),
    retry: (count, err) => !(err instanceof ApiError && err.status === 401) && count < 2,
    staleTime: 60_000,
  });
}

/** Renders children only for a signed-in user; otherwise sends to /login?next=… */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { data, isLoading, error } = useAuthState();
  const location = useLocation();
  if (isLoading) return <Spin fullscreen />;
  if (error || !data) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  if (data.totp_setup_required) return <TotpRequired />;
  return <MeContext.Provider value={data}>{children}</MeContext.Provider>;
}

/** A superadmin must turn two-factor login on before anything else (the server refuses other calls until then). */
function TotpRequired() {
  const logout = useLogout();
  return (
    <Flex align="center" justify="center" style={{ minHeight: '100vh', padding: 16 }}>
      <Card style={{ width: 560, maxWidth: '100%' }} title="Включите двухфакторную защиту входа"
        extra={<Button type="link" onClick={logout}>Выйти</Button>}>
        <Typography.Paragraph type="secondary">
          Суперадминистратор подключает АЗС и управляет всеми организациями, поэтому для него вход по одному паролю
          отключён. Это займёт пару минут.
        </Typography.Paragraph>
        <TotpSetup />
      </Card>
    </Flex>
  );
}

export function useLogout() {
  const qc = useQueryClient();
  return async () => {
    await api('/auth/logout', { method: 'POST' }).catch(() => undefined);
    qc.clear();
    window.location.assign('/login');
  };
}
