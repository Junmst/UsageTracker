export interface SessionDto {
  id: string;
  processName: string;
  windowTitle: string;
  startTime: string;
  endTime?: string | null;
  manualSubject?: string | null;
  lastCapturedAt?: string | null;
  durationSeconds?: number;
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

export interface SettingsSnapshot {
  theme: string | null;
  themeAccentColor: string | null;
  themeAccentRecentColors: string[];
  themeAccentSlots: string[];
  subjectCount: number;
  subjectDefinitions: SubjectDefinition[];
}

export interface SearchResult {
  items: SessionDto[];
  totalCount: number;
}

const responseCache = new Map<string, { value: unknown; expiresAt: number }>();
const pendingRequests = new Map<string, Promise<unknown>>();

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
    throw new Error(`请求失败 ${response.status}：${url}`);
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
  searchVersion: (forceRefresh = false) =>
    get<{ version: string }>('/api/search-version', 0, forceRefresh),
  search: (q: string, skip = 0, take = 50, forceRefresh = false) =>
    get<SearchResult>(
      `/api/search?q=${encodeURIComponent(q)}&skip=${skip}&take=${take}`,
      15_000,
      forceRefresh
    ),
  active: (subject?: string | null, forceRefresh = false) =>
    get<SessionDto | null>(`/api/active?${new URLSearchParams(subject ? { subject } : {})}`, 0, forceRefresh),
  webPreferences: () => get<WebPreferences>('/api/web-preferences', 60_000),
  saveOverviewRange: (from: string, to: string) =>
    send<WebPreferences>('/api/web-preferences/range', 'POST', { from, to }),
  clearOverviewRange: () => send<void>('/api/web-preferences/range', 'DELETE'),
};
