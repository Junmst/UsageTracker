// 全部口径镜像桌面版，避免网页与桌面统计数字不一致。

export const DAY_BOUNDARY_HOUR = 4;

export function pad2(value: number): string {
  return value < 10 ? `0${value}` : `${value}`;
}

export function formatDateKey(date: Date): string {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

export function parseDateKey(key: string): Date {
  const [y, m, d] = key.split('-').map(Number);
  return new Date(y, m - 1, d);
}

/// <summary>凌晨 4 点为日界线：0:00-3:59 归属前一天。</summary>
export function getTimeDistributionDate(time: Date): Date {
  const date = new Date(time.getFullYear(), time.getMonth(), time.getDate());
  if (time.getHours() < DAY_BOUNDARY_HOUR) {
    date.setDate(date.getDate() - 1);
  }
  return date;
}

export function getDayStart(date: Date): Date {
  const d = new Date(date.getFullYear(), date.getMonth(), date.getDate(), DAY_BOUNDARY_HOUR);
  return d;
}

export function getDayEnd(date: Date): Date {
  const d = getDayStart(date);
  d.setDate(d.getDate() + 1);
  return d;
}

/// <summary>镜像 TimeDistributionControl.FormatDurationShort。</summary>
export function formatDurationShort(seconds: number): string {
  const totalMinutes = seconds / 60;
  if (totalMinutes < 60) {
    return `${totalMinutes.toFixed(1)}分钟`;
  }
  let hours = Math.floor(totalMinutes / 60);
  let minutes = Math.round(totalMinutes - hours * 60);
  if (minutes === 60) {
    hours += 1;
    minutes = 0;
  }
  return `${hours}h${minutes}m`;
}

export function formatHoursMinutes(seconds: number): string {
  const totalMinutes = Math.round(seconds / 60);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours === 0) {
    return `${minutes}分钟`;
  }
  return `${hours}小时${minutes}分钟`;
}

export function getChineseWeekday(date: Date): string {
  switch (date.getDay()) {
    case 1:
      return '周一';
    case 2:
      return '周二';
    case 3:
      return '周三';
    case 4:
      return '周四';
    case 5:
      return '周五';
    case 6:
      return '周六';
    default:
      return '周天';
  }
}

/// <summary>镜像 DateLabelLayer.FormatDateLabel。</summary>
export function formatDateLabel(date: Date): string {
  const today = new Date();
  const todayKey = formatDateKey(today);
  const yesterdayKey = formatDateKey(new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1));
  const key = formatDateKey(date);

  if (key === todayKey) {
    return `今天（${getChineseWeekday(date)}）`;
  }
  if (key === yesterdayKey) {
    return `昨天（${getChineseWeekday(date)}）`;
  }

  const dateText =
    date.getFullYear() === today.getFullYear()
      ? `${date.getMonth() + 1}月${date.getDate()}日`
      : `${date.getFullYear()}年${date.getMonth() + 1}月${date.getDate()}日`;
  return `${dateText}（${getChineseWeekday(date)}）`;
}

export function formatClock(date: Date): string {
  return `${pad2(date.getHours())}:${pad2(date.getMinutes())}`;
}
