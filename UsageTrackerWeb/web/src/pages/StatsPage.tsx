import { useEffect, useMemo, useState } from 'react';
import LoadingTransition from '../components/LoadingTransition';
import { api } from '../lib/api';
import type { BucketStat, SubjectNodeDto } from '../lib/api';
import { useDataChange } from '../lib/events';
import { formatHoursMinutes } from '../lib/format';
import type { ThemeController } from '../theme';

interface Props {
  kind: 'process' | 'subject';
  date: string;
  theme: ThemeController;
}

interface NodeProps {
  node: SubjectNodeDto;
  maxSeconds: number;
  parentSeconds: number;
  depth: number;
  collapsed: Set<string>;
  toggle: (key: string) => void;
  path: string;
  accent: string;
}

function withAlpha(hex: string, alpha: number): string {
  const value = parseInt(hex.slice(1), 16);
  const r = (value >> 16) & 255;
  const g = (value >> 8) & 255;
  const b = value & 255;
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function SubjectNodeCard({
  node,
  maxSeconds,
  parentSeconds,
  depth,
  collapsed,
  toggle,
  path,
  accent,
}: NodeProps) {
  const key = `${path}/${node.name}`;
  const isCollapsed = collapsed.has(key);
  const ratio = parentSeconds > 0 ? node.seconds / parentSeconds : 0;
  const totalRatio = maxSeconds > 0 ? node.seconds / maxSeconds : 0;
  const alpha = depth === 0 ? 1 : depth === 1 ? 0.78 : 0.56;

  return (
    <div className={`subject-node depth-${depth}`}>
      <div className="subject-node-card">
        <div className="subject-header">
          <button
            className={`subject-toggle${node.children.length === 0 ? ' leaf' : ''}`}
            onClick={() => node.children.length > 0 && toggle(key)}
            aria-label={node.children.length > 0 ? (isCollapsed ? '展开' : '收起') : undefined}
            aria-expanded={node.children.length > 0 ? !isCollapsed : undefined}
          >
            {node.children.length > 0 ? (isCollapsed ? '＋' : '−') : '·'}
          </button>
          <div className="subject-heading">
            <span className="subject-name">{node.name}</span>
            <span className="subject-meta">
              {formatHoursMinutes(node.seconds)} · {node.sessionCount} 次记录
            </span>
          </div>
          <span className="subject-percent" title={`占当前层级 ${Math.round(ratio * 100)}%，占总时长 ${Math.round(totalRatio * 100)}%`}>
            {Math.round(ratio * 100)}%
          </span>
        </div>
        <div className="progress-track">
          <div
            className="progress-fill"
            style={{
              width: `${Math.min(100, ratio * 100)}%`,
              background: withAlpha(accent, alpha),
            }}
          />
        </div>
      </div>
      {node.children.length > 0 && (
        <div className={`subject-children${isCollapsed ? ' collapsed' : ''}`}>
          <div className="subject-children-inner">
            {node.children.map((child) => (
              <SubjectNodeCard
                key={child.name}
                node={child}
                maxSeconds={maxSeconds}
                parentSeconds={node.seconds}
                depth={depth + 1}
                collapsed={collapsed}
                toggle={toggle}
                path={key}
                accent={accent}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

export default function StatsPage({ kind, date, theme }: Props) {
  const [rows, setRows] = useState<BucketStat[]>([]);
  const [tree, setTree] = useState<SubjectNodeDto[]>([]);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [autoReloadToken, setAutoReloadToken] = useState(0);

  // 后台数据/分类变化即局部自动刷新（分类统计尤其依赖关键词/手动分类的落盘变化）
  useDataChange(() => setAutoReloadToken((value) => value + 1));

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    const task = kind === 'process' ? api.ranking(date, 'process', 80) : api.subjectTree(date);
    Promise.resolve(task)
      .then((result) => {
        if (cancelled) return;
        if (Array.isArray(result) && result.length === 0) {
          setRows([]);
          setTree([]);
        } else if (kind === 'process') {
          setRows(result as BucketStat[]);
        } else {
          setTree(result as SubjectNodeDto[]);
        }
        setLoading(false);
      })
      .catch(() => setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [kind, date, autoReloadToken]);

  const maxSeconds = useMemo(() => Math.max(1, ...rows.map((x) => x.seconds)), [rows]);
  const maxTreeSeconds = useMemo(() => Math.max(1, ...tree.map((x) => x.seconds)), [tree]);
  const totalSeconds = useMemo(
    () => rows.reduce((total, row) => total + row.seconds, 0),
    [rows]
  );
  const totalTreeSeconds = useMemo(
    () => tree.reduce((total, node) => total + node.seconds, 0),
    [tree]
  );

  const toggle = (key: string) => {
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const badge = kind === 'process' ? `${rows.length} 个进程` : `${tree.length} 个大类`;
  const itemCount = kind === 'process' ? rows.reduce((total, row) => total + row.sessionCount, 0) : tree.reduce((total, node) => total + node.sessionCount, 0);
  const duration = kind === 'process' ? totalSeconds : totalTreeSeconds;

  return (
    <div className="page">
      <div className="insight-strip">
        <div className="insight-item">
          <span className="insight-label">统计范围</span>
          <strong>{badge}</strong>
          <span className="insight-note">按使用时长排序</span>
        </div>
        <div className="insight-item">
          <span className="insight-label">总使用时长</span>
          <strong>{formatHoursMinutes(duration)}</strong>
          <span className="insight-note">当天已记录时长</span>
        </div>
        <div className="insight-item">
          <span className="insight-label">记录次数</span>
          <strong>{itemCount.toLocaleString()}</strong>
          <span className="insight-note">会话记录</span>
        </div>
      </div>

      <LoadingTransition loading={loading} className="stats-loading-transition">
        {kind === 'process' ? (
        <div className="panel stat-list">
          <div className="list-caption">
            <span>应用使用排行</span>
            <span>时长 / 会话</span>
          </div>
          {rows.map((row, index) => (
            <div className="stat-row" key={row.key}>
              <span className={`stat-rank rank-${Math.min(index + 1, 4)}`}>
                {String(index + 1).padStart(2, '0')}
              </span>
              <span className="stat-app-mark">{row.key.trim().slice(0, 1).toUpperCase() || '·'}</span>
              <div className="stat-row-main">
                <div className="stat-row-top">
                  <span className="stat-row-name" title={row.key}>{row.key}</span>
                  <span className="stat-row-value">{formatHoursMinutes(row.seconds)}</span>
                </div>
                <div className="progress-track">
                  <div
                    className="progress-fill"
                    style={{ width: `${Math.min(100, (row.seconds / maxSeconds) * 100)}%` }}
                  />
                </div>
              </div>
              <span className="stat-row-count">{row.sessionCount} 次</span>
            </div>
          ))}
          {rows.length === 0 && <div className="loading">该日期没有记录</div>}
        </div>
      ) : (
        <div className="panel subject-tree">
          <div className="list-caption">
            <span>分类层级分布</span>
            <span>点击左侧符号展开层级</span>
          </div>
          {tree.map((node) => (
            <SubjectNodeCard
              key={node.name}
              node={node}
              maxSeconds={maxTreeSeconds}
              parentSeconds={maxTreeSeconds}
              depth={0}
              collapsed={collapsed}
              toggle={toggle}
              path=""
              accent={theme.colors.accent}
            />
          ))}
          {tree.length === 0 && (
            <div className="loading">该日期没有已归类记录（空分类不计入分类统计）</div>
          )}
        </div>
      )}
      </LoadingTransition>
    </div>
  );
}
