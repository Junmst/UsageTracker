import { useEffect, useMemo, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { api, type SubjectDefinition, type SubjectManagementSnapshot } from '../lib/api';
import { useDataChange } from '../lib/events';

interface Props {
  settings: SubjectManagementSnapshot | null;
  onChanged: () => void;
}

type SelectionKind = 'major' | 'parent' | 'child' | 'rule' | 'process';
type ContextKind = Exclude<SelectionKind, 'rule' | 'process'> | 'rule' | 'process';
type StatusKind = 'info' | 'success' | 'error';

// 单次命令的精确提示文案：进行中 / 成功 / 失败前缀
interface CommandText {
  pending?: string;
  success: string;
  fail: string;
}

// 批量添加的精确提示文案
interface BatchText {
  pending: (items: string[]) => string;
  success: (items: string[]) => string;
  fail: string;
}

const emptySet = () => new Set<string>();

// 撤销操作的精确提示（静态文案，放在组件外避免闭包时序问题）
const UNDO_TEXT: CommandText = { pending: '正在撤销…', success: '撤销完成', fail: '撤销失败' };

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
  const [statusKind, setStatusKind] = useState<StatusKind>('info');
  const [busy, setBusy] = useState(false);
  const [selectedMajors, setSelectedMajors] = useState<Set<string>>(emptySet);
  const [selectedParents, setSelectedParents] = useState<Set<string>>(emptySet);
  const [selectedChildren, setSelectedChildren] = useState<Set<string>>(emptySet);
  const [selectedRules, setSelectedRules] = useState<Set<string>>(emptySet);
  const [selectedProcesses, setSelectedProcesses] = useState<Set<string>>(emptySet);
  const [anchors, setAnchors] = useState<Partial<Record<SelectionKind, string>>>({});
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; kind: ContextKind } | null>(null);

  // 配置在后台被修改（右键分类、其他看板操作等）时自动同步本页，无需手动刷新
  useDataChange(() => onChanged(), true, ['settings']);

  const definitions = settings?.subjectDefinitions ?? [];
  const major = definitions.find((item) => item.name === majorName) ?? definitions[0];
  const parents = major?.parents ?? [];
  const parent = parents.find((item) => item.name === parentName);
  const children = parent?.children ?? [];
  const subject = childName || parentName || major?.name || '';
  // 当前关键词归属的层级名称，用于“正在给 xx大类/父类/子类 …”类精确提示
  const subjectLevelLabel = (): string => (childName ? '子类' : parentName ? '父类' : '大类');
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
        void run('undo', {}, UNDO_TEXT);
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

  const notify = (text: string, kind: StatusKind = 'info') => {
    setStatus(text);
    setStatusKind(kind);
  };

  // 后端返回的 detail 已是完整友好句时直接展示；只有网络/未知错误才套用操作前缀
  const reasonText = (fallback: string, reason: string) =>
    reason.startsWith('请求失败') || reason === '未知错误' ? `${fallback}：${reason}` : reason;

  const run = async (command: string, args: Record<string, unknown> = {}, text?: CommandText) => {
    const fallback = text?.fail ?? '操作失败';
    setBusy(true);
    if (text?.pending) notify(text.pending, 'info');
    try {
      await api.subjectCommand(command, args);
      notify(text?.success ?? '操作已完成', 'success');
      clearSelections();
      await onChanged();
    } catch (error) {
      notify(reasonText(fallback, error instanceof Error ? error.message : '未知错误'), 'error');
    } finally {
      setBusy(false);
    }
  };

  const runMany = async (value: string, command: string, extra: Record<string, unknown> = {}, valueKey = 'name', text?: BatchText) => {
    const values = value.split(/[,，]/).map((item) => item.trim()).filter(Boolean);
    if (values.length === 0) return;
    const fallback = text?.fail ?? '操作失败';
    setBusy(true);
    notify(text ? text.pending(values) : `正在处理 ${values.length} 项…`, 'info');
    // 逐条容错：重复/冲突项不阻断其余项，最后如实汇总
    const succeeded: string[] = [];
    const failReasons: string[] = [];
    for (const item of values) {
      try {
        await api.subjectCommand(command, { ...extra, [valueKey]: item });
        succeeded.push(item);
      } catch (error) {
        failReasons.push(reasonText(fallback, error instanceof Error ? error.message : '未知错误'));
      }
    }
    await onChanged();
    if (failReasons.length === 0) {
      notify(text ? text.success(succeeded) : `已完成 ${succeeded.length} 项`, 'success');
    } else if (succeeded.length === 0) {
      notify(failReasons.join('；'), 'error');
    } else {
      const done = text ? text.success(succeeded) : `已完成 ${succeeded.length} 项`;
      notify(`${done}；${failReasons.length} 项未生效：${failReasons.join('；')}`, 'error');
    }
    setBusy(false);
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
      const count = selectedRules.size;
      if (window.confirm(`删除选中的 ${count} 条关键词？`)) await run('subject-remove-keywords', { subject, keywords: [...selectedRules] }, { pending: `正在删除 ${count} 条关键词…`, success: `已删除 ${count} 条关键词`, fail: '删除关键词失败' });
      return;
    }
    if (selectedChildren.size > 0 && major && parent) {
      const count = selectedChildren.size;
      if (window.confirm(`删除选中的 ${count} 个子类？`)) await run('subject-remove-children', { major: major.name, parent: parent.name, names: [...selectedChildren], promoteToParent: settings.deleteBehavior === 'PromoteToParent' }, { pending: `正在删除 ${count} 个子类…`, success: `已删除 ${count} 个子类`, fail: '删除子类失败' });
      return;
    }
    if (selectedParents.size > 0 && major) {
      const count = selectedParents.size;
      if (window.confirm(`删除选中的 ${count} 个父类？`)) await run('subject-remove-parents', { major: major.name, names: [...selectedParents], promoteToParent: settings.deleteBehavior === 'PromoteToParent' }, { pending: `正在删除 ${count} 个父类…`, success: `已删除 ${count} 个父类`, fail: '删除父类失败' });
      return;
    }
    if (selectedMajors.size > 0) {
      const count = selectedMajors.size;
      if (window.confirm(`删除选中的 ${count} 个大类？`)) await run('subject-remove-majors', { names: [...selectedMajors] }, { pending: `正在删除 ${count} 个大类…`, success: `已删除 ${count} 个大类`, fail: '删除大类失败' });
      return;
    }
    if (selectedProcesses.size > 0) {
      const count = selectedProcesses.size;
      if (window.confirm(`删除选中的 ${count} 个白名单进程？`)) await run('subject-remove-whitelist-processes', { processes: [...selectedProcesses] }, { pending: `正在删除 ${count} 个白名单进程…`, success: `已删除 ${count} 个白名单进程`, fail: '删除白名单进程失败' });
    }
  };

  const selectedTotal = Object.values(selectedCounts).reduce((sum, count) => sum + count, 0);

  // ── 各操作的精确提示文案 ──
  const majorBatchText: BatchText = {
    pending: (items) => items.length > 1 ? `正在添加 ${items.length} 个大类…` : `正在添加大类「${items[0]}」…`,
    success: (items) => items.length > 1 ? `已添加 ${items.length} 个大类` : `已添加大类「${items[0]}」`,
    fail: '添加大类失败',
  };
  const parentBatchText: BatchText = {
    pending: (items) => `正在给大类「${major?.name ?? ''}」添加${items.length > 1 ? ` ${items.length} 个父类` : `父类「${items[0]}」`}…`,
    success: (items) => `已给大类「${major?.name ?? ''}」添加${items.length > 1 ? ` ${items.length} 个父类` : `父类「${items[0]}」`}`,
    fail: '添加父类失败',
  };
  const childBatchText: BatchText = {
    pending: (items) => `正在给父类「${parent?.name ?? ''}」添加${items.length > 1 ? ` ${items.length} 个子类` : `子类「${items[0]}」`}…`,
    success: (items) => `已给父类「${parent?.name ?? ''}」添加${items.length > 1 ? ` ${items.length} 个子类` : `子类「${items[0]}」`}`,
    fail: '添加子类失败',
  };
  const keywordBatchText: BatchText = {
    pending: (items) => `正在给${subjectLevelLabel()}「${subject}」添加${items.length > 1 ? ` ${items.length} 条关键词` : `关键词「${items[0]}」`}…`,
    success: (items) => `已给${subjectLevelLabel()}「${subject}」添加${items.length > 1 ? ` ${items.length} 条关键词` : `关键词「${items[0]}」`}`,
    fail: '添加关键词失败',
  };
  const processBatchText: BatchText = {
    pending: (items) => items.length > 1 ? `正在添加 ${items.length} 个白名单进程…` : `正在添加白名单进程「${items[0]}」…`,
    success: (items) => items.length > 1 ? `已添加 ${items.length} 个白名单进程` : `已添加白名单进程「${items[0]}」`,
    fail: '添加白名单进程失败',
  };

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
          <button className="toolbar-button" disabled={busy} onClick={() => void run('undo', {}, UNDO_TEXT)}>撤销上一步</button>
          <button className="toolbar-button" disabled={busy} onClick={() => void onChanged()}>刷新</button>
        </div>
      </div>
      {status && <div className={`panel subject-status-bar status-${statusKind}`}>{status}</div>}
      <div className="panel subject-delete-behavior">
        <span className="eyebrow">删除行为</span>
        <strong>{settings.deleteBehavior === 'PromoteToParent' ? '删除后提升到上级' : '删除后按关键词规则重新匹配'}</strong>
        <span className="toolbar-hint">右键列表项可删除，或使用 Delete</span>
        <button className="toolbar-button" disabled={busy} onClick={() => void run('subject-set-delete-behavior', { behavior: settings.deleteBehavior === 'PromoteToParent' ? 'MatchRules' : 'PromoteToParent' }, { pending: '正在切换删除策略…', success: settings.deleteBehavior === 'PromoteToParent' ? '已切换为「删除后按关键词规则重新匹配」' : '已切换为「删除后提升到上级」', fail: '切换删除策略失败' })}>切换策略</button>
      </div>

      <div className="subject-tree-grid">
        <section className="panel subject-list-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">LEVEL 01</span><h3>大类</h3></div><span className="section-count">{definitions.length}</span></div>
          <div className="subject-list-scroll">
            {definitions.map((item, index) => <button key={item.name} className={`subject-tree-item${item.name === major?.name ? ' current' : ''}${selectedMajors.has(item.name) ? ' selected' : ''}`} onClick={(event) => { setMajorName(item.name); setParentName(''); setChildName(''); selectItem('major', item.name, definitions.map((entry) => entry.name), event); }} onContextMenu={(event) => openContextMenu(event, 'major', item.name, definitions.map((entry) => entry.name))}><span className="tree-index">{String(index + 1).padStart(2, '0')}</span><span><strong>{item.name}</strong><small>{(item.parents?.length ?? 0) + (item.children?.length ?? 0)} 个直接下级</small></span><span className="tree-arrow">→</span></button>)}
          </div>
          <div className="subject-add-row"><input className="search-input" value={majorInput} onChange={(event) => setMajorInput(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !busy) { void runMany(majorInput, 'subject-add-major', {}, 'name', majorBatchText); setMajorInput(''); } }} placeholder="新增大类，逗号分隔" /><button className="toolbar-button active" disabled={busy} onClick={() => { void runMany(majorInput, 'subject-add-major', {}, 'name', majorBatchText); setMajorInput(''); }}>新增</button></div>
        </section>

        <section className="panel subject-list-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">LEVEL 02 · {major?.name ?? '未选择'}</span><h3>父类</h3></div><span className="section-count">{parents.length}</span></div>
          <div className="subject-list-scroll">
            {parents.map((item) => <button key={item.name} className={`subject-tree-item${item.name === parent?.name ? ' current' : ''}${selectedParents.has(item.name) ? ' selected' : ''}`} onClick={(event) => { setParentName(item.name); setChildName(''); selectItem('parent', item.name, parents.map((entry) => entry.name), event); }} onContextMenu={(event) => openContextMenu(event, 'parent', item.name, parents.map((entry) => entry.name))}><span className="tree-branch">└</span><span><strong>{item.name}</strong><small>{item.children?.length ?? 0} 个子类</small></span><span className="tree-arrow">→</span></button>)}
            {parents.length === 0 && <div className="subject-list-empty">选择一个大类，或先新增父类。</div>}
          </div>
          <div className="subject-add-row"><input className="search-input" value={parentInput} onChange={(event) => setParentInput(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !busy && major) { void runMany(parentInput, 'subject-add-parent', { major: major?.name }, 'name', parentBatchText); setParentInput(''); } }} placeholder="新增父类，逗号分隔" disabled={!major} /><button className="toolbar-button active" disabled={busy || !major} onClick={() => { void runMany(parentInput, 'subject-add-parent', { major: major?.name }, 'name', parentBatchText); setParentInput(''); }}>新增</button></div>
        </section>

        <section className="panel subject-list-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">LEVEL 03 · {parent?.name ?? '未选择'}</span><h3>子类</h3></div><span className="section-count">{children.length}</span></div>
          <div className="subject-list-scroll">
            {children.map((child) => <button key={child} className={`subject-tree-item${child === childName ? ' current' : ''}${selectedChildren.has(child) ? ' selected' : ''}`} onClick={(event) => { setChildName(child); selectItem('child', child, children, event); }} onContextMenu={(event) => openContextMenu(event, 'child', child, children)}><span className="tree-branch">└</span><span><strong>{child}</strong><small>{settings.keywordRules[child]?.length ?? 0} 条关键词</small></span><span className="tree-arrow">→</span></button>)}
            {children.length === 0 && <div className="subject-list-empty">选择一个父类，或先新增子类。</div>}
          </div>
          <div className="subject-add-row"><input className="search-input" value={childInput} onChange={(event) => setChildInput(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !busy && parent) { void runMany(childInput, 'subject-add-child', { major: major?.name, parent: parent?.name }, 'name', childBatchText); setChildInput(''); } }} placeholder="新增子类，逗号分隔" disabled={!parent} /><button className="toolbar-button active" disabled={busy || !parent} onClick={() => { void runMany(childInput, 'subject-add-child', { major: major?.name, parent: parent?.name }, 'name', childBatchText); setChildInput(''); }}>新增</button></div>
        </section>
      </div>

      <div className="subject-tool-grid">
        <section className="panel subject-tool-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">CURRENT PATH</span><h3>{subject || '未选择分类'}</h3></div><span className="tree-path">{major?.name ?? '—'} / {parent?.name ?? '—'} / {childName || '—'}</span></div>
          <div className="subject-tool-actions"><button className="toolbar-button" disabled={busy || !major} onClick={() => { const name = major && window.prompt('新的大类名称', major.name); if (name) void run('subject-rename-major', { oldName: major.name, newName: name }, { pending: `正在将大类「${major.name}」重命名为「${name}」…`, success: `已将大类「${major.name}」重命名为「${name}」`, fail: '重命名大类失败' }); }}>重命名大类</button><button className="toolbar-button danger-button" disabled={busy || !major} onClick={() => { if (major && window.confirm(`删除大类“${major.name}”？`)) void run('subject-remove-major', { name: major.name }, { pending: `正在删除大类「${major.name}」…`, success: `已删除大类「${major.name}」`, fail: '删除大类失败' }); }}>删除大类</button></div>
          {parent && <div className="subject-tool-actions"><button className="toolbar-button" disabled={busy} onClick={() => { const name = window.prompt('新的父类名称', parent.name); if (name) void run('subject-rename-parent', { major: major?.name, oldName: parent.name, newName: name }, { pending: `正在将父类「${parent.name}」重命名为「${name}」…`, success: `已将父类「${parent.name}」重命名为「${name}」`, fail: '重命名父类失败' }); }}>重命名父类</button><button className="toolbar-button danger-button" disabled={busy} onClick={() => { if (window.confirm(`删除父类“${parent.name}”？`)) void run('subject-remove-parent', { major: major?.name, name: parent.name, promoteToParent: settings.deleteBehavior === 'PromoteToParent' }, { pending: `正在删除父类「${parent.name}」…`, success: `已删除父类「${parent.name}」`, fail: '删除父类失败' }); }}>删除父类</button></div>}
          {childName && <div className="subject-tool-actions"><button className="toolbar-button" disabled={busy} onClick={() => { const name = window.prompt('新的子类名称', childName); if (name) void run('subject-rename-child', { major: major?.name, parent: parent?.name, oldName: childName, newName: name }, { pending: `正在将子类「${childName}」重命名为「${name}」…`, success: `已将子类「${childName}」重命名为「${name}」`, fail: '重命名子类失败' }); }}>重命名子类</button><button className="toolbar-button danger-button" disabled={busy} onClick={() => { if (window.confirm(`删除子类“${childName}”？`)) void run('subject-remove-child', { major: major?.name, parent: parent?.name, name: childName, promoteToParent: settings.deleteBehavior === 'PromoteToParent' }, { pending: `正在删除子类「${childName}」…`, success: `已删除子类「${childName}」`, fail: '删除子类失败' }); }}>删除子类</button></div>}
        </section>

        <section className="panel subject-tool-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">KEYWORD RULES · {subject || '未选择'}</span><h3>关键词</h3></div><span className="section-count">{rules.length}</span></div>
          <div className="subject-chip-grid">{rules.map((rule) => <button key={rule} className={`subject-rule-chip${selectedRules.has(rule) ? ' selected' : ''}`} onClick={(event) => selectItem('rule', rule, rules, event)} onContextMenu={(event) => openContextMenu(event, 'rule', rule, rules)}>{rule}</button>)}{rules.length === 0 && <span className="subject-list-empty">当前分类暂无关键词规则。</span>}</div>
          <div className="subject-add-row"><input className="search-input" value={keywordInput} onChange={(event) => setKeywordInput(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !busy && subject) { void runMany(keywordInput, 'subject-add-keyword', { subject }, 'keyword', keywordBatchText); setKeywordInput(''); } }} placeholder="关键词表达式，逗号分隔；支持 + 或、* 与、- 剔除、() 分组" disabled={!subject} /><button className="toolbar-button active" disabled={busy || !subject} onClick={() => { void runMany(keywordInput, 'subject-add-keyword', { subject }, 'keyword', keywordBatchText); setKeywordInput(''); }}>添加</button></div>
        </section>

        <section className="panel subject-tool-panel">
          <div className="subject-list-heading"><div><span className="eyebrow">PARALLEL ACTIVITY</span><h3>进程白名单</h3></div><span className="section-count">{processes.length}</span></div>
          <div className="subject-chip-grid">{processes.map((process) => <button key={process} className={`subject-rule-chip${selectedProcesses.has(process) ? ' selected' : ''}`} onClick={(event) => selectItem('process', process, processes, event)} onContextMenu={(event) => openContextMenu(event, 'process', process, processes)}>{process}</button>)}{processes.length === 0 && <span className="subject-list-empty">暂无并行活动白名单。</span>}</div>
          <div className="subject-add-row"><input className="search-input" value={processInput} onChange={(event) => setProcessInput(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !busy) { void runMany(processInput, 'subject-add-whitelist', {}, 'process', processBatchText); setProcessInput(''); } }} placeholder="进程名，如 Spotify.exe" /><button className="toolbar-button active" disabled={busy} onClick={() => { void runMany(processInput, 'subject-add-whitelist', {}, 'process', processBatchText); setProcessInput(''); }}>添加</button></div>
        </section>
      </div>

      {contextMenu && <div className="subject-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} onClick={(event) => event.stopPropagation()} onContextMenu={(event) => event.preventDefault()}><strong>已选 {selectedCounts[contextMenu.kind]} 项</strong><button className="toolbar-button danger-button" disabled={busy} onClick={() => { setContextMenu(null); void deleteSelection(); }}>删除已选</button><button className="toolbar-button" disabled={busy} onClick={() => { setContextMenu(null); clearSelections(); }}>取消选择</button></div>}
    </div>
  );
}
