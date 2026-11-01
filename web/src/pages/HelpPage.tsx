import { Anchor, Card, Col, Grid, Row, Tabs, Tag, theme, Typography } from 'antd';
import { useEffect, useMemo } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { PageTitle } from '../components/ui';
import { changelog, guide, headings, render } from '../help/help';
import { latestVersion, markSeen, releases } from '../help/version';

/** Rendered help text: our own Markdown from web/src/help (see CLAUDE.md: keep it in step with the product). */
function Doc({ md, toc }: { md: string; toc: boolean }) {
  const { token } = theme.useToken();
  const html = useMemo(() => render(md), [md]);
  const screens = Grid.useBreakpoint();
  const items = useMemo(() => headings(md).filter((h) => h.depth === 2).map((h) => ({ key: h.id, href: `#${h.id}`, title: h.text })), [md]);
  const body = (
    <Card size="small">
      <div className="help-doc" dangerouslySetInnerHTML={{ __html: html }} />
      <style>{`
        .help-doc { font-size: 15px; line-height: 1.65; max-width: 860px; }
        .help-doc h1 { display: none; }
        .help-doc h2 { font-size: 21px; margin: 28px 0 10px; padding-top: 8px; border-top: 1px solid ${token.colorBorderSecondary}; scroll-margin-top: 16px; }
        .help-doc h2:first-of-type { border-top: 0; margin-top: 4px; }
        .help-doc h3 { font-size: 17px; margin: 22px 0 8px; scroll-margin-top: 16px; }
        .help-doc p, .help-doc ul, .help-doc ol { margin: 0 0 10px; }
        .help-doc li { margin: 3px 0; }
        .help-doc code { background: ${token.colorFillTertiary}; padding: 1px 5px; border-radius: 4px; font-size: 13px; }
        .help-doc a { color: ${token.colorLink}; }
        .help-doc strong { font-weight: 600; }
      `}</style>
    </Card>
  );
  if (!toc || !screens.lg) return body;
  return (
    <Row gutter={16} wrap={false}>
      <Col flex="auto" style={{ minWidth: 0 }}>{body}</Col>
      <Col flex="240px">
        <Card size="small" title="Содержание" style={{ position: 'sticky', top: 12 }}>
          <Anchor affix={false} items={items} targetOffset={16} />
        </Card>
      </Col>
    </Row>
  );
}

export default function HelpPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const tab = location.pathname.startsWith('/help/changes') ? 'changes' : 'guide';
  useEffect(() => { if (tab === 'changes') markSeen(); }, [tab]);
  const last = releases()[0];

  return (
    <>
      <PageTitle title="Помощь" extra={last && <Typography.Text type="secondary">Версия {latestVersion} от {last.date}</Typography.Text>} />
      <Tabs activeKey={tab} onChange={(k) => navigate(k === 'changes' ? '/help/changes' : '/help')} items={[
        { key: 'guide', label: 'Инструкция', children: <Doc md={guide} toc /> },
        { key: 'changes', label: <>Что нового <Tag color="blue" style={{ marginInlineStart: 4 }}>{latestVersion}</Tag></>, children: <Doc md={changelog} toc={false} /> },
      ]} />
    </>
  );
}
