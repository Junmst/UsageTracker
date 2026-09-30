import { useEffect, useRef, useState } from 'react';
import type { SubjectDefinition } from '../lib/api';

interface Props {
  subjects: SubjectDefinition[];
  value: string | null;
  onChange: (value: string | null) => void;
  emptyLabel?: string;
}

export default function SubjectFilterMenu({ subjects, value, onChange, emptyLabel = '全部分类' }: Props) {
  const [collapsedMajors, setCollapsedMajors] = useState<Set<string>>(new Set());
  const [collapsedParents, setCollapsedParents] = useState<Set<string>>(new Set());
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
  const collapseDefaultsInitializedRef = useRef(false);
  const menuCloseTimerRef = useRef<number | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);

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
    menuCloseTimerRef.current = window.setTimeout(closeMenu, 180);
  };

  useEffect(() => () => {
    if (menuCloseTimerRef.current !== null) window.clearTimeout(menuCloseTimerRef.current);
  }, []);

  useEffect(() => {
    if (!menuOpen) return;
    const onPointerDown = (event: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) closeMenu();
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [menuOpen]);

  useEffect(() => {
    if (collapseDefaultsInitializedRef.current || subjects.length === 0) return;
    setCollapsedMajors(new Set(subjects.map((major) => major.name)));
    setCollapsedParents(new Set(
      subjects.flatMap((major) => (major.parents ?? []).map((parent) => `${major.name}/${parent.name}`))
    ));
    collapseDefaultsInitializedRef.current = true;
  }, [subjects]);

  return (
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
        className={`toolbar-button${value ? ' active' : ''}`}
        aria-haspopup="menu"
        aria-expanded={menuOpen}
        onClick={() => (menuOpen ? closeMenu() : openMenu())}
      >
        {value ?? emptyLabel} ▾
      </button>
      {menuMounted && (
        <div className="subject-filter-menu" role="menu">
          <button
            className={`subject-filter-item${value === null ? ' active' : ''}`}
            onClick={() => {
              onChange(null);
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
                    className={`subject-filter-item major-item${value === major.name ? ' active' : ''}`}
                    onClick={() => {
                      onChange(major.name);
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
                      onClick={() => setCollapsedMajors((current) => {
                        const next = new Set(current);
                        if (next.has(majorKey)) next.delete(majorKey);
                        else next.add(majorKey);
                        return next;
                      })}
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
                              className={`subject-filter-item level-1${value === parent.name ? ' active' : ''}`}
                              onClick={() => {
                                onChange(parent.name);
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
                                onClick={() => setCollapsedParents((current) => {
                                  const next = new Set(current);
                                  if (next.has(parentKey)) next.delete(parentKey);
                                  else next.add(parentKey);
                                  return next;
                                })}
                              >
                                {isCollapsed ? '＋' : '−'}
                              </button>
                            )}
                          </div>
                          {!isCollapsed && children.map((child) => (
                            <button
                              key={child}
                              className={`subject-filter-item level-2${value === child ? ' active' : ''}`}
                              onClick={() => {
                                onChange(child);
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
                        className={`subject-filter-item level-1 direct-child${value === child ? ' active' : ''}`}
                        onClick={() => {
                          onChange(child);
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
  );
}
