import { useEffect, useRef, useState, type KeyboardEvent } from 'react';

interface HotkeyInputProps {
  /** 当前显示的快捷键文本，如 "Ctrl + Alt + W"；未设置时传 null */
  value: string | null;
  disabled?: boolean;
  /** 捕获到有效组合键时回调：modifiers 为 Win32 修饰位（Alt=1 Ctrl=2 Shift=4 Win=8），key 为虚拟键码 */
  onCapture: (modifiers: number, key: number) => void;
}

const MOD_ALT = 0x0001;
const MOD_CONTROL = 0x0002;
const MOD_SHIFT = 0x0004;
const MOD_WIN = 0x0008;

/** event.code → Windows 虚拟键码（字母 A-Z、数字 0-9、F1-F12） */
function codeToVirtualKey(code: string): number | null {
  if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3);
  if (/^Digit[0-9]$/.test(code)) return code.charCodeAt(5);
  const fKeyMatch = /^F(\d{1,2})$/.exec(code);
  if (fKeyMatch) {
    const index = Number(fKeyMatch[1]);
    if (index >= 1 && index <= 12) return 0x70 + index - 1; // VK_F1 = 112
  }
  return null;
}

function formatGesture(modifiers: number, key: number): string {
  const parts: string[] = [];
  if (modifiers & MOD_CONTROL) parts.push('Ctrl');
  if (modifiers & MOD_ALT) parts.push('Alt');
  if (modifiers & MOD_SHIFT) parts.push('Shift');
  if (modifiers & MOD_WIN) parts.push('Win');
  let keyName = '';
  if (key >= 65 && key <= 90) keyName = String.fromCharCode(key);
  else if (key >= 48 && key <= 57) keyName = String.fromCharCode(key);
  else if (key >= 112 && key <= 123) keyName = `F${key - 111}`;
  parts.push(keyName);
  return parts.join(' + ');
}

/** 只读的快捷键捕获输入框：聚焦后按下「修饰键 + 普通键」即完成录入。 */
export function HotkeyInput({ value, disabled, onCapture }: HotkeyInputProps) {
  const [capturing, setCapturing] = useState(false);
  const [pendingGesture, setPendingGesture] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!capturing) return;
    const onWindowMouseDown = (event: MouseEvent) => {
      if (inputRef.current && !inputRef.current.contains(event.target as Node)) {
        setCapturing(false);
        setPendingGesture(null);
      }
    };
    window.addEventListener('mousedown', onWindowMouseDown);
    return () => window.removeEventListener('mousedown', onWindowMouseDown);
  }, [capturing]);

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    event.preventDefault();
    event.stopPropagation();
    // 纯修饰键不完成录入，仅提示继续按普通键
    if (['Control', 'Alt', 'Shift', 'Meta'].includes(event.key)) {
      setPendingGesture(null);
      return;
    }
    const modifiers =
      (event.ctrlKey ? MOD_CONTROL : 0)
      | (event.altKey ? MOD_ALT : 0)
      | (event.shiftKey ? MOD_SHIFT : 0)
      | (event.metaKey ? MOD_WIN : 0);
    const key = codeToVirtualKey(event.code);
    // 无修饰键或不支持的普通键：忽略
    if (modifiers === 0 || key === null) {
      setPendingGesture(null);
      return;
    }
    const gesture = formatGesture(modifiers, key);
    setPendingGesture(gesture);
    setCapturing(false);
    onCapture(modifiers, key);
  };

  const display = capturing
    ? (pendingGesture ?? '请按下快捷键（修饰键 + 按键）')
    : (value ?? '未设置');

  return (
    <input
      ref={inputRef}
      className={`hotkey-input${capturing ? ' capturing' : ''}`}
      readOnly
      disabled={disabled}
      value={display}
      onFocus={() => setCapturing(true)}
      onBlur={() => { setCapturing(false); setPendingGesture(null); }}
      onKeyDown={handleKeyDown}
    />
  );
}
