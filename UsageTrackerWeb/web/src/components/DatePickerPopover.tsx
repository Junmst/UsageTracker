import { useEffect, useMemo, useRef, useState } from 'react';
import { formatDateKey, formatDateLabel, parseDateKey } from '../lib/format';

interface Props {
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  align?: 'left' | 'right';
}

const WEEKDAYS = ['一', '二', '三', '四', '五', '六', '日'];

function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function shiftMonth(date: Date, offset: number): Date {
  return new Date(date.getFullYear(), date.getMonth() + offset, 1);
}

export default function DatePickerPopover({ value, onChange, disabled = false, align = 'left' }: Props) {
  const [mounted, setMounted] = useState(false);
  const [open, setOpen] = useState(false);
  const [visibleMonth, setVisibleMonth] = useState(() => startOfMonth(parseDateKey(value)));
  const closeTimerRef = useRef<number | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  const close = (restoreFocus = false) => {
    setOpen(false);
    if (closeTimerRef.current !== null) window.clearTimeout(closeTimerRef.current);
    closeTimerRef.current = window.setTimeout(() => {
      setMounted(false);
      closeTimerRef.current = null;
      if (restoreFocus) triggerRef.current?.focus();
    }, 190);
  };

  const cancelClose = () => {
    if (closeTimerRef.current !== null) {
      window.clearTimeout(closeTimerRef.current);
      closeTimerRef.current = null;
    }
  };

  const openPicker = () => {
    if (disabled) return;
    cancelClose();
    setVisibleMonth(startOfMonth(parseDateKey(value)));
    setMounted(true);
    // 先以收起态挂载并绘制一帧，下一帧再切到展开态，打开时才有与收起一致的过渡动画。
    requestAnimationFrame(() => {
      requestAnimationFrame(() => setOpen(true));
    });
  };

  useEffect(() => () => {
    if (closeTimerRef.current !== null) window.clearTimeout(closeTimerRef.current);
  }, []);

  useEffect(() => {
    const onClosePopovers = () => {
      if (open) close();
    };
    window.addEventListener('shiji:close-popovers', onClosePopovers);
    return () => window.removeEventListener('shiji:close-popovers', onClosePopovers);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(event.target as Node)) close(true);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') close(true);
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  const days = useMemo(() => {
    const monthStart = startOfMonth(visibleMonth);
    const firstDay = (monthStart.getDay() + 6) % 7;
    return Array.from({ length: 42 }, (_, index) => {
      const date = new Date(monthStart);
      date.setDate(index - firstDay + 1);
      return date;
    });
  }, [visibleMonth]);

  const selectDate = (date: Date) => {
    onChange(formatDateKey(date));
    close();
  };

  const today = formatDateKey(new Date());
  const selectedDate = parseDateKey(value);
  const monthLabel = `${visibleMonth.getFullYear()}年${visibleMonth.getMonth() + 1}月`;

  return (
    <div
      className={`date-picker${align === 'right' ? ' date-picker-align-right' : ''}${mounted ? ' date-picker-mounted' : ''}${open ? ' date-picker-open' : ''}`}
      ref={rootRef}
      onPointerEnter={cancelClose}
      onPointerLeave={() => {
        if (!open) return;
        cancelClose();
        closeTimerRef.current = window.setTimeout(() => close(), 140);
      }}
    >
      <button
        type="button"
        ref={triggerRef}
        className="date-picker-trigger"
        disabled={disabled}
        aria-haspopup="dialog"
        aria-controls="date-picker-dialog"
        aria-expanded={open}
        onClick={() => (open ? close() : openPicker())}
      >
        <span>{value.replace(/-/g, '/')}</span>
        <span className="date-picker-icon" aria-hidden="true">▣</span>
      </button>
      {mounted && (
        <div id="date-picker-dialog" className="date-picker-popover" role="dialog" aria-label="选择日期">
          <div className="date-picker-header">
            <button type="button" className="date-picker-nav" onClick={() => setVisibleMonth((current) => shiftMonth(current, -1))} aria-label="上个月">‹</button>
            <strong>{monthLabel}</strong>
            <button type="button" className="date-picker-nav" onClick={() => setVisibleMonth((current) => shiftMonth(current, 1))} aria-label="下个月">›</button>
          </div>
          <div className="date-picker-weekdays">
            {WEEKDAYS.map((day) => <span key={day}>{day}</span>)}
          </div>
          <div className="date-picker-grid">
            {days.map((date) => {
              const key = formatDateKey(date);
              const inMonth = date.getMonth() === visibleMonth.getMonth();
              const selected = key === value;
              return (
                <button
                  type="button"
                  key={key}
                  className={`date-picker-day${inMonth ? '' : ' outside'}${selected ? ' selected' : ''}${key === today ? ' today' : ''}`}
                  onClick={() => selectDate(date)}
                >
                  {date.getDate()}
                </button>
              );
            })}
          </div>
          <div className="date-picker-footer">
            <span>{formatDateLabel(selectedDate)}</span>
            <button type="button" onClick={() => selectDate(new Date())}>今天</button>
          </div>
        </div>
      )}
    </div>
  );
}
