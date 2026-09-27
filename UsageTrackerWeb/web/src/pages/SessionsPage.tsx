import { useEffect, useState } from 'react';
import LoadingTransition from '../components/LoadingTransition';
import { api } from '../lib/api';
import type { SessionDto } from '../lib/api';
import { formatDurationShort, formatHoursMinutes } from '../lib/format';

const PAGE_SIZE = 50;

export default function SessionsPage() {
  const [keyword, setKeyword] = useState('');
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<SessionDto[]>([]);
  const [total, setTotal] = useState(0);
  const [skip, setSkip] = useState(0);
  const [pageInput, setPageInput] = useState('1');
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let version = '';
    const load = async (force = false) => {
      try {
        const current = await api.searchVersion(true);
        if (!force && current.version === version) return;
        version = current.version;
        setLoading(true);
        const result = await api.search(query, skip, PAGE_SIZE, true);
        if (cancelled) return;
        setItems(result.items);
        setTotal(result.totalCount);
      } catch {
        // 保留当前列表，等待下一轮检测。
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    void load(true);
    const timer = window.setInterval(() => void load(), 5000);
    const onFocus = () => load();
    window.addEventListener('focus', onFocus);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
      window.removeEventListener('focus', onFocus);
    };
  }, [query, skip]);

  const pageIndex = Math.floor(skip / PAGE_SIZE);
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE));

  useEffect(() => {
    setPageInput(String(pageIndex + 1));
  }, [pageIndex]);

  const jumpToPage = () => {
    const requested = Number.parseInt(pageInput, 10);
    if (!Number.isFinite(requested)) {
      setPageInput(String(pageIndex + 1));
      return;
    }
    const target = Math.min(pageCount, Math.max(1, requested));
    setPageInput(String(target));
    setSkip((target - 1) * PAGE_SIZE);
  };

  const runningCount = items.filter((item) => !item.endTime).length;
  const visibleSeconds = items.reduce((sum, item) => {
    const start = new Date(item.startTime);
    const end = item.endTime ? new Date(item.endTime) : null;
    return sum + (item.durationSeconds ?? (end ? (end.getTime() - start.getTime()) / 1000 : 0));
  }, 0);

  const runSearch = () => {
    setSkip(0);
    setQuery(keyword.trim());
  };

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>使用明细</h2>
          <div className="page-subtitle">完整记录每一次应用与窗口活动</div>
        </div>
        <div className="header-actions session-search">
          <div className="search-shell">
            <span className="search-symbol">⌕</span>
            <input
              className="search-input"
              value={keyword}
              placeholder="搜索进程名或窗口标题"
              onChange={(e) => setKeyword(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') runSearch();
              }}
            />
            {keyword && (
              <button className="search-clear" onClick={() => setKeyword('')} aria-label="清除搜索">
                ×
              </button>
            )}
          </div>
          <button className="toolbar-button active" onClick={runSearch}>搜索</button>
        </div>
      </div>

      <div className="insight-strip sessions-insight">
        <div className="insight-item">
          <span className="insight-label">全部记录</span>
          <strong>{total.toLocaleString()}</strong>
          <span className="insight-note">匹配当前搜索条件</span>
        </div>
        <div className="insight-item">
          <span className="insight-label">当前页</span>
          <strong>{items.length}</strong>
          <span className="insight-note">每页最多 {PAGE_SIZE} 条</span>
        </div>
        <div className="insight-item">
          <span className="insight-label">本页时长</span>
          <strong>{formatHoursMinutes(visibleSeconds)}</strong>
          <span className="insight-note">{runningCount > 0 ? `${runningCount} 条进行中` : '暂无进行中会话'}</span>
        </div>
      </div>

      <LoadingTransition loading={loading} className="sessions-loading-transition">
      <div className="panel session-table-shell">
        <div className="session-table">
          <div className="session-row session-head">
            <span>窗口标题</span>
            <span>进程</span>
            <span>开始时间</span>
            <span>使用时长</span>
            <span>分类</span>
          </div>
          {items.map((item) => {
            const start = new Date(item.startTime);
            const end = item.endTime ? new Date(item.endTime) : null;
            const seconds = item.durationSeconds ??
              (end ? (end.getTime() - start.getTime()) / 1000 : 0);
            return (
              <div className="session-row" key={item.id}>
                <span className="session-title ellipsis" title={item.windowTitle}>
                  <span className="session-title-dot" />
                  {item.windowTitle || '未命名窗口'}
                </span>
                <span className="session-process ellipsis" title={item.processName}>
                  {item.processName}
                </span>
                <span className="session-start">{start.toLocaleString('zh-CN', { hour12: false })}</span>
                <span className={`session-duration${!end ? ' running' : ''}`}>
                  {!end && <span className="live-dot" />}
                  {end ? formatDurationShort(seconds) : `进行中 · ${formatDurationShort(seconds)}`}
                  {seconds > 3600 && <small>{formatHoursMinutes(seconds)}</small>}
                </span>
                <span className={`session-subject${item.manualSubject ? '' : ' muted'}`}>
                  {item.manualSubject ?? '未分类'}
                </span>
              </div>
            );
          })}
          {!loading && items.length === 0 && <div className="loading loading-inline">没有匹配的记录</div>}
        </div>
      </div>
      </LoadingTransition>

      <div className="pager">
        <button
          className="toolbar-button"
          disabled={pageIndex === 0}
          onClick={() => setSkip(Math.max(0, skip - PAGE_SIZE))}
        >
          ← 上一页
        </button>
        <input
          className="pager-input"
          aria-label="跳转页码"
          inputMode="numeric"
          pattern="[0-9]*"
          value={pageInput}
          onChange={(event) => setPageInput(event.target.value.replace(/\D/g, ''))}
          onKeyDown={(event) => {
            if (event.key === 'Enter') jumpToPage();
          }}
          onBlur={jumpToPage}
        />
        <span className="pager-total">/ {pageCount}</span>
        <button
          className="toolbar-button"
          disabled={pageIndex + 1 >= pageCount}
          onClick={() => setSkip(skip + PAGE_SIZE)}
        >
          下一页 →
        </button>
      </div>
    </div>
  );
}
