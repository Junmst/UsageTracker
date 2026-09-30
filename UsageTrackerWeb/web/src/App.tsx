import { useEffect, useState, type ReactNode } from 'react';
import Sidebar from './components/Sidebar';
import OrbitLoader from './components/OrbitLoader';
import DatePickerPopover from './components/DatePickerPopover';
import DistributionPage from './pages/DistributionPage';
import OverviewPage from './pages/OverviewPage';
import SessionsPage from './pages/SessionsPage';
import SettingsPage from './pages/SettingsPage';
import SubjectManagementPage from './pages/SubjectManagementPage';
import StatsPage from './pages/StatsPage';
import { api } from './lib/api';
import type { RangeSummary, SettingsSnapshot, SubjectManagementSnapshot } from './lib/api';
import { connectDataEvents, useDataChange } from './lib/events';
import { formatDateKey, formatDateLabel, getTimeDistributionDate, parseDateKey } from './lib/format';
import { useTheme } from './theme';
import type { DistributionMergeMode } from './components/TimeDistribution';

export default function App() {
  const [page, setPage] = useState('overview');
  const [mountedPages, setMountedPages] = useState(() => new Set(['overview']));
  const [date, setDate] = useState(() => formatDateKey(getTimeDistributionDate(new Date())));
  const [settings, setSettings] = useState<SettingsSnapshot | null>(null);
  const [subjectManagement, setSubjectManagement] = useState<SubjectManagementSnapshot | null>(null);
  const [overviewRange, setOverviewRange] = useState<RangeSummary | null>(null);
  const [overviewRefreshToken, setOverviewRefreshToken] = useState(0);
  const [fogAnimation, setFogAnimation] = useState(() => localStorage.getItem('shiji.fog-animation') !== 'off');
  const [orbitAnimation, setOrbitAnimation] = useState(() => localStorage.getItem('shiji.orbit-animation') !== 'off');
  const [distributionMergeMode, setDistributionMergeMode] = useState<DistributionMergeMode>(() =>
    (localStorage.getItem('shiji-distribution-merge-mode') as DistributionMergeMode | null) ?? 'process'
  );
  const theme = useTheme(settings?.themeAccentColor);

  useEffect(() => {
    // 建立全局后台变更推送连接（SSE，断线浏览器自动重连）
    connectDataEvents();
    void api
      .settings()
      .then(setSettings)
      .catch(() => undefined);
    void api.subjectManagement(true).then(setSubjectManagement).catch(() => undefined);

    let cancelled = false;
    void api.webPreferences()
      .then(async (preferences) => {
        const savedRange = preferences.overviewRange;
        if (!savedRange) return;
        // 起始日期按配置恢复；结束日期不固定，重启后始终回到当天（含 04:00 日界线）。
        const today = formatDateKey(getTimeDistributionDate(new Date()));
        const from = savedRange.from && savedRange.from <= today ? savedRange.from : today;
        const range = await api.rangeSummary(from, today);
        if (!cancelled) setOverviewRange(range);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, []);

  // 后台配置/分类变化（可能来自 Native 自动重匹配或其他看板操作）：自动更新全局分类数据
  useDataChange(() => {
    void api.settings().then(setSettings).catch(() => undefined);
    void api.subjectManagement(true).then(setSubjectManagement).catch(() => undefined);
  }, true, ['settings']);

  useEffect(() => {
    document.body.classList.toggle('fog-animation-off', !fogAnimation);
    document.body.classList.toggle('orbit-animation-off', !orbitAnimation);
    return () => {
      document.body.classList.remove('fog-animation-off', 'orbit-animation-off');
    };
  }, [fogAnimation, orbitAnimation]);

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
    <div className={`app-root${fogAnimation ? '' : ' fog-animation-off'}${orbitAnimation ? '' : ' orbit-animation-off'}`}>
      <div className="ambient-orbit-layer" aria-hidden="true"><OrbitLoader /></div>
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
        {cachedPage('sessions', (
          <SessionsPage />
        ))}
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
          <SubjectManagementPage
            settings={subjectManagement}
            onChanged={async () => {
              const next = await api.subjectManagement(true);
              setSubjectManagement(next);
              const current = await api.settings();
              setSettings(current);
            }}
          />
        ))}
        {cachedPage('settings', (
          <SettingsPage
            theme={theme}
            distributionMergeMode={distributionMergeMode}
            onDistributionMergeModeChange={(value) => {
              setDistributionMergeMode(value);
              localStorage.setItem('shiji-distribution-merge-mode', value);
            }}
            fogAnimation={fogAnimation}
            orbitAnimation={orbitAnimation}
            onFogAnimationChange={(value) => {
              setFogAnimation(value);
              localStorage.setItem('shiji.fog-animation', value ? 'on' : 'off');
            }}
            onOrbitAnimationChange={(value) => {
              setOrbitAnimation(value);
              localStorage.setItem('shiji.orbit-animation', value ? 'on' : 'off');
            }}
          />
        ))}
        </div>
        </main>
      </div>
    </div>
  );
}
