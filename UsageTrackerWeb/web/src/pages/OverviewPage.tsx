import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { RankingChart, TrendChart } from '../components/Charts';
import LoadingTransition from '../components/LoadingTransition';
import SubjectFilterMenu from '../components/SubjectFilterMenu';
import { api } from '../lib/api';
import type { BucketStat, DailyPoint, Overview, RangeSummary, SubjectDefinition } from '../lib/api';
import { formatDateKey, formatDurationShort, formatHoursMinutes, parseDateKey } from '../lib/format';
import type { ThemeController } from '../theme';

interface Props {
  date: string;
  theme: ThemeController;
  overviewRange: RangeSummary | null;
  onOverviewRangeChange: (range: RangeSummary | null) => void;
  subjects: SubjectDefinition[];
  refreshToken: number;
}

export default function OverviewPage({ date, theme, overviewRange, onOverviewRangeChange, subjects, refreshToken }: Props) {
  const [overview, setOverview] = useState<Overview | null>(null);
  const [subjectFilter, setSubjectFilter] = useState<string | null>(null);
  const [daily, setDaily] = useState<DailyPoint[]>([]);
  const [top, setTop] = useState<BucketStat[]>([]);
  const [calendarOpen, setCalendarOpen] = useState(false);
  const [calendarMounted, setCalendarMounted] = useState(false);
  const [rangeOpen, setRangeOpen] = useState(false);
  const [rangeMounted, setRangeMounted] = useState(false);
  const [rangeFrom, setRangeFrom] = useState('');
  const [rangeTo, setRangeTo] = useState('');
  const [rangeResult, setRangeResult] = useState<RangeSummary | null>(overviewRange);
  const [rangeLoading, setRangeLoading] = useState(false);
  const [rangeError, setRangeError] = useState('');
  const [clockNow, setClockNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setClockNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    setRangeResult(overviewRange);
  }, [overviewRange]);

  useEffect(() => {
    if (!overviewRange) return;
    let cancelled = false;
    void api.rangeSummary(overviewRange.from, overviewRange.to, subjectFilter)
      .then((range) => {
        if (cancelled) return;
        setRangeResult(range);
        onOverviewRangeChange(range);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [subjectFilter]);

  useEffect(() => {
    let cancelled = false;
    const loadActive = () => {
      void api.active(subjectFilter, true)
        .then((active) => {
          if (cancelled) return;
          setOverview((current) => current ? { ...current, active } : current);
        })
        .catch(() => undefined);
    };
    loadActive();
    const activeTimer = window.setInterval(loadActive, 2000);
    return () => {
      cancelled = true;
      window.clearInterval(activeTimer);
    };
  }, [subjectFilter]);

  useEffect(() => {
    let cancelled = false;
    const load = (forceRefresh = false) => {
      void Promise.all([
        api.overview(date, subjectFilter, forceRefresh),
        api.daily(30, subjectFilter, forceRefresh),
        api.ranking(date, 'process', 10, subjectFilter, forceRefresh),
      ])
        .then(([o, d, r]) => {
          if (cancelled) return;
          setOverview(o);
          setDaily(d);
          setTop(r);
        })
        .catch(() => undefined);
    };
    load(refreshToken > 0);
    const timer = window.setInterval(() => load(true), 10000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [date, subjectFilter, refreshToken]);

  const fallbackOverview: Overview = {
    date,
    todaySeconds: 0,
    weekSeconds: 0,
    monthSeconds: 0,
    totalSeconds: 0,
    trackedDays: 0,
    sessionCount: 0,
    databaseSizeMb: 0,
    active: null,
  };
  const displayedOverview = overview ?? fallbackOverview;
  const isRangeMode = overviewRange !== null;
  const displayDaily = overviewRange?.daily ?? daily;
  const displayTop = overviewRange?.ranking ?? top;
  const topProcess = useMemo(() => displayTop[0], [displayTop]);
  const todayKey = displayedOverview.date;

  const openCalendarModal = () => {
    setCalendarMounted(true);
    setCalendarOpen(true);
  };

  const closeCalendarModal = () => {
    setCalendarOpen(false);
    window.setTimeout(() => setCalendarMounted(false), 220);
  };

  const openRangeModal = () => {
    const from = overviewRange?.from ?? overview?.earliestDate ?? todayKey;
    const to = overviewRange?.to ?? todayKey;
    setRangeFrom(from);
    setRangeTo(to);
    setRangeResult(null);
    setRangeError('');
    setRangeMounted(true);
    setRangeOpen(true);
  };

  const closeRangeModal = () => {
    setRangeOpen(false);
    window.setTimeout(() => setRangeMounted(false), 220);
  };

  const submitRange = async () => {
    if (!rangeFrom || !rangeTo) {
      setRangeError('请选择起始日期和结束日期');
      return;
    }
    if (rangeFrom > rangeTo) {
      setRangeError('起始日期不能晚于结束日期');
      return;
    }
    setRangeLoading(true);
    setRangeError('');
    try {
      const result = await api.rangeSummary(rangeFrom, rangeTo, subjectFilter);
      setRangeResult(result);
      onOverviewRangeChange(result);
      closeRangeModal();
      void api.saveOverviewRange(rangeFrom, rangeTo).catch(() => undefined);
    } catch {
      setRangeError('区间统计暂时不可用，请稍后重试');
    } finally {
      setRangeLoading(false);
    }
  };

  const calendarDays = useMemo(() => {
    const byDate = new Map(displayDaily.map((item) => [item.date, item.seconds]));
    const end = parseDateKey(overviewRange?.to ?? overview?.date ?? formatDateKey(new Date()));
    const values = Array.from({ length: 30 }, (_, index) => {
      const date = new Date(end);
      date.setDate(end.getDate() - (29 - index));
      const key = formatDateKey(date);
      return { date: key, seconds: byDate.get(key) ?? 0 };
    });
    const maxSeconds = Math.max(1, ...values.map((item) => item.seconds));
    return values.map((item) => ({
      ...item,
      intensity: item.seconds > 0 ? 0.16 + (item.seconds / maxSeconds) * 0.84 : 0.06,
    }));
  }, [displayDaily, overview?.date, overviewRange?.to]);

  const active = displayedOverview.active;
  const activeTitle = active?.windowTitle || active?.processName || '当前无活动会话';
  const displayTotalSeconds = overviewRange?.seconds ?? displayedOverview.totalSeconds;
  const displayTrackedDays = overviewRange?.trackedDays ?? displayedOverview.trackedDays;
  const displaySessionCount = overviewRange?.sessionCount ?? displayedOverview.sessionCount;
  const activeElapsed = active
    ? formatDurationShort(Math.max(0, (clockNow - new Date(active.startTime).getTime()) / 1000))
    : '';
  const rangeAverageSeconds = overviewRange && overviewRange.trackedDays > 0
    ? overviewRange.seconds / overviewRange.trackedDays
    : 0;

  const resetRangeMode = () => {
    void api.clearOverviewRange();
    onOverviewRangeChange(null);
    setRangeResult(null);
  };

  return (
    <LoadingTransition loading={!overview} className="overview-loading-transition">
    <div className="page overview-page">
      <div className="page-header overview-page-header">
        <div>
          <div className="page-kicker">TIME INSIGHT · DAILY BRIEF</div>
          <h2>总览</h2>
          <div className="page-subtitle">{isRangeMode ? '区间统计模式' : '你的时间使用脉络'}</div>
        </div>
        <div className="overview-header-controls">
          <div className="overview-date-status-row">
            <div className={`overview-live-status${isRangeMode ? ' is-active range-status' : active ? ' is-active' : ''}`}>
              <span className="status-pulse" />
              {isRangeMode ? '正在查看自定义区间' : active ? `正在使用 · ${activeElapsed}` : '当前无活动会话'}
            </div>
          </div>
          <div className="overview-action-row overview-category-row">
            <SubjectFilterMenu subjects={subjects} value={subjectFilter} onChange={setSubjectFilter} />
          </div>
        </div>
      </div>

      <section className="overview-hero panel">
        <div className="overview-hero-copy">
          <span className="eyebrow">{isRangeMode ? 'CUSTOM RANGE' : "TODAY'S RHYTHM"}</span>
          <h3>{isRangeMode ? '区间数据已更新' : active ? '专注正在继续' : '今天的记录已准备好'}</h3>
          <p title={activeTitle}>
            {isRangeMode ? `${overviewRange.from} 至 ${overviewRange.to} · ${displaySessionCount.toLocaleString()} 条会话` : active ? activeTitle : `已累计记录 ${displayedOverview.sessionCount.toLocaleString()} 条使用会话`}
          </p>
        </div>
        <div className="overview-hero-focus">
          <span>{isRangeMode ? '区间总使用' : '今日使用'}</span>
          <strong>{formatHoursMinutes(isRangeMode ? displayTotalSeconds : displayedOverview.todaySeconds)}</strong>
          <small>{topProcess ? `最常使用 · ${topProcess.key}` : '等待更多活动数据'}</small>
        </div>
        <div className="overview-hero-orbit" aria-hidden="true">
          <span />
          <span />
          <span />
        </div>
      </section>

      <div className="overview-data-content">
      <div className="overview-chart-grid">
        <section className="panel overview-trend-panel">
          <div className="chart-heading">
            <div>
              <span className="eyebrow">{isRangeMode ? 'SELECTED RANGE' : 'LAST 30 DAYS'}</span>
              <h3>{isRangeMode ? '区间每日使用节奏' : '每日使用节奏'}</h3>
            </div>
            <span className="chart-heading-note">小时</span>
          </div>
          <TrendChart data={displayDaily} theme={theme.colors} />
        </section>
        <RankingChart data={displayTop} title={isRangeMode ? '区间应用排行' : '今日应用排行'} theme={theme.colors} />
      </div>

      <div className="card-grid overview-metrics">
        <div className="stat-card">
          <div className="stat-card-top"><span className="label">{isRangeMode ? '区间总时长' : '今日'}</span><span className="stat-card-mark">◷</span></div>
          <div className="value accent">{formatHoursMinutes(isRangeMode ? displayTotalSeconds : displayedOverview.todaySeconds)}</div>
          <div className="hint">{isRangeMode ? `${overviewRange.from} → ${overviewRange.to}` : `${displayedOverview.date} · 日界线 04:00`}</div>
        </div>
        <div className="stat-card">
          <div className="stat-card-top"><span className="label">{isRangeMode ? '日均时长' : '本周'}</span><span className="stat-card-mark">⌁</span></div>
          <div className="value">{formatHoursMinutes(isRangeMode ? rangeAverageSeconds : displayedOverview.weekSeconds)}</div>
          <div className="hint">{isRangeMode ? `基于 ${displayTrackedDays} 个有记录日` : '周一起算'}</div>
        </div>
        <button className="stat-card month-stat-card" onClick={openCalendarModal}>
          <div className="stat-card-top"><span className="label">{isRangeMode ? '区间天数' : '本月'}</span><span className="stat-card-mark">▦</span></div>
          <div className="value">{isRangeMode ? displayTrackedDays : formatHoursMinutes(displayedOverview.monthSeconds)}</div>
          <div className="hint">{isRangeMode ? '有使用记录的天数' : `${displayedOverview.date.slice(0, 7)} · 点击查看 30 天`}</div>
        </button>
        <button className="stat-card cumulative-stat-card" onClick={openRangeModal}>
          <div className="stat-card-top"><span className="label">{isRangeMode ? '当前区间' : '累计'}</span><span className="stat-card-mark">✦</span></div>
          <div className="value">{isRangeMode ? displaySessionCount.toLocaleString() : formatHoursMinutes(displayedOverview.totalSeconds)}</div>
          <div className="hint">{isRangeMode ? '条会话 · 点击修改区间' : `自 ${displayedOverview.earliestDate ?? '—'} · 点击选择区间`}</div>
        </button>
        <div className="stat-card">
          <div className="stat-card-top"><span className="label">{isRangeMode ? '应用数量' : '记录天数'}</span><span className="stat-card-mark">◇</span></div>
          <div className="value">{isRangeMode ? overviewRange.processCount : displayedOverview.trackedDays}</div>
          <div className="hint">{isRangeMode ? '区间内使用过的进程' : `共 ${displayedOverview.sessionCount.toLocaleString()} 条会话`}</div>
        </div>
      </div>

      {isRangeMode && (
        <div className="range-mode-bar">
          <span>当前页面正在显示：{overviewRange.from} → {overviewRange.to}</span>
          <button className="toolbar-button" onClick={resetRangeMode}>恢复默认总览          </button>
        </div>
      )}

      </div>

      {calendarMounted && (
        <div className={`calendar-modal-backdrop${calendarOpen ? ' modal-open' : ' modal-closing'}`} onMouseDown={closeCalendarModal}>
          <section
            className="calendar-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="calendar-title"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <div className="calendar-modal-header">
              <div>
                <span className="eyebrow">30 DAY HEATMAP</span>
                <h3 id="calendar-title">本月使用日历</h3>
                <p>使用时长越长，颜色越深</p>
              </div>
              <button className="calendar-close" onClick={closeCalendarModal} aria-label="关闭月历">×</button>
            </div>
            <div className="calendar-weekdays">
              {['一', '二', '三', '四', '五', '六', '日'].map((day) => <span key={day}>周{day}</span>)}
            </div>
            <div className="calendar-grid">
              {calendarDays.map((item) => {
                const date = parseDateKey(item.date);
                return (
                  <div
                    className="calendar-day"
                    key={item.date}
                    style={{ '--heat': item.intensity } as CSSProperties}
                    title={`${item.date} · ${formatHoursMinutes(item.seconds)}`}
                  >
                    <span>{date.getDate()}</span>
                    <small>{item.seconds > 0 ? formatHoursMinutes(item.seconds) : '—'}</small>
                  </div>
                );
              })}
            </div>
            <div className="calendar-legend">
              <span>少</span><i className="heat-swatch heat-low" /><i className="heat-swatch heat-mid" /><i className="heat-swatch heat-high" /><span>多</span>
            </div>
          </section>
        </div>
      )}

      {rangeMounted && (
        <div className={`calendar-modal-backdrop${rangeOpen ? ' modal-open' : ' modal-closing'}`} onMouseDown={closeRangeModal}>
          <section
            className="calendar-modal range-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="range-title"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <div className="calendar-modal-header">
              <div>
                <span className="eyebrow">CUSTOM RANGE</span>
                <h3 id="range-title">累计使用区间</h3>
                <p>选择起止日期，查看这段时间的总使用时长</p>
              </div>
              <button className="calendar-close" onClick={closeRangeModal} aria-label="关闭区间统计">×</button>
            </div>
            <div className="range-form">
              <label>
                <span>开始日期</span>
                <input type="date" value={rangeFrom} max={rangeTo || todayKey} onChange={(event) => setRangeFrom(event.target.value)} />
              </label>
              <span className="range-arrow">→</span>
              <label>
                <span>结束日期</span>
                <input type="date" value={rangeTo} min={rangeFrom || undefined} max={todayKey} onChange={(event) => setRangeTo(event.target.value)} />
              </label>
            </div>
            <div className="range-actions">
              <button className="toolbar-button" onClick={() => setRangeTo(todayKey)}>结束日期设为今天</button>
              <button className="toolbar-button active" onClick={() => void submitRange()} disabled={rangeLoading}>
                {rangeLoading ? '统计中…' : '查看区间统计'}
              </button>
            </div>
            {rangeError && <div className="range-error">{rangeError}</div>}
            {rangeResult && (
              <div className="range-result">
                <div><span>区间总时长</span><strong>{formatHoursMinutes(rangeResult.seconds)}</strong></div>
                <div><span>统计范围</span><strong>{rangeResult.from} → {rangeResult.to}</strong></div>
                <div><span>有记录的天数</span><strong>{rangeResult.trackedDays} 天</strong></div>
              </div>
            )}
          </section>
        </div>
      )}
    </div>
    </LoadingTransition>
  );
}
