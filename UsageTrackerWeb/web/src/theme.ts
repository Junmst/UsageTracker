import { useCallback, useEffect, useMemo, useState } from 'react';

export type ThemeMode = 'dark' | 'light' | 'system';

export interface ThemeColors {
  windowBg: string;
  panel: string;
  panelAlt: string;
  border: string;
  textPrimary: string;
  textSecondary: string;
  accent: string;
  accentContrast: string;
  accentStrong: string;
  accentSoft: string;
  categoryCard: string;
  isDark: boolean;
}

// 桌面版默认色板（来自 settings.json 的 ThemeAccentSlots / ThemeAccentRecentColors）
export const FALLBACK_ACCENTS = [
  '#C62828', '#D32F2F', '#E53935', '#F4511E', '#FB8C00', '#F9A825',
  '#43A047', '#2E7D32', '#00A884', '#00897B', '#00838F', '#039BE5',
  '#1E88E5', '#3949AB', '#5E35B1', '#8E24AA', '#D81B60', '#EC407A',
  '#6D4C41', '#546E7A', '#FF6EA8', '#5FD3BD', '#A97FE8', '#3A8BFF',
  '#FFC7E9', '#C1D9FE', '#00DEAB', '#006AC1', '#2200E5', '#43B0FF',
  '#001E5A', '#C6FF13', '#FFD649',
];

const STORAGE_ACCENT = 'shiji.accent';
const STORAGE_MODE = 'shiji.mode';
const STORAGE_PANEL_OPACITY = 'shiji.panel-opacity';

function normalizeAccent(value: string | null | undefined): string | null {
  if (!value) return null;
  let hex = value.trim();
  if (hex.startsWith('#')) hex = hex.slice(1);
  // 兼容 WPF 的 #AARRGGBB
  if (hex.length === 8) hex = hex.slice(2);
  if (hex.length === 3) {
    hex = hex
      .split('')
      .map((c) => c + c)
      .join('');
  }
  if (!/^[0-9a-fA-F]{6}$/.test(hex)) return null;
  return `#${hex.toUpperCase()}`;
}

function parseRgb(hex: string) {
  const h = hex.startsWith('#') ? hex.slice(1) : hex;
  const v = parseInt(h, 16);
  return {
    r: (v >> 16) & 255,
    g: (v >> 8) & 255,
    b: v & 255,
  };
}

function mix(hex: string, target: string, ratio: number): string {
  const a = parseRgb(hex);
  const b = parseRgb(target);
  const r = Math.round(a.r + (b.r - a.r) * ratio);
  const g = Math.round(a.g + (b.g - a.g) * ratio);
  const bl = Math.round(a.b + (b.b - a.b) * ratio);
  return `#${((r << 16) | (g << 8) | bl).toString(16).padStart(6, '0').toUpperCase()}`;
}

function alpha(hex: string, opacity: number): string {
  const { r, g, b } = parseRgb(hex);
  return `rgba(${r}, ${g}, ${b}, ${opacity})`;
}

function getAccentContrast(hex: string): string {
  const { r, g, b } = parseRgb(hex);
  const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
  return luminance > 0.62 ? '#2C2F3A' : '#FFFFFF';
}

function getAccentStrong(hex: string): string {
  const { r, g, b } = parseRgb(hex);
  const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
  return luminance > 0.62 ? mix(hex, '#000000', 0.34) : hex;
}

export function buildTheme(mode: ThemeMode, accent: string): ThemeColors {
  const isDark = mode === 'dark' || (mode === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  if (isDark) {
    return {
      isDark,
      windowBg: 'rgba(0, 0, 0, 0)',
      panel: 'rgba(0, 0, 0, 0.92)',
      panelAlt: 'rgba(0, 0, 0, 0.78)',
      border: 'rgba(255, 255, 255, 0.2)',
      textPrimary: '#FFFFFF',
      textSecondary: '#C7C7C7',
      accent,
      accentContrast: getAccentContrast(accent),
      accentStrong: getAccentStrong(accent),
      accentSoft: alpha(accent, 0.24),
      categoryCard: 'rgba(0, 0, 0, 0.86)',
    };
  }

  // 浅色模式：保留玻璃质感，同时让背景有更明显的层次和色彩深度
  const base = mix(accent, '#FFFFFF', 0.86);
  return {
    isDark,
    windowBg: 'rgba(255, 255, 255, 0)',
    panel: 'rgba(255, 255, 255, 0.72)',
    panelAlt: 'rgba(255, 255, 255, 0.48)',
    border: alpha(mix(accent, '#FFFFFF', 0.55), 0.38),
    textPrimary: '#2C2F3A',
    textSecondary: '#6B6F7E',
    accent,
    accentContrast: getAccentContrast(accent),
    accentStrong: getAccentStrong(accent),
    accentSoft: mix(accent, '#FFFFFF', 0.72),
    categoryCard: alpha(mix(accent, '#FFFFFF', 0.88), 0.42),
  };
}

export interface ThemeController {
  colors: ThemeColors;
  accent: string;
  mode: ThemeMode;
  setAccent: (value: string) => void;
  setMode: (value: ThemeMode) => void;
  panelOpacity: number;
  setPanelOpacity: (value: number) => void;
  palette: string[];
}

export function useTheme(serverAccent?: string | null): ThemeController {
  const [accent, setAccentState] = useState<string>(() => {
    const stored = normalizeAccent(localStorage.getItem(STORAGE_ACCENT));
    return stored ?? normalizeAccent(serverAccent) ?? FALLBACK_ACCENTS[0];
  });
  const [mode, setModeState] = useState<ThemeMode>(() => {
    const stored = localStorage.getItem(STORAGE_MODE);
    return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
  });
  const [panelOpacity, setPanelOpacityState] = useState(() => {
    const stored = Number(localStorage.getItem(STORAGE_PANEL_OPACITY));
    return Number.isFinite(stored) ? Math.max(0, Math.min(1, stored)) : 0.82;
  });

  // 桌面版设置里的主题色作为默认值生效（用户未在网页端手动改过时才跟随）
  useEffect(() => {
    if (localStorage.getItem(STORAGE_ACCENT)) return;
    const normalized = normalizeAccent(serverAccent);
    if (normalized) setAccentState(normalized);
  }, [serverAccent]);

  const setAccent = useCallback((value: string) => {
    const normalized = normalizeAccent(value);
    if (!normalized) return;
    setAccentState(normalized);
    localStorage.setItem(STORAGE_ACCENT, normalized);
  }, []);

  const setMode = useCallback((value: ThemeMode) => {
    setModeState(value);
    localStorage.setItem(STORAGE_MODE, value);
  }, []);

  const setPanelOpacity = useCallback((value: number) => {
    const next = Math.max(0, Math.min(1, value));
    setPanelOpacityState(next);
    localStorage.setItem(STORAGE_PANEL_OPACITY, String(next));
  }, []);

  const [systemThemeVersion, setSystemThemeVersion] = useState(0);

  useEffect(() => {
    if (mode !== 'system') return;
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const onChange = () => setSystemThemeVersion((version) => version + 1);
    media.addEventListener?.('change', onChange);
    return () => media.removeEventListener?.('change', onChange);
  }, [mode]);

  const colors = useMemo(() => {
    const next = buildTheme(mode, accent);
    next.panel = next.panel.replace(/,\s*([\d.]+)\)$/, `, ${panelOpacity})`);
    next.panelAlt = next.panelAlt.replace(/,\s*([\d.]+)\)$/, `, ${Math.max(0, panelOpacity - 0.26)})`);
    next.categoryCard = next.categoryCard.replace(/,\s*([\d.]+)\)$/, `, ${Math.max(0, panelOpacity - 0.4)})`);
    return next;
  }, [mode, accent, panelOpacity, systemThemeVersion]);

  useEffect(() => {
    const root = document.documentElement;
    root.style.setProperty('--window-bg', colors.windowBg);
    root.style.setProperty('--panel', colors.panel);
    root.style.setProperty('--panel-alt', colors.panelAlt);
    root.style.setProperty('--border', colors.border);
    root.style.setProperty('--text-primary', colors.textPrimary);
    root.style.setProperty('--text-secondary', colors.textSecondary);
    root.style.setProperty('--accent', colors.accent);
    root.style.setProperty('--accent-contrast', colors.accentContrast);
    root.style.setProperty('--accent-strong', colors.accentStrong);
    root.style.setProperty('--accent-soft', colors.accentSoft);
    root.style.setProperty('--category-card', colors.categoryCard);
    root.style.setProperty('--panel-opacity', String(panelOpacity));
    root.dataset.panelTransparent = panelOpacity === 0 ? 'true' : 'false';
    root.style.setProperty('--radius', '16px');
    root.style.setProperty('--radius-lg', '24px');
    root.style.setProperty(
      '--shadow',
      colors.isDark ? '0 8px 24px rgba(0,0,0,0.35)' : '0 18px 44px rgba(40,40,60,0.10)'
    );
    root.style.setProperty(
      '--shadow-lg',
      colors.isDark ? '0 18px 50px rgba(0,0,0,0.45)' : '0 28px 70px rgba(40,40,60,0.14)'
    );
    root.style.setProperty('color-scheme', colors.isDark ? 'dark' : 'light');
    document.body.style.background = 'transparent';
    document.body.style.color = colors.textPrimary;
  }, [colors]);

  return { colors, accent, mode, setAccent, setMode, panelOpacity, setPanelOpacity, palette: FALLBACK_ACCENTS };
}
