import { useEffect, useRef } from 'react';
import * as echarts from 'echarts';
import type { BucketStat, DailyPoint } from '../lib/api';
import { formatHoursMinutes } from '../lib/format';
import type { ThemeColors } from '../theme';

interface ChartTheme {
  panel: string;
  border: string;
  textPrimary: string;
  textSecondary: string;
  accent: string;
  accentSoft: string;
  categoryCard: string;
}

function getChartTheme(theme: ThemeColors): ChartTheme {
  return {
    panel: theme.panel,
    border: theme.border,
    textPrimary: theme.textPrimary,
    textSecondary: theme.textSecondary,
    accent: theme.accent,
    accentSoft: theme.accentSoft,
    categoryCard: theme.categoryCard,
  };
}

export function TrendChart({ data, theme }: { data: DailyPoint[]; theme: ThemeColors }) {
  const hostRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const colors = getChartTheme(theme);
    const chart = echarts.init(host);
    chartRef.current = chart;
    const gradient = new echarts.graphic.LinearGradient(0, 0, 0, 1, [
      { offset: 0, color: colors.accent },
      { offset: 1, color: colors.accentSoft },
    ]);

    chart.setOption({
      backgroundColor: 'transparent',
      animation: true,
      animationDuration: 650,
      animationDurationUpdate: 900,
      animationEasingUpdate: 'cubicInOut',
      grid: { left: 52, right: 18, top: 24, bottom: 34, containLabel: false },
      tooltip: {
        trigger: 'axis',
        backgroundColor: colors.panel,
        borderColor: colors.border,
        borderWidth: 1,
        padding: [9, 12],
        textStyle: { color: colors.textPrimary, fontSize: 12 },
        axisPointer: {
          type: 'line',
          lineStyle: { color: colors.accent, opacity: 0.35, type: 'dashed' },
        },
        formatter: (params: unknown) => {
          const list = params as Array<{ name: string; value: number }>;
          const item = list[0];
          return `${item.name}<br/><span style="color:${colors.accent}">●</span> 使用 ${formatHoursMinutes(item.value)}`;
        },
      },
      xAxis: {
        type: 'category',
        boundaryGap: true,
        data: data.map((x) => x.date.slice(5)),
        axisLine: { lineStyle: { color: colors.border } },
        axisLabel: { color: colors.textSecondary, fontSize: 11, margin: 12 },
        axisTick: { show: false },
      },
      yAxis: {
        type: 'value',
        axisLabel: {
          color: colors.textSecondary,
          fontSize: 10,
          formatter: (value: number) => `${(value / 3600).toFixed(1)}h`,
        },
        axisLine: { show: false },
        axisTick: { show: false },
        splitLine: { lineStyle: { color: colors.border, type: 'dashed', opacity: 0.65 } },
      },
      series: [
        {
          type: 'bar',
          data: data.map((x) => Math.round(x.seconds)),
          barMaxWidth: 22,
          showBackground: true,
          backgroundStyle: { color: colors.categoryCard, borderRadius: [6, 6, 0, 0] },
          itemStyle: {
            color: gradient,
            borderRadius: [6, 6, 2, 2],
            shadowBlur: 10,
            shadowColor: colors.accentSoft,
            shadowOffsetY: 3,
          },
          emphasis: {
            itemStyle: {
              shadowBlur: 18,
              shadowColor: colors.accent,
            },
          },
        },
      ],
    });

    const resize = () => chart.resize();
    window.addEventListener('resize', resize);
    const observer = new ResizeObserver(resize);
    observer.observe(host);
    return () => {
      window.removeEventListener('resize', resize);
      observer.disconnect();
      chart.dispose();
      chartRef.current = null;
    };
  }, [theme]);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    chart.setOption({
      xAxis: { data: data.map((x) => x.date.slice(5)) },
      series: [{ data: data.map((x) => Math.round(x.seconds)) }],
    });
  }, [data]);

  return <div className="chart-host trend-chart-host" ref={hostRef} />;
}

export function RankingChart({
  data,
  title,
  theme,
}: {
  data: BucketStat[];
  title: string;
  theme: ThemeColors;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const colors = getChartTheme(theme);
    const chart = echarts.init(host);
    chartRef.current = chart;
    const sorted = [...data].reverse();
    const gradient = new echarts.graphic.LinearGradient(0, 0, 1, 0, [
      { offset: 0, color: colors.accentSoft },
      { offset: 1, color: colors.accent },
    ]);

    chart.setOption({
      backgroundColor: 'transparent',
      animation: true,
      animationDuration: 650,
      animationDurationUpdate: 700,
      animationEasingUpdate: 'cubicInOut',
      grid: { left: 16, right: 64, top: 8, bottom: 28, containLabel: true },
      tooltip: {
        trigger: 'item',
        appendToBody: true,
        confine: false,
        backgroundColor: colors.panel,
        borderColor: colors.border,
        borderWidth: 1,
        padding: [9, 12],
        textStyle: { color: colors.textPrimary, fontSize: 12 },
        formatter: (params: unknown) => {
          const raw = Array.isArray(params) ? params[0] : params;
          const item = raw as { name: string; value: number; data?: { sessionCount?: number } };
          const sessionCount = item.data?.sessionCount ?? 0;
          return `${item.name}<br/><span style="color:${colors.accent}">●</span> ${formatHoursMinutes(item.value)} · ${sessionCount} 次`;
        },
      },
      xAxis: {
        type: 'value',
        axisLabel: {
          color: colors.textSecondary,
          fontSize: 10,
          formatter: (value: number) => `${(value / 3600).toFixed(1)}h`,
        },
        axisLine: { show: false },
        axisTick: { show: false },
        splitLine: { lineStyle: { color: colors.border, type: 'dashed', opacity: 0.65 } },
      },
      yAxis: {
        type: 'category',
        data: sorted.map((x) => x.key),
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { show: false },
      },
      series: [
        {
          type: 'bar',
          data: sorted.map((x) => ({
            value: Math.round(x.seconds),
            sessionCount: x.sessionCount,
          })),
          itemStyle: {
            color: gradient,
            borderRadius: [0, 7, 7, 0],
            shadowBlur: 8,
            shadowColor: colors.accentSoft,
            shadowOffsetX: 3,
          },
          barMaxWidth: 16,
          showBackground: true,
          backgroundStyle: { color: colors.categoryCard, borderRadius: [0, 7, 7, 0] },
          label: {
            show: true,
            position: 'right',
            color: colors.textSecondary,
            fontSize: 10,
            formatter: (params: { value: number }) => formatHoursMinutes(params.value),
          },
          emphasis: {
            itemStyle: {
              shadowBlur: 18,
              shadowColor: colors.accent,
              shadowOffsetX: 3,
            },
          },
        },
      ],
    });

    const resize = () => chart.resize();
    window.addEventListener('resize', resize);
    const observer = new ResizeObserver(resize);
    observer.observe(host);
    return () => {
      window.removeEventListener('resize', resize);
      observer.disconnect();
      chart.dispose();
      chartRef.current = null;
    };
  }, [theme]);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    const sorted = [...data].reverse();
    chart.setOption({
      yAxis: { data: sorted.map((x) => x.key) },
      series: [{
        data: sorted.map((x) => ({
          value: Math.round(x.seconds),
          sessionCount: x.sessionCount,
        })),
      }],
    });
  }, [data]);

  return (
    <div className="panel ranking-chart-panel">
      <div className="panel-title">{title}</div>
      <div className="chart-host ranking-chart-host" ref={hostRef} />
    </div>
  );
}
