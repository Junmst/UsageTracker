import { useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react';
import LoadingTransition from '../components/LoadingTransition';
import SearchModeMenu from '../components/SearchModeMenu';
import DatePickerPopover from '../components/DatePickerPopover';
import { api } from '../lib/api';
import type { SessionDto, SubjectDefinition } from '../lib/api';
import { formatDurationShort, formatHoursMinutes, formatDateKey, getTimeDistributionDate } from '../lib/format';

const PAGE_SIZE = 50;

export default function SessionsPage() {
  const [keyword, setKeyword] = useState('');
  const [query, setQuery] = useState('');
  const [mode, setMode] = useState('all');
  const [allHistory, setAllHistory] = useState(false);
  const [selectedDate, setSelectedDate] = useState(formatDateKey(getTimeDistributionDate(new Date())));
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [subjectDefinitions, setSubjectDefinitions] = useState<SubjectDefinition[]>([]);
  const [actionMessage, setActionMessage] = useState('');
  const [actionBusy, setActionBusy] = useState(false);
  const [canUndoAction, setCanUndoAction] = useState(false);
  const [items, setItems] = useState<SessionDto[]>([]);
  const [total, setTotal] = useState(0);
  const [skip, setSkip] = useState(0);
  const [pageInput, setPageInput] = useState('1');
  const [loading, setLoading] = useState(false);
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number } | null>(null);
  const [collapsedContextMajors, setCollapsedContextMajors] = useState<Set<string>>(new Set());
  const [selectionBox, setSelectionBox] = useState<{ x: number; y: number; width: number; height: number } | null>(null);
  const tableRef = useRef<HTMLDivElement>(null);
  const rowRefs = useRef(new Map<string, HTMLDivElement>());
  const pointerRef = useRef<{ id: string; x: number; y: number; moved: boolean; pointerId: number } | null>(null);
  const suppressClickRef = useRef(false);
  const anchorIdRef = useRef<string | null>(null);

  const selectedItems = useMemo(() => items.filter((item) => selectedIds.has(item.id)), [items, selectedIds]);
  const selectedIdList = selectedId ? (selectedIds.has(selectedId) ? [...selectedIds] : [selectedId]) : [];

  useEffect(() => {
    void api.settings().then((value) => {
      const definitions = value.subjectDefinitions ?? [];
      setSubjectDefinitions(definitions);
      setCollapsedContextMajors(new Set(definitions.map((major) => major.name)));
    }).catch(() => undefined);
  }, []);

  useEffect(() => {
    let cancelled = false;
    let version = '';
    const load = async (force = false) => {
      try {
        const current = await api.searchVersion(true);
        if (!force && current.version === version) return;
        version = current.version;
        setLoading(true);
        const result = await api.search(query, skip, PAGE_SIZE, true, {
          date: selectedDate,
          allHistory,
          mode,
        });
        if (cancelled) return;
        setItems(result.items);
        setTotal(result.totalCount);
        const visible = new Set(result.items.map((item) => item.id));
        setSelectedIds((current) => new Set([...current].filter((id) => visible.has(id))));
        setSelectedId((currentId) => result.items.some((item) => item.id === currentId) ? currentId : null);
      } catch {
        // 保留当前列表，等待下一轮检测。
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    void load(true);
    const timer = window.setInterval(() => void load(), 5000);
    const onFocus = () => void load();
    window.addEventListener('focus', onFocus);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
      window.removeEventListener('focus', onFocus);
    };
  }, [query, skip, selectedDate, allHistory, mode]);

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

  // 分类按“进程 + 窗口标题”生效：一次提交一个代表记录，后端会同步同进程同标题的全部记录。
  const setSelectedSubject = async (subject: string | null) => {
    const targets = selectedIdList.length > 0 ? selectedItems : [];
    const uniqueTargets: SessionDto[] = [];
    const seen = new Set<string>();
    targets.forEach((item) => {
      const key = `${item.processName}\u0000${item.windowTitle}`;
      if (seen.has(key)) return;
      seen.add(key);
      uniqueTargets.push(item);
    });
    if (uniqueTargets.length === 0) return;
    setActionBusy(true);
    setActionMessage(`正在修改分类（${uniqueTargets.length} 组进程/窗口）…`);
    setContextMenu(null);
    try {
      await api.sessionCommand('session-bulk-set-subject', {
        sessions: uniqueTargets,
        subject,
        targetDate: allHistory ? null : selectedDate,
      });
      const keys = new Set(targets.map((item) => `${item.processName}\u0000${item.windowTitle}`));
      setItems((current) => current.map((entry) => keys.has(`${entry.processName}\u0000${entry.windowTitle}`) ? { ...entry, manualSubject: subject } : entry));
      setActionMessage('分类已更新（同进程同窗口名称的记录一并生效）');
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : '修改失败');
    } finally {
      setActionBusy(false);
    }
  };

  const deleteSelected = async () => {
    const targets = selectedIdList.length > 0 ? selectedItems : [];
    if (targets.length === 0 || !window.confirm(`确定删除选中的 ${targets.length} 条记录吗？`)) return;
    setActionBusy(true);
    setActionMessage('正在删除…');
    setContextMenu(null);
    try {
      await api.bulkDeleteSessions(targets);
      const removed = new Set(targets.map((item) => item.id));
      setItems((current) => current.filter((entry) => !removed.has(entry.id)));
      setTotal((current) => Math.max(0, current - removed.size));
      setSelectedIds(new Set());
      setSelectedId(null);
      setCanUndoAction(true);
      setActionMessage(`已删除 ${removed.size} 条记录，可撤销`);
    } catch (error) {
      setActionMessage(error instanceof Error ? error.message : '删除失败');
    } finally {
      setActionBusy(false);
    }
  };

  const selectRange = (id: string, additive: boolean) => {
    const anchorIndex = anchorIdRef.current ? items.findIndex((item) => item.id === anchorIdRef.current) : -1;
    const targetIndex = items.findIndex((item) => item.id === id);
    if (anchorIndex < 0 || targetIndex < 0) {
      setSelectedIds(new Set([id]));
      setSelectedId(id);
      anchorIdRef.current = id;
      return;
    }
    const start = Math.min(anchorIndex, targetIndex);
    const end = Math.max(anchorIndex, targetIndex);
    setSelectedIds((current) => {
      const next = additive ? new Set(current) : new Set<string>();
      for (let index = start; index <= end; index += 1) next.add(items[index].id);
      return next;
    });
    setSelectedId(id);
  };

  const toggleRow = (id: string, modifiers?: { shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean }) => {
    if (modifiers?.shiftKey) {
      selectRange(id, Boolean(modifiers.ctrlKey || modifiers.metaKey));
      return;
    }
    if (modifiers?.ctrlKey || modifiers?.metaKey) {
      setSelectedIds((current) => {
        const next = new Set(current);
        if (next.has(id)) next.delete(id);
        else next.add(id);
        return next;
      });
    } else {
      // 再次点击已唯一选中的行 → 取消选中；否则替换为单选
      if (selectedIds.size === 1 && selectedIds.has(id)) {
        setSelectedIds(new Set());
        setSelectedId(null);
        anchorIdRef.current = null;
        return;
      }
      setSelectedIds(new Set([id]));
    }
    setSelectedId(id);
    anchorIdRef.current = id;
  };

  const applyBoxSelection = (startX: number, startY: number, currentX: number, currentY: number) => {
    const container = tableRef.current;
    if (!container) return;
    const base = container.getBoundingClientRect();
    const left = Math.min(startX, currentX) - base.left;
    const top = Math.min(startY, currentY) - base.top;
    const right = Math.max(startX, currentX) - base.left;
    const bottom = Math.max(startY, currentY) - base.top;
    const next = new Set<string>();
    rowRefs.current.forEach((element, id) => {
      const rect = element.getBoundingClientRect();
      const rowLeft = rect.left - base.left;
      const rowTop = rect.top - base.top;
      const rowRight = rowLeft + rect.width;
      const rowBottom = rowTop + rect.height;
      if (rowRight >= left && rowLeft <= right && rowBottom >= top && rowTop <= bottom) next.add(id);
    });
    setSelectedIds(next);
    setSelectedId(next.size > 0 ? [...next][next.size - 1] : null);
  };

  const handleTablePointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    const target = event.target as HTMLElement | null;
    if (target?.closest('input, button, select, textarea')) return;
    const row = target?.closest<HTMLElement>('[data-session-id]');
    pointerRef.current = { id: row?.dataset.sessionId ?? '', x: event.clientX, y: event.clientY, moved: false, pointerId: event.pointerId };
    // 注意：此处不做 setPointerCapture。按下即捕获会让随后的 click 事件重定向到表格容器，
    // 行上的 onClick 永远收不到，导致左键单击无法选中；改为拖动确认后在 move 里捕获。
  };

  const handleTablePointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const pointer = pointerRef.current;
    if (!pointer || pointer.pointerId !== event.pointerId) return;
    if (!pointer.moved && Math.abs(event.clientX - pointer.x) < 4 && Math.abs(event.clientY - pointer.y) < 4) return;
    if (!pointer.moved) {
      // 拖动确认后才捕获指针，保证拖出表格区域仍能持续框选。
      try { event.currentTarget.setPointerCapture(event.pointerId); } catch { /* 捕获失败不影响框选 */ }
    }
    pointer.moved = true;
    suppressClickRef.current = true;
    const container = tableRef.current;
    if (!container) return;
    const base = container.getBoundingClientRect();
    setSelectionBox({
      x: Math.min(pointer.x, event.clientX) - base.left,
      y: Math.min(pointer.y, event.clientY) - base.top,
      width: Math.abs(event.clientX - pointer.x),
      height: Math.abs(event.clientY - pointer.y),
    });
    applyBoxSelection(pointer.x, pointer.y, event.clientX, event.clientY);
  };

  const endTablePointer = (event: ReactPointerEvent<HTMLDivElement>) => {
    const pointer = pointerRef.current;
    if (!pointer || pointer.pointerId !== event.pointerId) return;
    pointerRef.current = null;
    setSelectionBox(null);
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
    // 点空白处（无命中行且未拖动）→ 清空所有选中
    const target = event.target as HTMLElement | null;
    const hitRow = target?.closest('[data-session-id]');
    if (!pointer.moved && !hitRow) {
      setSelectedIds(new Set());
      setSelectedId(null);
      anchorIdRef.current = null;
    }
    // 拖动后的那次 click 会被派发到捕获元素而不是行，suppress 标记不会被消费；
    // 延迟到 click 派发之后清空，避免吞掉下一次真实单击。
    window.setTimeout(() => { suppressClickRef.current = false; }, 0);
  };

  useEffect(() => {
    const closeMenu = () => setContextMenu(null);
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const editing = target?.tagName === 'INPUT' || target?.tagName === 'TEXTAREA' || target?.tagName === 'SELECT' || target?.isContentEditable;
      if ((event.key === 'Delete' || event.key === 'Backspace') && !editing && selectedIdList.length > 0) {
        event.preventDefault();
        void deleteSelected();
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a' && !editing) {
        event.preventDefault();
        if (items.length > 0) {
          setSelectedIds(new Set(items.map((item) => item.id)));
          setSelectedId(items[items.length - 1].id);
          anchorIdRef.current = items[0].id;
        }
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z' && !editing) {
        event.preventDefault();
        setActionBusy(true);
        void api.subjectCommand('undo').then(() => {
          setCanUndoAction(false);
          setActionMessage('已撤销上一步操作');
        }).catch((error) => setActionMessage(error instanceof Error ? error.message : '撤销失败')).finally(() => setActionBusy(false));
      }
      if (event.key === 'Escape') {
        closeMenu();
        setSelectedIds(new Set());
        setSelectedId(null);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('click', closeMenu);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('click', closeMenu);
    };
  }, [selectedId, selectedIdList.length, items]);

  useEffect(() => {
    const onWindowPointerUp = () => {
      if (pointerRef.current) {
        pointerRef.current = null;
        setSelectionBox(null);
        window.setTimeout(() => { suppressClickRef.current = false; }, 0);
      }
    };
    window.addEventListener('pointerup', onWindowPointerUp);
    return () => window.removeEventListener('pointerup', onWindowPointerUp);
  }, []);

  const handleRowContextMenu = (event: ReactMouseEvent<HTMLDivElement>, item: SessionDto) => {
    event.preventDefault();
    event.stopPropagation();
    if (!selectedIds.has(item.id)) {
      setSelectedIds(new Set([item.id]));
    }
    setSelectedId(item.id);
    anchorIdRef.current = item.id;
    // 每次打开右键菜单重置为全部收起，不保留上次展开状态
    setCollapsedContextMajors(new Set(subjectDefinitions.map((major) => major.name)));
    // 菜单尺寸 240x295（与 CSS 一致），靠近视口右/下边缘时向内夹紧，避免被截断
    const menuSize = 295;
    const edgeGap = 8;
    const x = Math.max(edgeGap, Math.min(event.clientX, window.innerWidth - menuSize - edgeGap));
    const y = Math.max(edgeGap, Math.min(event.clientY, window.innerHeight - menuSize - edgeGap));
    setContextMenu({ x, y });
  };

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>使用明细</h2>
          <div className="page-subtitle">拖动框选 · Shift 起止多选 · Ctrl 加选 · Ctrl+A 全选当前页</div>
        </div>
        <div className="header-actions session-search">
          <button className={`toolbar-button${allHistory ? ' active' : ''}`} onClick={() => { setAllHistory((value) => !value); setSkip(0); }}>
            {allHistory ? '全历史' : '当前日期'}
          </button>
          <div className="search-shell">
            <span className="search-symbol">⌕</span>
            <input
              className="search-input"
              value={keyword}
              placeholder="支持 分类:、标题:、进程:、& | ! ()"
              onChange={(event) => setKeyword(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') runSearch();
              }}
            />
            {keyword && <button className="search-clear" onClick={() => setKeyword('')} aria-label="清除搜索">×</button>}
          </div>
          <button className="toolbar-button active" onClick={runSearch}>搜索</button>
          <SearchModeMenu value={mode} onChange={(value) => { setMode(value); setSkip(0); }} />
          <DatePickerPopover value={selectedDate} align="right" disabled={allHistory} onChange={(value) => { setSelectedDate(value); setSkip(0); }} />
        </div>
      </div>

      {actionMessage && <div className="panel session-bulk-toolbar">
        <strong>{actionMessage}</strong>
        {canUndoAction && <button className="toolbar-button active" disabled={actionBusy} onClick={async () => { setActionBusy(true); try { await api.subjectCommand('undo'); setCanUndoAction(false); setActionMessage('已撤销删除'); } finally { setActionBusy(false); } }}>撤销删除</button>}
      </div>}

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
          <div
            className="session-table"
            ref={tableRef}
            onPointerDown={handleTablePointerDown}
            onPointerMove={handleTablePointerMove}
            onPointerUp={endTablePointer}
            onPointerCancel={endTablePointer}
          >
            {selectionBox && <div className="session-selection-box" style={{ left: selectionBox.x, top: selectionBox.y, width: selectionBox.width, height: selectionBox.height }} />}
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
              const seconds = item.durationSeconds ?? (end ? (end.getTime() - start.getTime()) / 1000 : 0);
              return (
                <div
                  className={`session-row${selectedIds.has(item.id) ? ' active' : ''}`}
                  key={item.id}
                  data-session-id={item.id}
                  ref={(element) => {
                    if (element) rowRefs.current.set(item.id, element);
                    else rowRefs.current.delete(item.id);
                  }}
                  onContextMenu={(event) => handleRowContextMenu(event, item)}
                  onClick={(event) => {
                    if (suppressClickRef.current) {
                      suppressClickRef.current = false;
                      return;
                    }
                    toggleRow(item.id, event);
                  }}
                >
                  <span className="session-title ellipsis" title={item.windowTitle}>
                    <span className="session-title-dot" />
                    {item.windowTitle || '未命名窗口'}
                  </span>
                  <span className="session-process ellipsis" title={item.processName}>{item.processName}</span>
                  <span className="session-start">{start.toLocaleString('zh-CN', { hour12: false })}</span>
                  <span className={`session-duration${!end ? ' running' : ''}`}>
                    {!end && <span className="live-dot" />}
                    {end ? formatDurationShort(seconds) : `进行中 · ${formatDurationShort(seconds)}`}
                    {seconds > 3600 && <small>{formatHoursMinutes(seconds)}</small>}
                  </span>
                  <span className={`session-subject-text${item.manualSubject ? '' : ' muted'}`} title={item.manualSubject ?? '空分类'}>
                    {item.manualSubject ?? '空分类'}
                  </span>
                </div>
              );
            })}
            {!loading && items.length === 0 && <div className="loading loading-inline">没有匹配的记录</div>}
          </div>
        </div>
      </LoadingTransition>

      {contextMenu && <div className="session-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} onClick={(event) => event.stopPropagation()} onContextMenu={(event) => event.preventDefault()}>
        <button className="session-context-item danger-button" disabled={actionBusy} onClick={() => void deleteSelected()}>
          删除{selectedIdList.length > 1 ? `这 ${selectedIdList.length} 条记录` : '当前记录'}
        </button>
        <div className="session-context-divider" />
        <button className="session-context-item" disabled={actionBusy} onClick={() => void setSelectedSubject(null)}>设置为空分类</button>
        <div className="session-context-divider" />
        {subjectDefinitions.map((major) => {
          const collapsed = collapsedContextMajors.has(major.name);
          return <div className="session-context-group" key={major.name}>
            <div className="session-context-major-row">
              <button className="session-context-item session-context-major" disabled={actionBusy} onClick={() => void setSelectedSubject(major.name)}>直接归到{major.name}</button>
              <button className="session-context-collapse" onClick={() => setCollapsedContextMajors((current) => { const next = new Set(current); if (next.has(major.name)) next.delete(major.name); else next.add(major.name); return next; })} aria-label={`${collapsed ? '展开' : '折叠'}${major.name}分类`} aria-expanded={!collapsed}>{collapsed ? '＋' : '−'}</button>
            </div>
            {!collapsed && <>
              {(major.parents ?? []).map((parentItem) => <div className="session-context-subgroup" key={`${major.name}/${parentItem.name}`}>
                <button className="session-context-item level-1" disabled={actionBusy} onClick={() => void setSelectedSubject(parentItem.name)}>↳ {parentItem.name}</button>
                {(parentItem.children ?? []).map((child) => <button className="session-context-item level-2" key={`${major.name}/${parentItem.name}/${child}`} disabled={actionBusy} onClick={() => void setSelectedSubject(child)}>↳ {child}</button>)}
              </div>)}
              {(major.children ?? []).map((child) => <button className="session-context-item level-1" key={`${major.name}/${child}`} disabled={actionBusy} onClick={() => void setSelectedSubject(child)}>↳ {child}</button>)}
            </>}
          </div>;
        })}
      </div>}

      {selectedId && (() => {
        const selected = items.find((item) => item.id === selectedId);
        if (!selected) return null;
        return <div className="panel session-detail-panel"><div><strong>{selected.windowTitle || selected.processName}</strong><span className="toolbar-hint">{selected.processName} · {selected.manualSubject ?? '空分类'}</span></div><div className="toolbar-hint">{new Date(selected.startTime).toLocaleString('zh-CN', { hour12: false })} — {selected.endTime ? new Date(selected.endTime).toLocaleString('zh-CN', { hour12: false }) : '进行中'}</div>{selected.parallelActivities && selected.parallelActivities.length > 0 && <div className="toolbar-hint">并行活动：{selected.parallelActivities.map((activity) => `${activity.processName || activity.description} ${formatDurationShort(activity.observedSeconds)}`).join('、')}</div>}</div>;
      })()}

      <div className="pager">
        <button className="toolbar-button" disabled={pageIndex === 0} onClick={() => setSkip(Math.max(0, skip - PAGE_SIZE))}>← 上一页</button>
        <input className="pager-input" aria-label="跳转页码" inputMode="numeric" pattern="[0-9]*" value={pageInput} onChange={(event) => setPageInput(event.target.value.replace(/\D/g, ''))} onKeyDown={(event) => { if (event.key === 'Enter') jumpToPage(); }} onBlur={jumpToPage} />
        <span className="pager-total">/ {pageCount}</span>
        <button className="toolbar-button" disabled={pageIndex + 1 >= pageCount} onClick={() => setSkip(skip + PAGE_SIZE)}>下一页 →</button>
      </div>
    </div>
  );
}
