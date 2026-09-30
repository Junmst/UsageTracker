export interface SessionDto {
  id: string;
  processName: string;
  windowTitle: string;
  startTime: string;
  endTime?: string | null;
  manualSubject?: string | null;
  parallelActivities?: ParallelActivityDto[];
  lastCapturedAt?: string | null;
  durationSeconds?: number;
}

export interface ParallelActivityDto {
  processName: string;
  windowTitle: string;
  description: string;
  observedSeconds: number;
  countInTotal: boolean;
}

export interface BucketStat {
  key: string;
  seconds: number;
  sessionCount: number;
}

export interface DailyPoint {
  date: string;
  seconds: number;
}

export interface RangeSummary {
  from: string;
  to: string;
  seconds: number;
  trackedDays: number;
  sessionCount: number;
  processCount: number;
  daily: DailyPoint[];
  ranking: BucketStat[];
}

export interface Overview {
  date: string;
  todaySeconds: number;
  weekSeconds: number;
  monthSeconds: number;
  totalSeconds: number;
  trackedDays: number;
  sessionCount: number;
  earliestDate?: string | null;
  active?: SessionDto | null;
  databaseSizeMb: number;
}

export interface Meta {
  databasePath: string;
  databaseExists: boolean;
  databaseSizeMb: number;
  sessionCount: number;
  earliestDate?: string | null;
}

export interface DistributionResponse {
  dates: string[];
  sessions: SessionDto[];
}

export interface SubjectNodeDto {
  name: string;
  seconds: number;
  sessionCount: number;
  children: SubjectNodeDto[];
}

export interface SubjectDefinition {
  name: string;
  parents?: { name: string; children?: string[] }[] | null;
  children?: string[] | null;
}

export interface WebPreferences {
  overviewRange?: { from: string; to: string } | null;
}

export interface AgentStatus {
  running: boolean;
  tracking: boolean;
  isIdle: boolean;
  isManualIdle: boolean;
  isVideoPlayback?: boolean;
  activeProcessName?: string | null;
  activeWindowTitle?: string | null;
  activeStartTime?: string | null;
  lastCapturedAt?: string | null;
  error?: string;
}

export interface SettingsSnapshot {
  theme: string | null;
  themeAccentColor: string | null;
  themeAccentRecentColors: string[];
  themeAccentSlots: string[];
  subjectCount: number;
  subjectDefinitions: SubjectDefinition[];
  idleTimeoutMinutes?: number | null;
}

export interface SubjectManagementSnapshot {
  subjectDefinitions: SubjectDefinition[];
  keywordRules: Record<string, string[]>;
  parallelWhitelistProcesses: string[];
  deleteBehavior: 'MatchRules' | 'PromoteToParent' | string;
  canUndo: boolean;
}

export interface SearchResult {
  items: SessionDto[];
  totalCount: number;
}

export interface TransferPreview {
  filePath: string;
  kind: string;
  totalRecords: number;
  earliestStartTime?: string | null;
  latestStartTime?: string | null;
  nonConflictCount: number;
  conflictCount: number;
  subjectDefinitionCount: number;
  subjectKeywordRuleCount: number;
  theme?: string | null;
  themeAccentColor?: string | null;
  idleTimeoutMinutes?: number | null;
  manualIdleShortcutText?: string | null;
  subjectDeleteBehavior?: string | null;
}

export interface TransferExportResult {
  path: string;
  kind: string;
}


const responseCache = new Map<string, { value: unknown; expiresAt: number }>();
const pendingRequests = new Map<string, Promise<unknown>>();

// 后台数据/配置变更后清空 GET 缓存，保证随后的自动刷新拿到最新数据而不是 15 秒内的旧响应
export function invalidateResponseCache(): void {
  responseCache.clear();
}

async function get<T>(url: string, ttlMs = 15_000, forceRefresh = false): Promise<T> {
  const cached = responseCache.get(url);
  if (!forceRefresh && cached && cached.expiresAt > Date.now()) {
    return cached.value as T;
  }

  const pending = pendingRequests.get(url);
  if (pending) {
    return pending as Promise<T>;
  }

  const request = fetch(url)
    .then(async (response) => {
      if (!response.ok) {
        throw new Error(`请求失败 ${response.status}：${url}`);
      }
      const value = (await response.json()) as T;
      responseCache.set(url, { value, expiresAt: Date.now() + ttlMs });
      return value;
    })
    .finally(() => {
      pendingRequests.delete(url);
    });
  pendingRequests.set(url, request);
  return request;
}

async function send<T>(url: string, method: 'POST' | 'DELETE', body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) {
    let message = `请求失败 ${response.status}：${url}`;
    try {
      const errorData = await response.json();
      if (errorData?.message) message = errorData.message;
      else if (errorData?.detail) message = errorData.detail;
    } catch { /* 错误响应不是 JSON，用默认消息 */ }
    throw new Error(message);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

export const api = {
  meta: () => get<Meta>('/api/meta'),
  overview: (date?: string, subject?: string | null, forceRefresh = false) =>
    get<Overview>(`/api/overview?${new URLSearchParams({ ...(date ? { date } : {}), ...(subject ? { subject } : {}) })}`, 15_000, forceRefresh),
  daily: (days: number, subject?: string | null, forceRefresh = false) =>
    get<DailyPoint[]>(`/api/daily?${new URLSearchParams({ days: String(days), ...(subject ? { subject } : {}) })}`, 15_000, forceRefresh),
  rangeSummary: (from: string, to: string, subject?: string | null, forceRefresh = false) =>
    get<RangeSummary>(`/api/range-summary?${new URLSearchParams({ from, to, ...(subject ? { subject } : {}) })}`, 15_000, forceRefresh),
  ranking: (date: string, type: 'process' | 'subject', top = 12, subject?: string | null, forceRefresh = false) =>
    get<BucketStat[]>(`/api/ranking?${new URLSearchParams({ date, type, top: String(top), ...(subject ? { subject } : {}) })}`, 15_000, forceRefresh),
  distribution: (from?: string, to?: string, forceRefresh = false) =>
    get<DistributionResponse>(
      `/api/distribution${from ? `?from=${from}&to=${to ?? from}` : ''}`,
      15_000,
      forceRefresh
    ),
  subjectTree: (date: string) => get<SubjectNodeDto[]>(`/api/subject-tree?date=${date}`),
  settings: () => get<SettingsSnapshot>('/api/settings'),
  saveAppearance: (theme: string, accent: string) => send<void>('/api/settings/appearance', 'POST', { theme, accent }),
  saveIdleTimeout: (minutes: number) => send<void>('/api/settings/idle-timeout', 'POST', { minutes }),
  subjectManagement: (forceRefresh = false) => get<SubjectManagementSnapshot>('/api/subject-management', 0, forceRefresh),
  subjectCommand: (command: string, args: Record<string, unknown> = {}) => send<{ success: boolean; message?: string; data?: unknown }>('/api/subject-management/command', 'POST', { command, args }),
  searchVersion: (forceRefresh = false) =>
    get<{ version: string }>('/api/search-version', 0, forceRefresh),
  search: (q: string, skip = 0, take = 50, forceRefresh = false, options?: { date?: string; allHistory?: boolean; mode?: string; subject?: string | null }) =>
    get<SearchResult>(
      `/api/search?${new URLSearchParams({ q, skip: String(skip), take: String(take), ...(options?.date ? { date: options.date } : {}), ...(options?.allHistory ? { allHistory: 'true' } : {}), ...(options?.mode ? { mode: options.mode } : {}), ...(options?.subject ? { subject: options.subject } : {}) })}`,
      15_000,
      forceRefresh
    ),
  active: (subject?: string | null, forceRefresh = false) =>
    get<SessionDto | null>(`/api/active?${new URLSearchParams(subject ? { subject } : {})}`, 0, forceRefresh),
  agentStatus: (forceRefresh = false) => get<AgentStatus>('/api/agent/status', 2_000, forceRefresh),
  startAgent: () => send<void>('/api/agent/start', 'POST'),
  restartAgent: () => send<void>('/api/agent/restart', 'POST'),
  enterManualIdle: () => send<void>('/api/agent/idle', 'POST'),
  exitAgent: () => send<void>('/api/agent/exit', 'POST'),
  sessionCommand: (command: string, args: Record<string, unknown> = {}) => send<void>('/api/session/command', 'POST', { command, args }),
  bulkDeleteSessions: (sessions: SessionDto[]) => send<{ deleted: number }>('/api/session/command', 'POST', { command: 'session-bulk-delete', args: { sessions } }),
  webPreferences: () => get<WebPreferences>('/api/web-preferences', 60_000),
  saveOverviewRange: (from: string, to: string) =>
    send<WebPreferences>('/api/web-preferences/range', 'POST', { from, to }),
  clearOverviewRange: () => send<void>('/api/web-preferences/range', 'DELETE'),
  transferExport: (kind: 'usage' | 'settings' | 'full') => send<TransferExportResult>('/api/transfer/export', 'POST', { kind }),
  transferUpload: async (file: File) => {
    const form = new FormData();
    form.append('file', file);
    const response = await fetch('/api/transfer/upload', { method: 'POST', body: form });
    if (!response.ok) throw new Error(`上传失败 ${response.status}`);
    return (await response.json()) as { path: string; fileName: string; length: number };
  },
  transferPreview: (path: string, kind?: string) => send<TransferPreview>('/api/transfer/preview', 'POST', { path, kind }),
  transferPreviewSessions: (path: string) => send<SessionDto[]>('/api/transfer/preview-sessions', 'POST', { path }),
  transferImport: (path: string, dataMode: string, conflictStrategy: string, settingsMode: string) =>
    send<TransferPreview | { importedCount: number; settingsChangedCount: number }>('/api/transfer/import', 'POST', { path, dataMode, conflictStrategy, settingsMode }),
  transferDownloadUrl: (path: string) => `/api/transfer/download?path=${encodeURIComponent(path)}`,
};
