import { useEffect, useRef, useState } from 'react';

export interface NavItem {
  id: string;
  title: string;
  group: string;
  icon: string;
}

// 镜像桌面版 ModuleRegistry：分组、图标、顺序一致
export const NAV_ITEMS: NavItem[] = [
  { id: 'overview', title: '总览', group: 'WORKSPACE', icon: '⌁' },
  { id: 'sessions', title: '使用明细', group: 'WORKSPACE', icon: '☷' },
  { id: 'distribution', title: '时长分布', group: 'WORKSPACE', icon: '◴' },
  { id: 'process', title: '进程统计', group: 'WORKSPACE', icon: '▥' },
  { id: 'subject', title: '分类统计', group: 'WORKSPACE', icon: '◆' },
  { id: 'subjectManagement', title: '分类管理', group: 'TOOLS', icon: '✦' },
  { id: 'settings', title: '设置', group: 'TOOLS', icon: '◷' },
];

interface Props {
  active: string;
  onSelect: (id: string) => void;
}

export default function Sidebar({ active, onSelect }: Props) {
  const groups = ['WORKSPACE', 'TOOLS'];
  const [expanded, setExpanded] = useState(true);
  const [pinned, setPinned] = useState(false);
  const collapseTimerRef = useRef<number | null>(null);
  const expandTimerRef = useRef<number | null>(null);

  const clearExpandTimer = () => {
    if (expandTimerRef.current !== null) {
      window.clearTimeout(expandTimerRef.current);
      expandTimerRef.current = null;
    }
  };

  const clearCollapseTimer = () => {
    if (collapseTimerRef.current !== null) {
      window.clearTimeout(collapseTimerRef.current);
      collapseTimerRef.current = null;
    }
  };

  const scheduleCollapse = () => {
    clearCollapseTimer();
    if (pinned) return;
    collapseTimerRef.current = window.setTimeout(() => {
      setExpanded(false);
      collapseTimerRef.current = null;
    }, 180);
  };

  const scheduleExpand = () => {
    clearCollapseTimer();
    clearExpandTimer();
    if (expanded || pinned) return;
    expandTimerRef.current = window.setTimeout(() => {
      setExpanded(true);
      expandTimerRef.current = null;
    }, 180);
  };

  useEffect(() => () => {
    clearCollapseTimer();
    clearExpandTimer();
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !pinned) {
        clearCollapseTimer();
        setExpanded(false);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [pinned]);

  return (
    <div
      className={`sidebar-hot-zone${expanded ? ' expanded' : ''}`}
      onPointerEnter={scheduleExpand}
      onPointerLeave={scheduleCollapse}
    >
      <aside className={`sidebar${expanded ? ' expanded' : ' collapsed'}`}>
        <div className="sidebar-brand">
          <span className="sidebar-brand-mark">◴</span>
          <span className="sidebar-brand-text">时迹</span>
          <button
            type="button"
            className={`sidebar-toggle${pinned ? ' pinned' : ''}`}
            aria-label={pinned ? '松开导航栏' : '固定导航栏'}
            aria-pressed={pinned}
            aria-expanded={expanded}
            onClick={() => {
              clearCollapseTimer();
              clearExpandTimer();
              setPinned((current) => {
                const next = !current;
                if (next) setExpanded(true);
                return next;
              });
            }}
          >
            <span aria-hidden="true">{pinned ? '●' : '○'}</span>
          </button>
        </div>

        {groups.map((group) => (
          <div className="nav-group" key={group}>
            <div className="nav-group-title">{group}</div>
            {NAV_ITEMS.filter((item) => item.group === group).map((item) => (
              <button
                key={item.id}
                className={`nav-item${active === item.id ? ' active' : ''}`}
                aria-current={active === item.id ? 'page' : undefined}
                title={!expanded ? item.title : undefined}
                onClick={() => onSelect(item.id)}
              >
                <span className="nav-icon">{item.icon}</span>
                <span className="nav-title">{item.title}</span>
              </button>
            ))}
          </div>
        ))}
      </aside>
    </div>
  );
}
