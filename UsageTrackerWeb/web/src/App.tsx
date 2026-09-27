import { useEffect, useState, type ReactNode } from 'react';
import Sidebar from './components/Sidebar';
import DatePickerPopover from './components/DatePickerPopover';
import DistributionPage from './pages/DistributionPage';
import OverviewPage from './pages/OverviewPage';
import SessionsPage from './pages/SessionsPage';
import SettingsPage, { SubjectStructure } from './pages/SettingsPage';
import StatsPage from './pages/StatsPage';
import { api } from './lib/api';
import type { RangeSummary, SettingsSnapshot } from './lib/api';
import { formatDateKey, formatDateLabel, getTimeDistributionDate, parseDateKey } from './lib/format';
import { useTheme } from './theme';
import type { DistributionMergeMode } from './components/TimeDistribution';

export default function App() {
  const [page, setPage] = useState('overview');
  const [mountedPages, setMountedPages] = useState(() => new Set(['overview']));
  const [date, setDate] = useState(() => formatDateKey(getTimeDistributionDate(new Date())));
  const [settings, setSettings] = useState<SettingsSnapshot | null>(null);
  const [overviewRange, setOverviewRange] = useState<RangeSummary | null>(null);
  const [overviewRefreshToken, setOverviewRefreshToken] = useState(0);
  const [distributionMergeMode, setDistributionMergeMode] = useState<DistributionMergeMode>(() =>
    (localStorage.getItem('shiji-distribution-merge-mode') as DistributionMergeMode | null) ?? 'process'
  );
  const theme = useTheme(settings?.themeAccentColor);

  useEffect(() => {
    void api
      .settings()
      .then(setSettings)
      .catch(() => undefined);

    let cancelled = false;
    void api.webPreferences()
      .then(async (preferences) => {
        const savedRange = preferences.overviewRange;
        if (!savedRange) return;
        const range = await api.rangeSummary(savedRange.from, savedRange.to);
        if (!cancelled) setOverviewRange(range);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const closeTransientPopovers = () => {
      window.dispatchEvent(new CustomEvent('shiji:close-popovers'));
    };
    window.addEventListener('scroll', closeTransientPopovers, true);
    return () => window.removeEventListener('scroll', closeTransientPopovers, true);
  }, []);

  useEffect(() => {
    const heartbeat = () => {
      void fetch('/api/browser-presence', { method: 'POST', keepalive: true }).catch(() => undefined);
    };

    heartbeat();
    const timer = window.setInterval(heartbeat, 700);
    const close = () => {
      const body = new Blob([], { type: 'text/plain' });
      navigator.sendBeacon('/api/browser-presence/close', body);
    };
    window.addEventListener('pagehide', close);

    return () => {
      window.clearInterval(timer);
      window.removeEventListener('pagehide', close);
    };
  }, []);

  const showOverviewDatePicker = page === 'overview';
  const showStatsDatePicker = page === 'process' || page === 'subject';
  const selectPage = (nextPage: string) => {
    setPage(nextPage);
    setMountedPages((current) => {
      if (current.has(nextPage)) return current;
      const next = new Set(current);
      next.add(nextPage);
      return next;
    });
  };
  const cachedPage = (key: string, content: ReactNode) => {
    if (!mountedPages.has(key)) return null;
    return (
      <div
        className={`page-view${page === key ? ' page-view-active' : ''}`}
        style={{ display: page === key ? undefined : 'none' }}
      >
        {content}
      </div>
    );
  };

  return (
    <div className="shell">
      <Sidebar active={page} onSelect={selectPage} />
      <main className={`content${page === 'distribution' ? ' content-fixed' : ''}`}>
        <div className="page-transition-layer">
        {showOverviewDatePicker && (
          <div className="content-toolbar overview-global-toolbar">
            <DatePickerPopover value={date} onChange={setDate} />
            <span className="toolbar-hint">{formatDateLabel(parseDateKey(date))}</span>
            <button
              className="toolbar-button"
              onClick={() => {
                setDate(formatDateKey(getTimeDistributionDate(new Date())));
                setOverviewRefreshToken((value) => value + 1);
              }}
            >
              今天
            </button>
          </div>
        )}
        {showStatsDatePicker && (
          <div className="content-toolbar stats-global-toolbar">
            <DatePickerPopover value={date} onChange={setDate} />
            <span className="toolbar-hint">{formatDateLabel(parseDateKey(date))}</span>
            <button
              className="toolbar-button"
              onClick={() => setDate(formatDateKey(getTimeDistributionDate(new Date())))}
            >
              今天
            </button>
          </div>
        )}

        {cachedPage('overview', (
          <OverviewPage
            date={date}
            theme={theme}
            overviewRange={overviewRange}
            onOverviewRangeChange={setOverviewRange}
            subjects={settings?.subjectDefinitions ?? []}
            refreshToken={overviewRefreshToken}
          />
        ))}
        {cachedPage('sessions', <SessionsPage />)}
        {cachedPage('distribution', (
          <DistributionPage
            active={page === 'distribution'}
            theme={theme}
            subjects={settings?.subjectDefinitions ?? []}
            mergeMode={distributionMergeMode}
          />
        ))}
        {cachedPage('process', <StatsPage kind="process" date={date} theme={theme} />)}
        {cachedPage('subject', <StatsPage kind="subject" date={date} theme={theme} />)}
        {cachedPage('subjectManagement', (
          <div className="page">
            <div className="page-header">
              <div>
                <h2>分类管理</h2>
                <div className="page-subtitle">只读展示 · 修改分类请在桌面版进行</div>
              </div>
            </div>
            <SubjectStructure definitions={settings?.subjectDefinitions ?? []} />
          </div>
        ))}
        {cachedPage('settings', (
          <SettingsPage
            theme={theme}
            distributionMergeMode={distributionMergeMode}
            onDistributionMergeModeChange={(value) => {
              setDistributionMergeMode(value);
              localStorage.setItem('shiji-distribution-merge-mode', value);
            }}
          />
        ))}
        </div>
      </main>
    </div>
  );
}
