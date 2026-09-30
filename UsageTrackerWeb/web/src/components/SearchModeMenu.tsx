import { useEffect, useRef, useState } from 'react';

interface Props {
  value: string;
  onChange: (mode: string) => void;
}

const MODES: Array<{ value: string; label: string }> = [
  { value: 'all', label: '全盘查找' },
  { value: 'subject', label: '分类查找' },
  { value: 'title', label: '标题查找' },
  { value: 'process', label: '进程查找' },
];

export default function SearchModeMenu({ value, onChange }: Props) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuMounted, setMenuMounted] = useState(false);
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

  const current = MODES.find((item) => item.value === value) ?? MODES[0];

  return (
    <div
      className={`search-mode-menu${menuMounted ? ' menu-mounted' : ''}${menuOpen ? ' menu-open' : ''}`}
      ref={menuRef}
      onPointerEnter={() => {
        if (menuMounted) openMenu();
      }}
      onPointerLeave={() => {
        if (menuOpen) scheduleMenuClose();
      }}
    >
      <button
        className={`toolbar-button${value !== 'all' ? ' active' : ''}`}
        aria-haspopup="menu"
        aria-expanded={menuOpen}
        onClick={() => (menuOpen ? closeMenu() : openMenu())}
      >
        {current.label} ▾
      </button>
      {menuMounted && (
        <div className="search-mode-dropdown" role="menu">
          {MODES.map((item) => (
            <button
              key={item.value}
              className={`search-mode-item${value === item.value ? ' active' : ''}`}
              role="menuitem"
              onClick={() => {
                onChange(item.value);
                closeMenu();
              }}
            >
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
