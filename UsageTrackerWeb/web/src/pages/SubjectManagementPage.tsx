import { useEffect, useMemo, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { api, type SubjectDefinition, type SubjectManagementSnapshot } from '../lib/api';

interface Props {
  settings: SubjectManagementSnapshot | null;
  onChanged: () => void;
}

type SelectionKind = 'major' | 'parent' | 'child' | 'rule' | 'process';
type ContextKind = Exclude<SelectionKind, 'rule' | 'process'> | 'rule' | 'process';

const emptySet = () => new Set<string>();

export default function SubjectManagementPage({ settings, onChanged }: Props) {
  const [majorName, setMajorName] = useState('');
  const [parentName, setParentName] = useState('');
  const [childName, setChildName] = useState('');
  const [majorInput, setMajorInput] = useState('');
  const [parentInput, setParentInput] = useState('');
  const [childInput, setChildInput] = useState('');
  const [keywordInput, setKeywordInput] = useState('');
  const [processInput, setProcessInput] = useState('');
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);
  const [selectedMajors, setSelectedMajors] = useState<Set<string>>(emptySet);
  const [selectedParents, setSelectedParents] = useState<Set<string>>(emptySet);
  const [selectedChildren, setSelectedChildren] = useState<Set<string>>(emptySet);
  const [selectedRules, setSelectedRules] = useState<Set<string>>(emptySet);
  const [selectedProcesses, setSelectedProcesses] = useState<Set<string>>(emptySet);
  const [anchors, setAnchors] = useState<Partial<Record<SelectionKind, string>>>({});
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; kind: ContextKind } | null>(null);

  const definitions = settings?.subjectDefinitions ?? [];
  const major = definitions.find((item) => item.name === majorName) ?? definitions[0];
  const parents = major?.parents ?? [];
  const parent = parents.find((item) => item.name === parentName);
  const children = parent?.children ?? [];
  const subject = childName || parentName || major?.name || '';
  const rules = settings?.keywordRules[subject] ?? [];
  const processes = settings?.parallelWhitelistProcesses ?? [];

  const selectedCounts = useMemo(() => ({
    major: selectedMajors.size,
    parent: selectedParents.size,
    child: selectedChildren.size,
    rule: selectedRules.size,
    process: selectedProcesses.size,
  }), [selectedMajors, selectedParents, selectedChildren, selectedRules, selectedProcesses]);

  useEffect(() => {
    if (!majorName && definitions[0]) setMajorName(definitions[0].name);
  }, [definitions, majorName]);

  useEffect(() => {
    if (!major) return;
    if (!parents.some((item) => item.name === parentName)) setParentName('');
    if (!children.includes(childName)) setChildName('');
  }, [major, parents, children, parentName, childName]);

  useEffect(() => {
    const close = () => setContextMenu(null);
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const editing = target?.tagName === 'INPUT' || target?.tagName === 'TEXTAREA' || target?.tagName === 'SELECT' || target?.isContentEditable;
      if (editing) return;
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
        event.preventDefault();
        void run('undo');
        return;
      }
      if (event.key === 'Escape') {
        setContextMenu(null);
        return;
      }
      if (event.key === 'Delete' || event.key === 'Backspace') {
        event.preventDefault();
        void deleteSelection();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('click', close);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('click', close);
    };
  });

  const run = async (command: string, args: Record<string, unknown> = {}) => {
    setBusy(true);
    try {
      await api.subjectCommand(command, args);
      setStatus('操作已完成');
      clearSelections();
      await onChanged();
    } catch (error) {
      setStatus(error instanceof Error ? error.message : '操作失败');
    } finally {
      setBusy(false);
    }
  };

  const runMany = async (value: string, command: string, extra: Record<string, unknown> = {}, valueKey = 'name') => {
    const values = value.split(/[,，]/).map((item) => item.trim()).filter(Boolean);
    if (values.length === 0) return;
    setBusy(true);
    try {
      for (const value of values) await api.subjectCommand(command, { ...extra, [valueKey]: value });
      setStatus(`已完成 ${values.length} 项操作`);
      await onChanged();
    } catch (error) {
      setStatus(error instanceof Error ? error.message : '操作失败');
    } finally {
      setBusy(false);
    }
  };

  const getSelected = (kind: SelectionKind) => {
    if (kind === 'major') return selectedMajors;
    if (kind === 'parent') return selectedParents;
    if (kind === 'child') return selectedChildren;
    if (kind === 'rule') return selectedRules;
    return selectedProcesses;
  };

  const setSelected = (kind: SelectionKind, value: Set<string>) => {
    if (kind === 'major') setSelectedMajors(value);
    else if (kind === 'parent') setSelectedParents(value);
    else if (kind === 'child') setSelectedChildren(value);
    else if (kind === 'rule') setSelectedRules(value);
    else setSelectedProcesses(value);
  };

  const clearSelections = () => {
    setSelectedMajors(emptySet());
    setSelectedParents(emptySet());
    setSelectedChildren(emptySet());
    setSelectedRules(emptySet());
    setSelectedProcesses(emptySet());
    setAnchors({});
  };

  const selectItem = (kind: SelectionKind, key: string, list: string[], event: ReactMouseEvent) => {
    event.stopPropagation();
    const current = getSelected(kind);
    const next = event.shiftKey && anchors[kind] && list.includes(anchors[kind]!)
      ? new Set<string>(list.slice(Math.min(list.indexOf(anchors[kind]!), list.indexOf(key)), Math.max(list.indexOf(anchors[kind]!), list.indexOf(key)) + 1))
      : event.ctrlKey || event.metaKey
        ? new Set(current)
        : new Set<string>();
    if (!(event.shiftKey && anchors[kind])) {
      if (next.has(key)) next.delete(key);
      else next.add(key);
    }
    setSelected(kind, next);
    setAnchors((value) => ({ ...value, [kind]: key }));
  };

  const openContextMenu = (event: ReactMouseEvent, kind: ContextKind, key: string, list: string[]) => {
    event.preventDefault();
    event.stopPropagation();
    if (!getSelected(kind).has(key)) selectItem(kind, key, list, event);
    setContextMenu({ x: Math.min(event.clientX, window.innerWidth - 270), y: Math.min(event.clientY, window.innerHeight - 230), kind });
  };

  const deleteSelection = async () => {
    if (!settings) return;
    if (selectedRules.size > 0 && subject) {
      if (window.confirm(`删除选中的 ${selectedRules.size} 条关键词？`)) await run('subject-remove-keywords', { subject, keywords: [...selectedRules] });
      return;
    }
    if (selectedChildren.size > 0 && major && parent) {
      if (window.confirm(`删除选中的 ${selectedChildren.size} 个子类？`)) await run('subject-remove-children', { major: major.name, parent: parent.name, names: [...selectedChildren], promoteToParent: settings.deleteBehavior === 'PromoteToParent' });
      return;
    }
    if (selectedParents.size > 0 && major) {
      if (window.confirm(`删除选中的 ${selectedParents.size} 个父类？`)) await run('subject-remove-parents', { major: major.name, names: [...selectedParents], promoteToParent: settings.deleteBehavior === 'PromoteToParent' });
      return;
    }
    if (selectedMajors.size > 0) {
      if (window.confirm(`删除选中的 ${selectedMajors.size} 个大类？`)) await run('subject-remove-majors', { names: [...selectedMajors] });
      return;
    }
    if (selectedProcesses.size > 0 && window.confirm(`删除选中的 ${selectedProcesses.size} 个白名单进程？`)) {
      await run('subject-remove-whitelist-processes', { processes: [...selectedProcesses] });
    }
  };

  const selectedTotal = Object.values(selectedCounts).reduce((sum, count) => sum + count, 0);

  if (!settings) return <div className="panel loading">正在读取桌面版分类设置…</div>;

  return (
    <div className="page subject-manage subject-manage-modern">
      <div className="page-header">
        <div>
          <h2>分类管理</h2>
          <div className="page-subtitle">按大类 → 父类 → 子类逐层维护；支持 Ctrl/Shift 多选、右键操作和 Ctrl+Z 撤销</div>
        </div>
        <div className="header-actions">
          {selectedTotal > 0 && <span className="toolbar-hint">已选 {selectedTotal} 项 · Delete 删除</span>}
          <button className="toolbar-button" disabled={busy} onClick={() => void run('undo')}>撤销上一步</button>
          <button className="toolbar-button" disabled={busy} onClick={() => void onChanged()}>刷新</button>
        </div>
      </div>
      {status && <div className="panel subject-status-bar">{status}</div>}
      <div className="panel subject-delete-behavior">
        <span className="eyebrow">删除行为</span>
        <strong>{settings.deleteBehavior === 'PromoteToParent' ? '删除后提升到上级' : '删除后按关键词规则重新匹配'}</strong>
        <span className="toolbar-hint">右键列表项可删除，或使用 Delete</span>
        <button className="toolbar-button" disabled={busy} onClick={() => void run('subject-set-delete-behavior', { behavior: settings.deleteBehavior === 'PromoteToParent' ? 'MatchRules' : 'PromoteToParent' })}>切换策略</button>
      </div>

      <div className="subject-tree-grid">
        <section className="panel subject-list-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">LEVEL 01</span><h3>大类</h3></div><span className="section-count">{definitions.length}</span></div>
          <div className="subject-list-scroll">
            {definitions.map((item, index) => <button key={item.name} className={`subject-tree-item${item.name === major?.name ? ' current' : ''}${selectedMajors.has(item.name) ? ' selected' : ''}`} onClick={(event) => { setMajorName(item.name); setParentName(''); setChildName(''); selectItem('major', item.name, definitions.map((entry) => entry.name), event); }} onContextMenu={(event) => openContextMenu(event, 'major', item.name, definitions.map((entry) => entry.name))}><span className="tree-index">{String(index + 1).padStart(2, '0')}</span><span><strong>{item.name}</strong><small>{(item.parents?.length ?? 0) + (item.children?.length ?? 0)} 个直接下级</small></span><span className="tree-arrow">→</span></button>)}
          </div>
          <div className="subject-add-row"><input className="search-input" value={majorInput} onChange={(event) => setMajorInput(event.target.value)} placeholder="新增大类，逗号分隔" /><button className="toolbar-button active" disabled={busy} onClick={() => { void runMany(majorInput, 'subject-add-major'); setMajorInput(''); }}>新增</button></div>
        </section>

        <section className="panel subject-list-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">LEVEL 02 · {major?.name ?? '未选择'}</span><h3>父类</h3></div><span className="section-count">{parents.length}</span></div>
          <div className="subject-list-scroll">
            {parents.map((item) => <button key={item.name} className={`subject-tree-item${item.name === parent?.name ? ' current' : ''}${selectedParents.has(item.name) ? ' selected' : ''}`} onClick={(event) => { setParentName(item.name); setChildName(''); selectItem('parent', item.name, parents.map((entry) => entry.name), event); }} onContextMenu={(event) => openContextMenu(event, 'parent', item.name, parents.map((entry) => entry.name))}><span className="tree-branch">└</span><span><strong>{item.name}</strong><small>{item.children?.length ?? 0} 个子类</small></span><span className="tree-arrow">→</span></button>)}
            {parents.length === 0 && <div className="subject-list-empty">选择一个大类，或先新增父类。</div>}
          </div>
          <div className="subject-add-row"><input className="search-input" value={parentInput} onChange={(event) => setParentInput(event.target.value)} placeholder="新增父类，逗号分隔" disabled={!major} /><button className="toolbar-button active" disabled={busy || !major} onClick={() => { void runMany(parentInput, 'subject-add-parent', { major: major?.name }); setParentInput(''); }}>新增</button></div>
        </section>

        <section className="panel subject-list-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">LEVEL 03 · {parent?.name ?? '未选择'}</span><h3>子类</h3></div><span className="section-count">{children.length}</span></div>
          <div className="subject-list-scroll">
            {children.map((child) => <button key={child} className={`subject-tree-item${child === childName ? ' current' : ''}${selectedChildren.has(child) ? ' selected' : ''}`} onClick={(event) => { setChildName(child); selectItem('child', child, children, event); }} onContextMenu={(event) => openContextMenu(event, 'child', child, children)}><span className="tree-branch">└</span><span><strong>{child}</strong><small>{settings.keywordRules[child]?.length ?? 0} 条关键词</small></span><span className="tree-arrow">→</span></button>)}
            {children.length === 0 && <div className="subject-list-empty">选择一个父类，或先新增子类。</div>}
          </div>
          <div className="subject-add-row"><input className="search-input" value={childInput} onChange={(event) => setChildInput(event.target.value)} placeholder="新增子类，逗号分隔" disabled={!parent} /><button className="toolbar-button active" disabled={busy || !parent} onClick={() => { void runMany(childInput, 'subject-add-child', { major: major?.name, parent: parent?.name }); setChildInput(''); }}>新增</button></div>
        </section>
      </div>

      <div className="subject-tool-grid">
        <section className="panel subject-tool-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">CURRENT PATH</span><h3>{subject || '未选择分类'}</h3></div><span className="tree-path">{major?.name ?? '—'} / {parent?.name ?? '—'} / {childName || '—'}</span></div>
          <div className="subject-tool-actions"><button className="toolbar-button" disabled={busy || !major} onClick={() => { const name = major && window.prompt('新的大类名称', major.name); if (name) void run('subject-rename-major', { oldName: major.name, newName: name }); }}>重命名大类</button><button className="toolbar-button danger-button" disabled={busy || !major} onClick={() => { if (major && window.confirm(`删除大类“${major.name}”？`)) void run('subject-remove-major', { name: major.name }); }}>删除大类</button></div>
          {parent && <div className="subject-tool-actions"><button className="toolbar-button" disabled={busy} onClick={() => { const name = window.prompt('新的父类名称', parent.name); if (name) void run('subject-rename-parent', { major: major?.name, oldName: parent.name, newName: name }); }}>重命名父类</button><button className="toolbar-button danger-button" disabled={busy} onClick={() => { if (window.confirm(`删除父类“${parent.name}”？`)) void run('subject-remove-parent', { major: major?.name, name: parent.name, promoteToParent: settings.deleteBehavior === 'PromoteToParent' }); }}>删除父类</button></div>}
          {childName && <div className="subject-tool-actions"><button className="toolbar-button" disabled={busy} onClick={() => { const name = window.prompt('新的子类名称', childName); if (name) void run('subject-rename-child', { major: major?.name, parent: parent?.name, oldName: childName, newName: name }); }}>重命名子类</button><button className="toolbar-button danger-button" disabled={busy} onClick={() => { if (window.confirm(`删除子类“${childName}”？`)) void run('subject-remove-child', { major: major?.name, parent: parent?.name, name: childName, promoteToParent: settings.deleteBehavior === 'PromoteToParent' }); }}>删除子类</button></div>}
        </section>

        <section className="panel subject-tool-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">KEYWORD RULES · {subject || '未选择'}</span><h3>关键词</h3></div><span className="section-count">{rules.length}</span></div>
          <div className="subject-chip-grid">{rules.map((rule) => <button key={rule} className={`subject-rule-chip${selectedRules.has(rule) ? ' selected' : ''}`} onClick={(event) => selectItem('rule', rule, rules, event)} onContextMenu={(event) => openContextMenu(event, 'rule', rule, rules)}>{rule}</button>)}{rules.length === 0 && <span className="subject-list-empty">当前分类暂无关键词规则。</span>}</div>
          <div className="subject-add-row"><input className="search-input" value={keywordInput} onChange={(event) => setKeywordInput(event.target.value)} placeholder="关键词表达式，逗号分隔" disabled={!subject} /><button className="toolbar-button active" disabled={busy || !subject} onClick={() => { void runMany(keywordInput, 'subject-add-keyword', { subject }); setKeywordInput(''); }}>添加</button></div>
        </section>

        <section className="panel subject-tool-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">PARALLEL ACTIVITY</span><h3>进程白名单</h3></div><span className="section-count">{processes.length}</span></div>
          <div className="subject-chip-grid">{processes.map((process) => <button key={process} className={`subject-rule-chip${selectedProcesses.has(process) ? ' selected' : ''}`} onClick={(event) => selectItem('process', process, processes, event)} onContextMenu={(event) => openContextMenu(event, 'process', process, processes)}>{process}</button>)}{processes.length === 0 && <span className="subject-list-empty">暂无并行活动白名单。</span>}</div>
          <div className="subject-add-row"><input className="search-input" value={processInput} onChange={(event) => setProcessInput(event.target.value)} placeholder="进程名，如 Spotify.exe" /><button className="toolbar-button active" disabled={busy} onClick={() => { void runMany(processInput, 'subject-add-whitelist', {}, 'process'); setProcessInput(''); }}>添加</button></div>
        </section>
      </div>

      {contextMenu && <div className="subject-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} onClick={(event) => event.stopPropagation()} onContextMenu={(event) => event.preventDefault()}><strong>已选 {selectedCounts[contextMenu.kind]} 项</strong><button className="toolbar-button danger-button" disabled={busy} onClick={() => { setContextMenu(null); void deleteSelection(); }}>删除已选</button><button className="toolbar-button" disabled={busy} onClick={() => { setContextMenu(null); clearSelections(); }}>取消选择</button></div>}
    </div>
  );
}
