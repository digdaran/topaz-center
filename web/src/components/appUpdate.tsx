import { Alert, Button, Result, Typography } from 'antd';
import { Component, ComponentType, ErrorInfo, lazy, ReactNode, useEffect, useRef } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { clientVersion, isChunkLoadError, pageOutdated, reloadForNewVersion, useNewerVersion } from '../lib/appVersion';

/** lazy() that reloads the page once when the section's code is gone (the server was updated meanwhile). */
export function lazyPage<T extends ComponentType<object>>(load: () => Promise<{ default: T }>) {
  return lazy(() => load().catch((e: unknown) => {
    if (isChunkLoadError(e) && reloadForNewVersion()) return new Promise<{ default: T }>(() => { /* reloading */ });
    throw e;
  }));
}

/** Banner while this tab runs the code of an older release than the server. */
export function UpdateBanner() {
  const newer = useNewerVersion();
  if (!newer) return null;
  return (
    <Alert type="warning" showIcon style={{ marginBottom: 12 }}
      message={`Сайт обновлён до версии ${newer}. Обновите страницу, чтобы всё работало правильно.`}
      action={<Button size="small" type="primary" onClick={() => window.location.reload()}>Обновить</Button>} />
  );
}

/** Moving to another section of an outdated tab loads it from the server (with the new code). */
export function useReloadOnNavigateWhenOutdated() {
  const { pathname } = useLocation();
  const first = useRef(pathname);
  useEffect(() => {
    if (pathname !== first.current && pageOutdated()) window.location.reload();
    first.current = pathname;
  }, [pathname]);
}

/** «Версия X» in the bottom-left corner; links to «Что нового». */
export function VersionLabel({ linked = true }: { linked?: boolean }) {
  const newer = useNewerVersion();
  const text = `Версия ${clientVersion === 'dev' ? 'разработки' : clientVersion}`;
  return (
    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
      {linked ? <Link to="/help/changes" style={{ color: 'inherit' }} title="Что нового">{text}</Link> : text}
      {newer && (
        <>
          {' · '}
          <Typography.Link style={{ fontSize: 12, color: '#d46b08' }} onClick={() => window.location.reload()} title="Обновить страницу">
            доступна {newer}
          </Typography.Link>
        </>
      )}
    </Typography.Text>
  );
}

/** A page that fails to render shows a message with a reload button instead of a blank screen. */
export class PageErrorBoundary extends Component<{ resetKey: string; children: ReactNode }, { error: unknown }> {
  state = { error: null as unknown };

  static getDerivedStateFromError(error: unknown) {
    return { error };
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    console.error('Page error', error, info.componentStack);
    if (isChunkLoadError(error)) reloadForNewVersion();
  }

  componentDidUpdate(prev: { resetKey: string }) {
    if (prev.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null });
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <Result status="warning" title="Не удалось открыть страницу"
        subTitle="Возможно, сайт только что обновился. Обновите страницу; если ошибка повторится — сообщите администратору."
        extra={<Button type="primary" onClick={() => window.location.reload()}>Обновить страницу</Button>} />
    );
  }
}
