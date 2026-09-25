import { useEffect, useState } from 'react';
import { api } from '../lib/api';
import type { Meta, SubjectDefinition } from '../lib/api';
import type { ThemeController } from '../theme';

interface Props {
  theme: ThemeController;
  palette: string[];
}

export default function SettingsPage({ theme, palette }: Props) {
  const [meta, setMeta] = useState<Meta | null>(null);

  useEffect(() => {
    void api.meta().then(setMeta).catch(() => undefined);
  }, []);

  const colors = palette.length > 0 ? palette : theme.palette;

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h2>设置</h2>
          <div className="page-subtitle">只读视图 · 数据维护请在桌面版「时迹」进行</div>
        </div>
      </div>

      <div className="panel appearance-panel">
        <div className="panel-title">外观</div>
        <div className="appearance-section">
          <div className="appearance-label">主题色</div>
          <div className="swatch-row">
            {colors.map((color) => (
              <button
                key={color}
                className={`swatch${theme.accent.toUpperCase() === color.toUpperCase() ? ' selected' : ''}`}
                style={{ background: color }}
                onClick={() => theme.setAccent(color)}
                title={color}
              />
            ))}
          </div>
        </div>
        <div className="appearance-section">
          <div className="appearance-label">模式</div>
          <div className="mode-row">
            <button
              className={`toolbar-button${theme.mode === 'dark' ? ' active' : ''}`}
              onClick={() => theme.setMode('dark')}
            >
              深色
            </button>
            <button
              className={`toolbar-button${theme.mode === 'light' ? ' active' : ''}`}
              onClick={() => theme.setMode('light')}
            >
              浅色
            </button>
          </div>
        </div>
      </div>

      <div className="panel info-list">
        <div className="info-row">
          <span>数据源</span>
          <span className="info-value">{meta?.databasePath ?? '—'}</span>
        </div>
        <div className="info-row">
          <span>数据库大小</span>
          <span className="info-value">{meta?.databaseSizeMb ?? 0} MB</span>
        </div>
        <div className="info-row">
          <span>会话记录</span>
          <span className="info-value">{(meta?.sessionCount ?? 0).toLocaleString()} 条</span>
        </div>
        <div className="info-row">
          <span>最早记录</span>
          <span className="info-value">{meta?.earliestDate ?? '—'}</span>
        </div>
        <div className="info-row">
          <span>当前主题色</span>
          <span className="info-value">
            <span className="swatch-dot" style={{ background: theme.colors.accent }} />
            {theme.colors.accent}
          </span>
        </div>
        <div className="info-row">
          <span>主题模式</span>
          <span className="info-value">{theme.mode === 'dark' ? '深色' : '浅色'}</span>
        </div>
      </div>
    </div>
  );
}

export function SubjectStructure({ definitions }: { definitions: SubjectDefinition[] }) {
  const [keyword, setKeyword] = useState('');
  const [majorName, setMajorName] = useState<string | null>(null);
  const [collapsedParents, setCollapsedParents] = useState<Set<string>>(new Set());

  if (definitions.length === 0) {
    return <div className="panel loading">未配置分类</div>;
  }

  const kw = keyword.trim().toLowerCase();
  const hit = (text: string) => kw.length === 0 || text.toLowerCase().includes(kw);
  const branchMatches = (major: SubjectDefinition) =>
    hit(major.name) ||
    (major.children ?? []).some(hit) ||
    (major.parents ?? []).some(
      (parent) => hit(parent.name) || (parent.children ?? []).some(hit)
    );

  const majors = definitions.filter(branchMatches);
  const selectedMajor = majors.find((major) => major.name === majorName) ?? null;
  const selectedParents = (selectedMajor?.parents ?? []).filter(
    (parent) =>
      kw.length === 0 ||
      hit(selectedMajor?.name ?? '') ||
      hit(parent.name) ||
      (parent.children ?? []).some(hit)
  );
  const selectedDirectChildren = (selectedMajor?.children ?? []).filter(
    (child) => kw.length === 0 || hit(selectedMajor?.name ?? '') || hit(child)
  );
  const parentCount = definitions.reduce((sum, major) => sum + (major.parents ?? []).length, 0);
  const childCount = definitions.reduce(
    (sum, major) =>
      sum +
      (major.children ?? []).length +
      (major.parents ?? []).reduce((parentSum, parent) => parentSum + (parent.children ?? []).length, 0),
    0
  );

  const clearSelection = () => {
    setMajorName(null);
    setKeyword('');
  };

  return (
    <div className="subject-manage">
      <div className="subject-manage-toolbar">
        <div className="search-shell">
          <span className="search-symbol">⌕</span>
          <input
            className="search-input"
            value={keyword}
            placeholder="搜索大类、父类或子类"
            onChange={(event) => setKeyword(event.target.value)}
          />
          {keyword && (
            <button className="search-clear" onClick={() => setKeyword('')} aria-label="清除搜索">
              ×
            </button>
          )}
        </div>
        <button className="toolbar-button" onClick={clearSelection}>清除选择</button>
        <span className="toolbar-hint">
          {selectedMajor ? `正在查看：${selectedMajor.name}` : '选择左侧大类查看完整分类结构'}
        </span>
      </div>

      <div className="subject-manage-summary">
        <div className="manage-stat accent-stat">
          <span className="manage-stat-icon">◇</span>
          <div><strong>{majors.length}</strong><span>大类</span></div>
        </div>
        <div className="manage-stat">
          <span className="manage-stat-icon">◈</span>
          <div><strong>{parentCount}</strong><span>父类</span></div>
        </div>
        <div className="manage-stat">
          <span className="manage-stat-icon">✦</span>
          <div><strong>{childCount}</strong><span>子类</span></div>
        </div>
      </div>

      <div className="subject-manage-layout">
        <aside className="panel subject-manage-sidebar">
          <div className="manage-section-heading">
            <div><span className="eyebrow">分类目录</span><strong>大类</strong></div>
            <span className="section-count">{majors.length}</span>
          </div>
          <div className="subject-major-list">
            {majors.map((major, index) => {
              const children = (major.children ?? []).length + (major.parents ?? []).reduce(
                (sum, parent) => sum + (parent.children ?? []).length,
                0
              );
              return (
                <button
                  key={major.name}
                  className={`subject-major-card${major.name === majorName ? ' active' : ''}`}
                  onClick={() => setMajorName(major.name)}
                >
                  <span className="major-index">{String(index + 1).padStart(2, '0')}</span>
                  <span className="major-card-main">
                    <strong className="ellipsis">{major.name}</strong>
                    <small>{(major.parents ?? []).length} 个父类 · {children} 个子类</small>
                  </span>
                  <span className="major-arrow">→</span>
                </button>
              );
            })}
            {majors.length === 0 && <div className="loading">无匹配分类</div>}
          </div>
        </aside>

        <section className="panel subject-manage-detail">
          {selectedMajor ? (
            <div className="subject-manage-detail-content" key={selectedMajor.name}>
              <div className="detail-header">
                <div>
                  <span className="eyebrow">当前分类</span>
                  <h3>{selectedMajor.name}</h3>
                </div>
                <span className="detail-count">{selectedParents.length} 个父类</span>
              </div>
              <div className="parent-card-grid">
                {selectedParents.map((parent) => {
                  const parentKey = `${selectedMajor.name}/${parent.name}`;
                  const allChildren = parent.children ?? [];
                  const children = kw.length === 0 || hit(selectedMajor.name) || hit(parent.name)
                    ? allChildren
                    : allChildren.filter(hit);
                  const isCollapsed = collapsedParents.has(parentKey);
                  return (
                    <article className={`subject-parent-card${isCollapsed ? ' collapsed' : ''}`} key={parent.name}>
                      <div className="parent-card-header">
                        <span className="parent-marker" />
                        <strong>{parent.name}</strong>
                        <span className="parent-card-count">{allChildren.length} 子类</span>
                        <button
                          type="button"
                          className="subject-manage-collapse"
                          aria-label={`${isCollapsed ? '展开' : '折叠'}${parent.name}子类`}
                          aria-expanded={!isCollapsed}
                          onClick={() => {
                            setCollapsedParents((current) => {
                              const next = new Set(current);
                              if (next.has(parentKey)) next.delete(parentKey);
                              else next.add(parentKey);
                              return next;
                            });
                          }}
                        >
                          {isCollapsed ? '＋' : '−'}
                        </button>
                      </div>
                      <div className="child-chip-list">
                        <div className="child-chip-list-inner">
                          {children.map((child) => <span className="child-chip" key={child}>{child}</span>)}
                          {children.length === 0 && <span className="empty-chip">暂无匹配子类</span>}
                        </div>
                      </div>
                    </article>
                  );
                })}
                {selectedDirectChildren.length > 0 && (
                  <article className="subject-parent-card direct-children-card">
                    <div className="parent-card-header">
                      <span className="parent-marker" />
                      <strong>直属子类</strong>
                      <span className="parent-card-count">{selectedDirectChildren.length} 项</span>
                    </div>
                    <div className="child-chip-list">
                      <div className="child-chip-list-inner">
                        {selectedDirectChildren.map((child) => <span className="child-chip" key={child}>{child}</span>)}
                      </div>
                    </div>
                  </article>
                )}
              </div>
              {selectedParents.length === 0 && selectedDirectChildren.length === 0 && (
                <div className="empty-state">该大类暂未配置下级分类</div>
              )}
            </div>
          ) : (
            <div className="subject-manage-empty">
              <div className="empty-orbit">◇</div>
              <h3>浏览分类结构</h3>
              <p>从左侧选择一个大类，查看父类与子类的完整关系。</p>
            </div>
          )}
        </section>
      </div>
    </div>
  );
}
