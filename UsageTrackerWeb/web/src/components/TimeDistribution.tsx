import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { SessionDto } from '../lib/api';
import {
  formatClock,
  formatDurationShort,
  getChineseWeekday,
  getDayEnd,
  getDayStart,
  parseDateKey,
} from '../lib/format';

// ─── 镜像 TimeDistributionControl 常量 ───
const HEADER_HEIGHT = 36;
const DATE_COLUMN_WIDTH = 142;
const ROW_HEIGHT = 58;
const BOTTOM_PADDING = 10;
const TOTAL_MINUTES = 1440;
const MIN_ZOOM = 0.96;
const DEFAULT_ZOOM = 1.27;
const DEFAULT_START_MINUTES = 150;
const MAX_ZOOM = 15;
const MAX_CACHE_PIXELS = 40_000_000;

export interface DistributionTheme {
  panel: string;
  panelAlt: string;
  windowBg: string;
  border: string;
  textSecondary: string;
  accent: string;
  accentSoft: string;
  textPrimary: string;
}

interface PreparedSession {
  processName: string;
  windowTitle: string;
  subject: string | null;
  start: number;
  end: number | null;
}

interface Segment {
  session: PreparedSession;
  startMin: number;
  endMin: number;
}

interface ViewState {
  zoom: number;
  offsetX: number;
  offsetY: number;
}

function getTimeScaleStep(zoom: number): number {
  if (zoom >= 10) return 10;
  if (zoom >= 6) return 15;
  if (zoom >= 4) return 30;
  if (zoom >= 3) return 60;
  if (zoom >= 1.2) return 120;
  return 240;
}

function clamp(value: number, min: number, max: number): number {
  return value < min ? min : value > max ? max : value;
}

function roundRect(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  width: number,
  height: number,
  radius: number
): void {
  const r = Math.min(radius, width / 2, height / 2);
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.lineTo(x + width - r, y);
  ctx.quadraticCurveTo(x + width, y, x + width, y + r);
  ctx.lineTo(x + width, y + height - r);
  ctx.quadraticCurveTo(x + width, y + height, x + width - r, y + height);
  ctx.lineTo(x + r, y + height);
  ctx.quadraticCurveTo(x, y + height, x, y + height - r);
  ctx.lineTo(x, y + r);
  ctx.quadraticCurveTo(x, y, x + r, y);
  ctx.closePath();
}

function fillGradientBar(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  width: number,
  height: number,
  accent: string,
  accentSoft: string
): void {
  const r = Math.min(6, width / 2, height / 2);
  const gradient = ctx.createLinearGradient(x, y, x + width, y + height);
  gradient.addColorStop(0, accent);
  gradient.addColorStop(0.55, accent);
  gradient.addColorStop(1, accentSoft);
  ctx.fillStyle = gradient;
  ctx.globalAlpha = 0.98;
  roundRect(ctx, x, y, width, height, r);
  ctx.fill();
  if (width >= 7) {
    ctx.globalAlpha = 0.42;
    ctx.strokeStyle = accentSoft;
    ctx.lineWidth = 1;
    roundRect(ctx, x + 0.5, y + 0.5, Math.max(1, width - 1), Math.max(1, height - 1), r);
    ctx.stroke();
  }
  ctx.globalAlpha = 1;
}

interface Props {
  active?: boolean;
  dates: string[];
  sessions: SessionDto[];
  height?: number | string;
  theme: DistributionTheme;
  onNeedOlder?: () => void;
}

export default function TimeDistribution({
  active = true,
  dates,
  sessions,
  height = 560,
  theme,
  onNeedOlder,
}: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const viewRef = useRef<ViewState>({ zoom: DEFAULT_ZOOM, offsetX: 0, offsetY: 0 });
  const defaultViewInitializedRef = useRef(false);
  const rafRef = useRef<number | null>(null);
  const draggingRef = useRef(false);
  const pointerRef = useRef({ x: 0, y: 0, offsetX: 0, offsetY: 0 });
  const wheelRef = useRef({ deltaX: 0, deltaY: 0, zoomDelta: 0, mouseX: 0, frame: null as number | null });
  const zoomingRef = useRef(false);
  const hoverRef = useRef<{ row: number; segment: Segment } | null>(null);
  const cacheRef = useRef<{
    canvas: HTMLCanvasElement;
    zoom: number;
    version: number;
  } | null>(null);
  const rebuildTimerRef = useRef<number | null>(null);
  const zoomAnimationRef = useRef<number | null>(null);

  const [zoomLabel, setZoomLabel] = useState(DEFAULT_ZOOM);
  const zoomLabelRef = useRef(DEFAULT_ZOOM);
  const [dragging, setDragging] = useState(false);
  const [tooltip, setTooltip] = useState<{
    x: number;
    y: number;
    title: string;
    process: string;
    subject: string | null;
    range: string;
    duration: string;
  } | null>(null);

  // 最新日期在最上面：后端返回升序，这里倒序
  const orderedDates = useMemo(() => [...dates].reverse().map(parseDateKey), [dates]);

  const prepared = useMemo<PreparedSession[]>(
    () =>
      sessions.map((item) => ({
        processName: item.processName,
        windowTitle: item.windowTitle,
        subject: item.manualSubject ?? null,
        start: new Date(item.startTime).getTime(),
        end: item.endTime ? new Date(item.endTime).getTime() : null,
      })),
    [sessions]
  );

  const dataVersion = useMemo(
    () => `${orderedDates.length}:${prepared.length}:${prepared[0]?.start ?? 0}`,
    [orderedDates.length, prepared]
  );
  const versionRef = useRef(dataVersion);
  if (versionRef.current !== dataVersion) {
    versionRef.current = dataVersion;
    cacheRef.current = null;
  }

  // 按行预分桶：渲染时只遍历可见行，避免 O(日期 × 会话) 全量扫描
  const rowSegments = useMemo(() => {
    const rows: Segment[][] = orderedDates.map(() => []);
    const now = Date.now();
    for (const session of prepared) {
      const end = session.end ?? now;
      for (let row = 0; row < orderedDates.length; row++) {
        const dayStart = getDayStart(orderedDates[row]).getTime();
        const dayEnd = getDayEnd(orderedDates[row]).getTime();
        if (end <= dayStart || session.start >= dayEnd) continue;
        const clippedStart = Math.max(session.start, dayStart);
        const clippedEnd = Math.min(end, dayEnd);
        if (clippedEnd <= clippedStart) continue;
        rows[row].push({
          session,
          startMin: (clippedStart - dayStart) / 60000,
          endMin: (clippedEnd - dayStart) / 60000,
        });
      }
    }
    return rows.map((segments) => {
      const merged: Segment[] = [];
      for (const segment of segments.sort((a, b) => a.startMin - b.startMin)) {
        const previous = merged[merged.length - 1];
        if (previous && segment.startMin <= previous.endMin + 0.35) {
          previous.endMin = Math.max(previous.endMin, segment.endMin);
          continue;
        }
        merged.push({ ...segment });
      }
      return merged;
    });
  }, [orderedDates, prepared]);

  const rowTotals = useMemo(() => {
    return rowSegments.map((segments) =>
      segments.reduce((sum, s) => sum + (s.endMin - s.startMin) * 60000, 0)
    );
  }, [rowSegments]);

  const getMetrics = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return null;
    const viewportWidth = Math.max(0, canvas.clientWidth - DATE_COLUMN_WIDTH);
    const viewportHeight = Math.max(0, canvas.clientHeight - HEADER_HEIGHT);
    if (viewportWidth <= 0 || viewportHeight <= 0) return null;
    return {
      canvas,
      viewportWidth,
      viewportHeight,
      baseWidth: viewportWidth,
      worldHeight: orderedDates.length * ROW_HEIGHT + BOTTOM_PADDING,
    };
  }, [orderedDates.length]);

  const clampView = useCallback(
    (view: ViewState): ViewState => {
      const metrics = getMetrics();
      if (!metrics) return view;
      const maxOffsetX = Math.max(0, metrics.viewportWidth * view.zoom - metrics.viewportWidth);
      const maxOffsetY = Math.max(0, metrics.worldHeight - metrics.viewportHeight);
      return {
        zoom: clamp(view.zoom, MIN_ZOOM, MAX_ZOOM),
        offsetX: clamp(view.offsetX, 0, maxOffsetX),
        offsetY: clamp(view.offsetY, 0, maxOffsetY),
      };
    },
    [getMetrics]
  );

  const getDefaultView = useCallback((): ViewState => {
    const metrics = getMetrics();
    if (!metrics) return { zoom: DEFAULT_ZOOM, offsetX: 0, offsetY: 0 };
    const maxOffsetX = Math.max(0, metrics.viewportWidth * DEFAULT_ZOOM - metrics.viewportWidth);
    const startOffsetX = (DEFAULT_START_MINUTES / TOTAL_MINUTES) * metrics.baseWidth;
    return {
      zoom: DEFAULT_ZOOM,
      offsetX: clamp(startOffsetX, 0, maxOffsetX),
      offsetY: 0,
    };
  }, [getMetrics]);

  // 世界空间内容绘制到离屏缓存（网格 + 会话条，不含高亮）
  const buildCache = useCallback(() => {
    const metrics = getMetrics();
    if (!metrics) return;
    const view = viewRef.current;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const worldWidth = Math.ceil(metrics.baseWidth * view.zoom);
    const worldHeight = Math.ceil(metrics.worldHeight);
    if (worldWidth * worldHeight > MAX_CACHE_PIXELS) {
      cacheRef.current = null;
      return;
    }

    const cache = cacheRef.current?.canvas ?? document.createElement('canvas');
    cache.width = Math.round(worldWidth * dpr);
    cache.height = Math.round(worldHeight * dpr);
    const ctx = cache.getContext('2d');
    if (!ctx) return;

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, worldWidth, worldHeight);

    const minuteStep = getTimeScaleStep(view.zoom);
    const scaledStep = (minuteStep / TOTAL_MINUTES) * metrics.baseWidth * view.zoom;

    // 交替行底色先绘制，网格线和会话条保持清晰可见。
    ctx.globalAlpha = 1;
    for (let row = 0; row < orderedDates.length; row++) {
      if (row % 2 === 1) {
        ctx.fillStyle = theme.panel;
        ctx.globalAlpha = 0.22;
        ctx.fillRect(0, row * ROW_HEIGHT, worldWidth, ROW_HEIGHT);
      }
    }

    // 垂直网格线：主刻度更清晰，背景保持轻盈。
    ctx.strokeStyle = theme.border;
    ctx.lineWidth = 1;
    ctx.globalAlpha = 0.62;
    ctx.beginPath();
    for (let x = 0; x <= worldWidth + 1; x += scaledStep) {
      ctx.moveTo(x, 0);
      ctx.lineTo(x, worldHeight);
    }
    ctx.stroke();

    // 水平网格线，帮助快速横向追踪日期。
    ctx.globalAlpha = 0.48;
    ctx.lineWidth = 0.7;
    ctx.beginPath();
    for (let row = 1; row <= orderedDates.length; row++) {
      const y = row * ROW_HEIGHT;
      ctx.moveTo(0, y);
      ctx.lineTo(worldWidth, y);
    }
    ctx.stroke();
    ctx.globalAlpha = 1;

    // 会话条
    const barHeight = Math.max(8, Math.min(ROW_HEIGHT - 10, 34));
    const scale = (minutes: number) => (minutes / TOTAL_MINUTES) * metrics.baseWidth * view.zoom;
    for (let row = 0; row < rowSegments.length; row++) {
      const y = row * ROW_HEIGHT;
      const barTop = y + (ROW_HEIGHT - barHeight) / 2;
      for (const segment of rowSegments[row]) {
        const x = scale(segment.startMin);
        const width = Math.max(2, scale(segment.endMin) - scale(segment.startMin) - 1);
        fillGradientBar(ctx, x + 0.5, barTop, width, barHeight, theme.accent, theme.accentSoft);
      }
    }

    cacheRef.current = { canvas: cache, zoom: view.zoom, version: 1 };
  }, [getMetrics, orderedDates.length, rowSegments, theme.accent, theme.accentSoft, theme.border]);

  const scheduleRebuild = useCallback(() => {
    if (rebuildTimerRef.current !== null) {
      window.clearTimeout(rebuildTimerRef.current);
    }
    rebuildTimerRef.current = window.setTimeout(() => {
      rebuildTimerRef.current = null;
      buildCache();
      renderRef.current?.();
    }, 140);
  }, [buildCache]);

  const render = useCallback(() => {
    const metrics = getMetrics();
    const canvas = metrics?.canvas;
    const ctx = canvas?.getContext('2d');
    if (!metrics || !canvas || !ctx) return;

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const width = canvas.clientWidth;
    const canvasHeight = canvas.clientHeight;
    const pixelWidth = Math.round(width * dpr);
    const pixelHeight = Math.round(canvasHeight * dpr);
    if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
      canvas.width = pixelWidth;
      canvas.height = pixelHeight;
    }

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, canvasHeight);

    const { viewportWidth, viewportHeight, baseWidth } = metrics;
    const view = viewRef.current;

    ctx.fillStyle = theme.panel;
    ctx.fillRect(0, 0, width, canvasHeight);
    ctx.fillStyle = theme.panel;
    ctx.fillRect(0, 0, width, HEADER_HEIGHT);
    ctx.fillStyle = theme.panel;
    ctx.globalAlpha = 0.96;
    ctx.fillRect(0, HEADER_HEIGHT, DATE_COLUMN_WIDTH, viewportHeight);
    ctx.globalAlpha = 1;

    ctx.strokeStyle = theme.border;
    ctx.lineWidth = 1;
    ctx.globalAlpha = 0.9;
    ctx.beginPath();
    ctx.moveTo(DATE_COLUMN_WIDTH - 0.5, 0);
    ctx.lineTo(DATE_COLUMN_WIDTH - 0.5, canvasHeight);
    ctx.moveTo(0, HEADER_HEIGHT - 0.5);
    ctx.lineTo(width, HEADER_HEIGHT - 0.5);
    ctx.stroke();
    ctx.globalAlpha = 1;

    // ── 图表区 ──
    ctx.save();
    ctx.beginPath();
    ctx.rect(DATE_COLUMN_WIDTH, HEADER_HEIGHT, viewportWidth, viewportHeight);
    ctx.clip();
    ctx.translate(DATE_COLUMN_WIDTH, HEADER_HEIGHT);

    const cache = cacheRef.current;
    const cacheMatchesZoom = cache && Math.abs(cache.zoom - view.zoom) < 0.0001;
    if (cache && cache.canvas.width > 0 && !zoomingRef.current && cacheMatchesZoom) {
      // 只有缓存比例与当前视图一致时才使用离屏缓存，避免缩放结束后的旧缓存重采样虚化。
      // 缓存内部坐标是已经乘过 zoom 的世界坐标，所以 sourceX 必须再乘 zoom
      const cacheDpr = cache.canvas.height / metrics.worldHeight;
      const cacheZoom = cache.zoom;
      const sourceX = view.offsetX * cacheZoom * cacheDpr;
      const sourceY = view.offsetY * cacheDpr;
      const sourceW = viewportWidth * (cacheZoom / view.zoom) * cacheDpr;
      const sourceH = viewportHeight * cacheDpr;
      ctx.drawImage(
        cache.canvas,
        sourceX,
        sourceY,
        sourceW,
        sourceH,
        0,
        0,
        viewportWidth,
        viewportHeight
      );
    } else {
      // 未命中：只画可见行，成本与视口大小成正比
      const minuteStep = getTimeScaleStep(view.zoom);
      ctx.strokeStyle = theme.border;
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (let minutesOffset = 0; minutesOffset <= TOTAL_MINUTES; minutesOffset += minuteStep) {
        const worldX = (minutesOffset / TOTAL_MINUTES) * baseWidth;
        const screenX = (worldX - view.offsetX) * view.zoom;
        if (screenX < -1 || screenX > viewportWidth + 1) continue;
        ctx.moveTo(screenX, 0);
        ctx.lineTo(screenX, viewportHeight);
      }
      ctx.stroke();

      ctx.save();
      ctx.globalAlpha = 0.4;
      ctx.lineWidth = 0.5;
      ctx.beginPath();
      for (let row = 1; row <= orderedDates.length; row++) {
        const screenY = row * ROW_HEIGHT - view.offsetY;
        if (screenY < -1 || screenY > viewportHeight + 1) continue;
        ctx.moveTo(0, screenY);
        ctx.lineTo(viewportWidth, screenY);
      }
      ctx.stroke();
      ctx.restore();

      const barHeight = Math.max(8, Math.min(ROW_HEIGHT - 10, 34));
      const firstRow = Math.max(0, Math.floor(view.offsetY / ROW_HEIGHT));
      const lastRow = Math.min(
        orderedDates.length - 1,
        Math.ceil((view.offsetY + viewportHeight) / ROW_HEIGHT)
      );
      for (let row = firstRow; row <= lastRow; row++) {
        const screenY = row * ROW_HEIGHT - view.offsetY;
        const barTop = screenY + (ROW_HEIGHT - barHeight) / 2;
        for (const segment of rowSegments[row] ?? []) {
          const worldX = (segment.startMin / TOTAL_MINUTES) * baseWidth;
          const screenX = (worldX - view.offsetX) * view.zoom;
          const screenWidth = ((segment.endMin - segment.startMin) / TOTAL_MINUTES) * baseWidth * view.zoom;
          if (screenX + screenWidth < 0 || screenX > viewportWidth) continue;
          fillGradientBar(ctx, screenX + 0.5, barTop, Math.max(2, screenWidth - 1), barHeight, theme.accent, theme.accentSoft);
        }
      }
    }

    // 悬停高亮单独叠加，不触碰缓存
    const hover = hoverRef.current;
    if (hover) {
      const screenY = hover.row * ROW_HEIGHT - view.offsetY;
      const barHeight = Math.max(8, Math.min(ROW_HEIGHT - 10, 34));
      const barTop = screenY + (ROW_HEIGHT - barHeight) / 2;
      const worldX = (hover.segment.startMin / TOTAL_MINUTES) * baseWidth;
      const screenX = (worldX - view.offsetX) * view.zoom;
      const screenWidth = ((hover.segment.endMin - hover.segment.startMin) / TOTAL_MINUTES) * baseWidth * view.zoom;
      fillGradientBar(ctx, screenX + 0.5, barTop, Math.max(2, screenWidth - 1), barHeight, theme.accent, theme.accentSoft);
      ctx.strokeStyle = theme.textPrimary;
      ctx.lineWidth = 1;
      ctx.stroke();
    }
    ctx.restore();

    // ── 表头 ──
    ctx.save();
    ctx.beginPath();
    ctx.rect(DATE_COLUMN_WIDTH, 0, viewportWidth, HEADER_HEIGHT);
    ctx.clip();
    ctx.translate(DATE_COLUMN_WIDTH, 0);
    const minuteStep = getTimeScaleStep(view.zoom);
    ctx.strokeStyle = theme.border;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let minutesOffset = 0; minutesOffset <= TOTAL_MINUTES; minutesOffset += minuteStep) {
      const worldX = (minutesOffset / TOTAL_MINUTES) * baseWidth;
      const screenX = (worldX - view.offsetX) * view.zoom;
      if (screenX < -1 || screenX > viewportWidth + 1) continue;
      ctx.moveTo(screenX, HEADER_HEIGHT - 6);
      ctx.lineTo(screenX, HEADER_HEIGHT);
    }
    ctx.stroke();
    ctx.fillStyle = theme.textSecondary;
    ctx.font = '11px "PingFang SC", "HarmonyOS Sans SC", "Microsoft YaHei", system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const rangeStart = getDayStart(new Date()).getTime();
    for (let minutesOffset = 0; minutesOffset <= TOTAL_MINUTES; minutesOffset += minuteStep) {
      const worldX = (minutesOffset / TOTAL_MINUTES) * baseWidth;
      const screenX = (worldX - view.offsetX) * view.zoom;
      if (screenX < -80 || screenX > viewportWidth + 80) continue;
      const time = new Date(rangeStart + minutesOffset * 60000);
      const text =
        minuteStep >= 60
          ? `${time.getHours().toString().padStart(2, '0')}:00`
          : formatClock(time);
      ctx.fillText(text, screenX, 8);
    }
    ctx.restore();

    // ── 日期列 ──
    ctx.save();
    ctx.beginPath();
    ctx.rect(0, HEADER_HEIGHT, DATE_COLUMN_WIDTH, viewportHeight);
    ctx.clip();
    ctx.translate(0, HEADER_HEIGHT);
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    const firstRow = Math.max(0, Math.floor(view.offsetY / ROW_HEIGHT) - 1);
    const lastRow = Math.min(
      orderedDates.length - 1,
      Math.ceil((view.offsetY + viewportHeight) / ROW_HEIGHT) + 1
    );
    for (let row = firstRow; row <= lastRow; row++) {
      const screenY = row * ROW_HEIGHT - view.offsetY;
      const date = orderedDates[row];
      const dateText = `${date.getMonth() + 1}月${date.getDate()}日`;
      const weekday = getChineseWeekday(date);
      ctx.fillStyle = theme.textPrimary;
      ctx.font = '600 13px "PingFang SC", "HarmonyOS Sans SC", "Microsoft YaHei", system-ui, sans-serif';
      ctx.globalAlpha = 0.96;
      ctx.fillText(dateText, 12, screenY + 8);
      ctx.fillStyle = theme.textSecondary;
      ctx.font = '11px "PingFang SC", "HarmonyOS Sans SC", "Microsoft YaHei", system-ui, sans-serif';
      ctx.globalAlpha = 0.78;
      ctx.fillText(weekday, 12, screenY + 27);
      ctx.fillStyle = theme.accent;
      ctx.font = '600 10px "PingFang SC", "HarmonyOS Sans SC", "Microsoft YaHei", system-ui, sans-serif';
      ctx.globalAlpha = 0.92;
      ctx.fillText(formatDurationShort(rowTotals[row] / 1000), 82, screenY + 27);
    }
    ctx.globalAlpha = 1;
    ctx.restore();

    // 滚到底部时通知外层加载更早日期
    if (onNeedOlder && view.offsetY >= Math.max(0, metrics.worldHeight - viewportHeight) - ROW_HEIGHT) {
      onNeedOlder();
    }
  }, [
    getMetrics,
    onNeedOlder,
    orderedDates,
    rowSegments,
    rowTotals,
    theme.accent,
    theme.accentSoft,
    theme.border,
    theme.panel,
    theme.panelAlt,
    theme.textPrimary,
    theme.textSecondary,
    theme.windowBg,
  ]);

  const renderRef = useRef(render);
  renderRef.current = render;

  const scheduleRender = useCallback(() => {
    if (!active || rafRef.current !== null) return;
    rafRef.current = requestAnimationFrame(() => {
      rafRef.current = null;
      renderRef.current();
    });
  }, [active]);

  useEffect(() => {
    if (!active) return;
    scheduleRender();
  }, [active, scheduleRender, rowSegments, orderedDates, theme]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const observer = new ResizeObserver(() => {
      if (!active) return;
      if (!defaultViewInitializedRef.current && canvas.clientWidth > DATE_COLUMN_WIDTH) {
        viewRef.current = getDefaultView();
        zoomLabelRef.current = DEFAULT_ZOOM;
        setZoomLabel(DEFAULT_ZOOM);
        defaultViewInitializedRef.current = true;
      }
      cacheRef.current = null;
      scheduleRender();
    });
    observer.observe(canvas);
    return () => observer.disconnect();
  }, [active, getDefaultView, scheduleRender]);

  useEffect(() => {
    if (!active) return;
    cacheRef.current = null;
    scheduleRender();
    scheduleRebuild();
  }, [active, rowSegments, scheduleRender, scheduleRebuild]);

  useEffect(() => {
    if (active) return;
    if (rafRef.current !== null) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
    if (rebuildTimerRef.current !== null) {
      window.clearTimeout(rebuildTimerRef.current);
      rebuildTimerRef.current = null;
    }
    if (wheelRef.current.frame !== null) {
      cancelAnimationFrame(wheelRef.current.frame);
      wheelRef.current.frame = null;
      wheelRef.current.deltaX = 0;
      wheelRef.current.deltaY = 0;
      wheelRef.current.zoomDelta = 0;
      zoomingRef.current = false;
    }
  }, [active]);

  useEffect(
    () => () => {
      if (rafRef.current !== null) cancelAnimationFrame(rafRef.current);
      if (rebuildTimerRef.current !== null) window.clearTimeout(rebuildTimerRef.current);
      if (wheelRef.current.frame !== null) cancelAnimationFrame(wheelRef.current.frame);
      if (zoomAnimationRef.current !== null) cancelAnimationFrame(zoomAnimationRef.current);
    },
    []
  );

  const hitTest = useCallback(
    (mouseX: number, mouseY: number) => {
      const metrics = getMetrics();
      if (!metrics) return null;
      const view = viewRef.current;
      const row = Math.floor((mouseY - HEADER_HEIGHT + view.offsetY) / ROW_HEIGHT);
      if (row < 0 || row >= rowSegments.length) return null;
      const worldX = view.offsetX + (mouseX - DATE_COLUMN_WIDTH) / view.zoom;
      const targetMinutes = (worldX / metrics.baseWidth) * TOTAL_MINUTES;
      for (const segment of rowSegments[row]) {
        if (targetMinutes >= segment.startMin && targetMinutes <= segment.endMin) {
          return { row, segment };
        }
      }
      return null;
    },
    [getMetrics, rowSegments]
  );

  const handlePointerDown = (event: React.PointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    canvas.setPointerCapture(event.pointerId);
    const rect = canvas.getBoundingClientRect();
    draggingRef.current = true;
    setDragging(true);
    pointerRef.current = {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top,
      offsetX: viewRef.current.offsetX,
      offsetY: viewRef.current.offsetY,
    };
  };

  const handlePointerMove = (event: React.PointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mouseX = event.clientX - rect.left;
    const mouseY = event.clientY - rect.top;

    if (draggingRef.current) {
      const dx = mouseX - pointerRef.current.x;
      const dy = mouseY - pointerRef.current.y;
      viewRef.current = clampView({
        zoom: viewRef.current.zoom,
        offsetX: pointerRef.current.offsetX - dx / viewRef.current.zoom,
        offsetY: pointerRef.current.offsetY - dy,
      });
      scheduleRender();
      return;
    }

    if (mouseX < DATE_COLUMN_WIDTH || mouseY < HEADER_HEIGHT) {
      if (hoverRef.current) {
        hoverRef.current = null;
        setTooltip(null);
        scheduleRender();
      }
      return;
    }

    const hit = hitTest(mouseX, mouseY);
    const previous = hoverRef.current;
    if (!hit) {
      if (previous) {
        hoverRef.current = null;
        setTooltip(null);
        scheduleRender();
      }
      return;
    }

    hoverRef.current = hit;
    const start = new Date(
      getDayStart(orderedDates[hit.row]).getTime() + hit.segment.startMin * 60000
    );
    const end = new Date(
      getDayStart(orderedDates[hit.row]).getTime() + hit.segment.endMin * 60000
    );
    setTooltip({
      x: mouseX,
      y: mouseY,
      title: hit.segment.session.windowTitle || hit.segment.session.processName,
      process: hit.segment.session.processName,
      subject: hit.segment.session.subject,
      range: `${formatClock(start)} – ${formatClock(end)}`,
      duration: formatDurationShort((hit.segment.endMin - hit.segment.startMin) * 60),
    });
    scheduleRender();
  };

  const endDrag = (event: React.PointerEvent<HTMLCanvasElement>) => {
    if (!draggingRef.current) return;
    draggingRef.current = false;
    setDragging(false);
    canvasRef.current?.releasePointerCapture(event.pointerId);
  };

  const handleWheel = useCallback(
    (event: WheelEvent) => {
      const canvas = canvasRef.current;
      if (!canvas) return;
      event.preventDefault();
      const rect = canvas.getBoundingClientRect();
      const wheel = wheelRef.current;
      wheel.mouseX = event.clientX - rect.left - DATE_COLUMN_WIDTH;

      const wheelScale = 0.26;
      if (event.ctrlKey || event.metaKey) {
        zoomingRef.current = true;
        wheel.zoomDelta += event.deltaY * wheelScale;
      } else if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
        wheel.deltaX += (event.shiftKey ? 0 : event.deltaX) * wheelScale;
        wheel.deltaY += (event.shiftKey ? event.deltaY : 0) * wheelScale;
      } else {
        wheel.deltaY += event.deltaY * wheelScale;
      }

      if (wheel.frame !== null) return;
      const animate = () => {
        wheel.frame = requestAnimationFrame(animate);
        const current = viewRef.current;
        const smoothing = 0.27;
        const pendingX = wheel.deltaX * smoothing;
        const pendingY = wheel.deltaY * smoothing;
        const pendingZoom = wheel.zoomDelta * smoothing;
        wheel.deltaX -= pendingX;
        wheel.deltaY -= pendingY;
        wheel.zoomDelta -= pendingZoom;

        const hasMovement = Math.abs(pendingX) > 0.02 || Math.abs(pendingY) > 0.02 || Math.abs(pendingZoom) > 0.02;
        if (!hasMovement) {
          cancelAnimationFrame(wheel.frame);
          wheel.frame = null;
          wheel.deltaX = 0;
          wheel.deltaY = 0;
          wheel.zoomDelta = 0;
          if (zoomingRef.current) {
            zoomingRef.current = false;
            scheduleRebuild();
            scheduleRender();
          }
          return;
        }

        const nextZoom = pendingZoom === 0
          ? current.zoom
          : clamp(current.zoom * Math.pow(1.0018, -pendingZoom), MIN_ZOOM, MAX_ZOOM);
        const zoomRatio = nextZoom / current.zoom;
        const worldXBefore = current.offsetX + wheel.mouseX / current.zoom;
        viewRef.current = clampView({
          zoom: nextZoom,
          offsetX: pendingZoom === 0 ? current.offsetX + pendingX : worldXBefore - wheel.mouseX / nextZoom + pendingX,
          offsetY: current.offsetY + pendingY,
        });
        if (Math.abs(nextZoom - zoomLabelRef.current) >= 0.01) {
          zoomLabelRef.current = nextZoom;
          setZoomLabel(nextZoom);
        }
        scheduleRender();
        if (Math.abs(zoomRatio - 1) > 0.0001) scheduleRebuild();
      };
      wheel.frame = requestAnimationFrame(animate);
    },
    [clampView, scheduleRender, scheduleRebuild]
  );

  // React 的 onWheel 是被动监听，preventDefault 无效，这里手动挂非被动监听
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    canvas.addEventListener('wheel', handleWheel, { passive: false });
    return () => canvas.removeEventListener('wheel', handleWheel);
  }, [handleWheel]);

  const animateViewTo = (target: ViewState) => {
    if (zoomAnimationRef.current !== null) cancelAnimationFrame(zoomAnimationRef.current);
    const start = { ...viewRef.current };
    const startedAt = performance.now();
    const duration = 360;
    const ease = (value: number) => 1 - Math.pow(1 - value, 3);
    const step = (now: number) => {
      const progress = Math.min(1, (now - startedAt) / duration);
      const eased = ease(progress);
      viewRef.current = clampView({
        zoom: start.zoom + (target.zoom - start.zoom) * eased,
        offsetX: start.offsetX + (target.offsetX - start.offsetX) * eased,
        offsetY: start.offsetY + (target.offsetY - start.offsetY) * eased,
      });
      const currentZoom = viewRef.current.zoom;
      zoomLabelRef.current = currentZoom;
      setZoomLabel(currentZoom);
      scheduleRender();
      if (progress < 1) {
        zoomAnimationRef.current = requestAnimationFrame(step);
      } else {
        zoomAnimationRef.current = null;
        scheduleRebuild();
      }
    };
    zoomAnimationRef.current = requestAnimationFrame(step);
  };

  const applyZoom = (factor: number) => {
    animateViewTo(clampView({ ...viewRef.current, zoom: viewRef.current.zoom * factor }));
  };

  const resetView = () => {
    animateViewTo(getDefaultView());
  };

  return (
    <div className="distribution-panel panel">
      <div className="distribution-toolbar">
        <button className="toolbar-button" onClick={() => applyZoom(1 / 1.3)}>
          缩小
        </button>
        <button className="toolbar-button" onClick={() => applyZoom(1.3)}>
          放大
        </button>
        <button className="toolbar-button" onClick={resetView}>
          重置
        </button>
        <span className="toolbar-hint">
          {zoomLabel.toFixed(2)}× · 滚轮缩放 · 拖拽平移 · Shift+滚轮纵向
        </span>
      </div>
      <div className="distribution-viewport" style={{ height }}>
        <canvas
          ref={canvasRef}
          className={`distribution-canvas${dragging ? ' dragging' : ''}`}
          style={{ height: '100%' }}
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={endDrag}
          onPointerCancel={endDrag}
          onPointerLeave={() => {
            hoverRef.current = null;
            setTooltip(null);
            scheduleRender();
          }}
        />
        {tooltip && (
          <div
            className="tooltip"
            style={{ left: Math.min(tooltip.x + 14, 620), top: tooltip.y + 16 }}
          >
            <div className="title">{tooltip.title}</div>
            <div className="meta">
              <div>{tooltip.process}</div>
              {tooltip.subject && <div>分类：{tooltip.subject}</div>}
              <div>
                {tooltip.range} · {tooltip.duration}
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
