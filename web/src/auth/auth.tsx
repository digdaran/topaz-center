import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Spin } from 'antd';
import { createContext, ReactNode, useContext } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { api, ApiError, Me } from '../api/client';

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
  return <MeContext.Provider value={data}>{children}</MeContext.Provider>;
}

export function useLogout() {
  const qc = useQueryClient();
  return async () => {
    await api('/auth/logout', { method: 'POST' }).catch(() => undefined);
    qc.clear();
    window.location.assign('/login');
  };
}
