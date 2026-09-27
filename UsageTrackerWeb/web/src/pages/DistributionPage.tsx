import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import LoadingTransition from '../components/LoadingTransition';
import TimeDistribution, { type DistributionMergeMode } from '../components/TimeDistribution';
import { api } from '../lib/api';
import type { DistributionResponse, SubjectDefinition } from '../lib/api';
import { formatDateKey, formatHoursMinutes, getTimeDistributionDate } from '../lib/format';
import type { ThemeController } from '../theme';

const RANGES = [
  { label: '7 天', days: 7 },
  { label: '14 天', days: 14 },
  { label: '30 天', days: 30 },
  { label: '60 天', days: 60 },
];

const REFRESH_INTERVAL_MS = 15000;

// 镜像 WPF TimeDistributionPage.BuildSubjectFilterGroups：
// 选中某个分类时，命中集合 = 该分类自身 + 其下所有父类/子类名称。
function buildSubjectFilterGroups(definitions: SubjectDefinition[]) {
  const groups = new Map<string, Set<string>>();
  const add = (key: string, value: string) => {
    const lowerKey = key.toLowerCase();
    let set = groups.get(lowerKey);
    if (!set) {
      set = new Set<string>();
      groups.set(lowerKey, set);
    }
    set.add(value.toLowerCase());
  };

  for (const major of definitions) {
    add(major.name, major.name);
    for (const child of major.children ?? []) {
      add(major.name, child);
      add(child, child);
    }
    for (const parent of major.parents ?? []) {
      add(major.name, parent.name);
      add(parent.name, parent.name);
      for (const child of parent.children ?? []) {
        add(major.name, child);
        add(parent.name, child);
        add(child, child);
      }
    }
  }
  return groups;
}

function matchesSubjectFilter(
  subject: string | null | undefined,
  filter: string | null,
  groups: Map<string, Set<string>>
) {
  if (!filter) return true;
  if (!subject) return false;
  const group = groups.get(filter.toLowerCase());
  if (group) return group.has(subject.toLowerCase());
  return subject.toLowerCase() === filter.toLowerCase();
}

interface Props {
  active: boolean;
  theme: ThemeController;
  subjects: SubjectDefinition[];
  mergeMode: DistributionMergeMode;
}

export default function DistributionPage({ active, theme, subjects, mergeMode }: Props) {
  const [days, setDays] = useState(14);
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [data, setData] = useState<DistributionResponse | null>(null);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState(false);
  const [subjectFilter, setSubjectFilter] = useState<string | null>(null);
  const [collapsedMajors, setCollapsedMajors] = useState<Set<string>>(new Set());
  const [collapsedParents, setCollapsedParents] = useState<Set<string>>(new Set());
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
  const [rangeMenuOpen, setRangeMenuOpen] = useState(false);
  const collapseDefaultsInitializedRef = useRef(false);
  const loadingRef = useRef(false);
  const menuCloseTimerRef = useRef<number | null>(null);
  const rangeMenuCloseTimerRef = useRef<number | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const rangeMenuRef = useRef<HTMLDivElement>(null);

  const openMenu = () => {
    if (menuCloseTimerRef.current !== null) {
      window.clearTimeout(menuCloseTimerRef.current);
      menuCloseTimerRef.current = null;
    }
    setMenuMounted(true);
    setMenuOpen(true);
  };

  const closeMenu = () => {
    setMenuOpen(false);
    if (menuCloseTimerRef.current !== null) window.clearTimeout(menuCloseTimerRef.current);
    menuCloseTimerRef.current = window.setTimeout(() => {
      setMenuMounted(false);
      menuCloseTimerRef.current = null;
    }, 180);
  };

  const scheduleMenuClose = () => {
    if (menuCloseTimerRef.current !== null) window.clearTimeout(menuCloseTimerRef.current);
    menuCloseTimerRef.current = window.setTimeout(() => {
      closeMenu();
    }, 260);
  };

  const cancelRangeMenuClose = () => {
    if (rangeMenuCloseTimerRef.current !== null) {
      window.clearTimeout(rangeMenuCloseTimerRef.current);
      rangeMenuCloseTimerRef.current = null;
    }
  };

  const scheduleRangeMenuClose = () => {
    cancelRangeMenuClose();
    rangeMenuCloseTimerRef.current = window.setTimeout(() => {
      setRangeMenuOpen(false);
      rangeMenuCloseTimerRef.current = null;
    }, 220);
  };

  useEffect(() => () => {
    if (menuCloseTimerRef.current !== null) window.clearTimeout(menuCloseTimerRef.current);
    if (rangeMenuCloseTimerRef.current !== null) window.clearTimeout(rangeMenuCloseTimerRef.current);
  }, []);

  useEffect(() => {
    if (!menuOpen && !rangeMenuOpen) return;
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (menuOpen && menuRef.current && !menuRef.current.contains(target)) closeMenu();
      if (rangeMenuOpen && rangeMenuRef.current && !rangeMenuRef.current.contains(target)) {
        cancelRangeMenuClose();
        setRangeMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [menuOpen, rangeMenuOpen]);

  const groups = useMemo(() => buildSubjectFilterGroups(subjects), [subjects]);

  useEffect(() => {
    if (collapseDefaultsInitializedRef.current || subjects.length === 0) return;

    const majorKeys = new Set(subjects.map((major) => major.name));
    const parentKeys = new Set(
      subjects.flatMap((major) =>
        (major.parents ?? []).map((parent) => `${major.name}/${parent.name}`)
      )
    );
    setCollapsedMajors(majorKeys);
    setCollapsedParents(parentKeys);
    collapseDefaultsInitializedRef.current = true;
  }, [subjects]);

  const visibleSessions = useMemo(() => {
    if (!data) return [];
    return data.sessions.filter((session) =>
      matchesSubjectFilter(session.manualSubject, subjectFilter, groups)
    );
  }, [data, subjectFilter, groups]);

  const visibleTotalSeconds = useMemo(() => {
    const now = Date.now();
    return visibleSessions.reduce((total, session) => {
      const start = new Date(session.startTime).getTime();
      const end = session.endTime ? new Date(session.endTime).getTime() : now;
      return total + Math.max(0, end - start) / 1000;
    }, 0);
  }, [visibleSessions]);

  const activeCount = useMemo(
    () => visibleSessions.filter((session) => !session.endTime).length,
    [visibleSessions]
  );

  const load = useCallback(async (forceRefresh = false) => {
    if (loadingRef.current) return;
    loadingRef.current = true;
    if (forceRefresh) {
      setRefreshing(true);
      setRefreshError(false);
    }
    try {
      const today = getTimeDistributionDate(new Date());
      const from = new Date(today);
      from.setDate(from.getDate() - (days - 1));
      const result = await api.distribution(formatDateKey(from), formatDateKey(today), forceRefresh);
      setData(result);
      setUpdatedAt(new Date());
      setRefreshError(false);
    } catch {
      if (forceRefresh) setRefreshError(true);
    } finally {
      loadingRef.current = false;
      if (forceRefresh) setRefreshing(false);
    }
  }, [days]);

  useEffect(() => {
    if (!active) return;
    void load();
  }, [active, load]);

  useEffect(() => {
    if (!active || !autoRefresh) return;
    const timer = setInterval(() => void load(), REFRESH_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [active, autoRefresh, load]);

  const distributionTheme = {
    panel: theme.colors.panel,
    border: theme.colors.border,
    textSecondary: theme.colors.textSecondary,
    accent: theme.colors.accent,
    accentSoft: theme.colors.accentSoft,
    textPrimary: theme.colors.textPrimary,
  };

  return (
    <div className="page distribution-page">
      <div className="page-header">
        <div>
          <h2>时长分布</h2>
          <div className="page-subtitle">
            最新日期在最上方 · 每天 4:00 为分界
            {updatedAt && ` · 更新于 ${updatedAt.toLocaleTimeString('zh-CN')}`}
            {refreshing && <span className="refresh-status"> · 正在刷新…</span>}
            {refreshError && <span className="refresh-status error"> · 刷新失败，仍显示上次数据</span>}
            {data && ` · ${visibleSessions.length} / ${data.sessions.length} 段`}
          </div>
        </div>
        <div className="header-actions">
          <div
            className={`range-wheel${rangeMenuOpen ? ' is-open' : ''}`}
            aria-label="时间范围选择"
            ref={rangeMenuRef}
            onPointerEnter={cancelRangeMenuClose}
            onPointerLeave={() => {
              if (rangeMenuOpen) scheduleRangeMenuClose();
            }}
          >
            <button
              className="toolbar-button range-wheel-current"
              aria-haspopup="menu"
              aria-expanded={rangeMenuOpen}
              onClick={() => {
                cancelRangeMenuClose();
                setRangeMenuOpen((open) => !open);
              }}
            >
              {RANGES.find((item) => item.days === days)?.label} ▾
            </button>
            <div className="range-wheel-options" role="menu">
              {RANGES.map((item) => (
                <button
                  key={item.days}
                  className={`range-wheel-option${days === item.days ? ' active' : ''}`}
                  role="menuitemradio"
                  aria-checked={days === item.days}
                  onClick={() => {
                    setDays(item.days);
                    setRangeMenuOpen(false);
                  }}
                >
                  {item.label}
                </button>
              ))}
            </div>
          </div>
          <button
            className={`toolbar-button${autoRefresh ? ' active' : ''}`}
            onClick={() => setAutoRefresh((v) => !v)}
          >
            自动刷新
          </button>
          <button
            className={`toolbar-button refresh-button${refreshing ? ' refreshing' : ''}`}
            onClick={() => void load(true)}
            disabled={refreshing}
            aria-busy={refreshing}
          >
            <span className="refresh-icon" aria-hidden="true">↻</span>
            {refreshing ? '刷新中…' : '立即刷新'}
          </button>

          <div
            className={`subject-filter${menuMounted ? ' menu-mounted' : ''}${menuOpen ? ' menu-open' : ''}`}
            ref={menuRef}
            onPointerEnter={() => {
              if (menuMounted) openMenu();
            }}
            onPointerLeave={() => {
              if (menuOpen) scheduleMenuClose();
            }}
          >
            <button
              className={`toolbar-button${subjectFilter ? ' active' : ''}`}
              aria-haspopup="menu"
              aria-expanded={menuOpen}
              onClick={() => (menuOpen ? closeMenu() : openMenu())}
            >
              {subjectFilter ?? '全部分类'} ▾
            </button>
            {menuMounted && (
              <div className="subject-filter-menu" role="menu">
                <button
                  className={`subject-filter-item${subjectFilter === null ? ' active' : ''}`}
                  onClick={() => {
                    setSubjectFilter(null);
                    closeMenu();
                  }}
                >
                  全部分类
                </button>
                {subjects.map((major) => {
                  const majorKey = major.name;
                  const isMajorCollapsed = collapseDefaultsInitializedRef.current
                    ? collapsedMajors.has(majorKey)
                    : true;
                  const parents = major.parents ?? [];
                  const directChildren = major.children ?? [];
                  const hasChildren = parents.length > 0 || directChildren.length > 0;
                  return (
                    <div className="subject-filter-major" key={majorKey}>
                      <div className="subject-filter-major-row">
                        <button
                          className={`subject-filter-item major-item${subjectFilter === major.name ? ' active' : ''}`}
                          onClick={() => {
                            setSubjectFilter(major.name);
                            closeMenu();
                          }}
                        >
                          <span className="subject-filter-level-dot" />
                          {major.name}
                        </button>
                        {hasChildren && (
                          <button
                            className="subject-filter-collapse major-collapse"
                            aria-label={`${isMajorCollapsed ? '展开' : '折叠'}${major.name}分类`}
                            aria-expanded={!isMajorCollapsed}
                            onClick={() => {
                              setCollapsedMajors((current) => {
                                const next = new Set(current);
                                if (next.has(majorKey)) next.delete(majorKey);
                                else next.add(majorKey);
                                return next;
                              });
                            }}
                          >
                            {isMajorCollapsed ? '＋' : '−'}
                          </button>
                        )}
                      </div>
                      {!isMajorCollapsed && (
                        <div className="subject-filter-major-children">
                          {parents.map((parent) => {
                            const parentKey = `${major.name}/${parent.name}`;
                            const children = parent.children ?? [];
                            const isCollapsed = collapseDefaultsInitializedRef.current
                              ? collapsedParents.has(parentKey)
                              : true;
                            return (
                              <div className="subject-filter-parent" key={parentKey}>
                                <div className="subject-filter-parent-row">
                                  <button
                                    className={`subject-filter-item level-1${subjectFilter === parent.name ? ' active' : ''}`}
                                    onClick={() => {
                                      setSubjectFilter(parent.name);
                                      closeMenu();
                                    }}
                                  >
                                    {parent.name}
                                  </button>
                                  {children.length > 0 && (
                                    <button
                                      className="subject-filter-collapse"
                                      aria-label={`${isCollapsed ? '展开' : '折叠'}${parent.name}子类`}
                                      aria-expanded={!isCollapsed}
                                      onClick={() => {
                                        setCollapsedParents((current) => {
                                          const next = new Set(current);
                                          if (next.has(parentKey)) next.delete(parentKey);
                                          else next.add(parentKey);
                                          return next;
                                        });
                                      }}
                                    >
                                      {isCollapsed ? '＋' : '−'}
                                    </button>
                                  )}
                                </div>
                                {!isCollapsed && children.map((child) => (
                                  <button
                                    key={child}
                                    className={`subject-filter-item level-2${subjectFilter === child ? ' active' : ''}`}
                                    onClick={() => {
                                      setSubjectFilter(child);
                                      closeMenu();
                                    }}
                                  >
                                    {child}
                                  </button>
                                ))}
                              </div>
                            );
                          })}
                          {directChildren.map((child) => (
                            <button
                              key={child}
                              className={`subject-filter-item level-1 direct-child${subjectFilter === child ? ' active' : ''}`}
                              onClick={() => {
                                setSubjectFilter(child);
                                closeMenu();
                              }}
                            >
                              {child}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      </div>

      <LoadingTransition loading={!data} className="distribution-loading-transition">
        {data ? (
        <>
          <div className="distribution-summary">
            <div className="distribution-summary-card featured">
              <span className="summary-icon">◴</span>
              <div><span>可视时长</span><strong>{formatHoursMinutes(visibleTotalSeconds)}</strong></div>
              <small>{subjectFilter ?? '全部分类'}</small>
            </div>
            <div className="distribution-summary-card">
              <span className="summary-icon">▥</span>
              <div><span>时间片段</span><strong>{visibleSessions.length}</strong></div>
              <small>共 {data.sessions.length} 段</small>
            </div>
            <div className="distribution-summary-card">
              <span className="summary-icon">●</span>
              <div><span>当前进行中</span><strong>{activeCount}</strong></div>
              <small>{autoRefresh ? '自动更新已开启' : '自动更新已暂停'}</small>
            </div>
          </div>
          <TimeDistribution
            active={active}
            dates={data.dates}
            sessions={visibleSessions}
            theme={distributionTheme}
            mergeMode={mergeMode}
            height="100%"
          />
        </>
      ) : null}
      </LoadingTransition>
    </div>
  );
}
