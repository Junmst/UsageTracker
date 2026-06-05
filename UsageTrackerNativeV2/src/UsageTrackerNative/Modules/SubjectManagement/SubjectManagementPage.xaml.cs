namespace UsageTrackerNative.Modules.SubjectManagement;

public partial class SubjectManagementPage : System.Windows.Controls.UserControl
{
    private readonly Shell.V2AppContext _context;
    private List<SubjectDefinition> _definitions = [];
    private SubjectDefinition? _selectedMajor;
    private SubjectParentDefinition? _selectedParent;
    private string? _selectedChild;
    private string? _selectedKeyword;
    private string? _selectedParallelWhitelistProcess;
    private readonly HashSet<string> _selectedMajorKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedParentKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedChildKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedKeywordKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedParallelWhitelistProcessKeys = new(StringComparer.OrdinalIgnoreCase);
    private int _lastMajorIndex = -1;
    private int _lastParentIndex = -1;
    private int _lastChildIndex = -1;
    private int _lastKeywordIndex = -1;
    private int _lastParallelWhitelistProcessIndex = -1;
    private bool _isDeleteBehaviorFlyoutOpen;
    private readonly System.Windows.Threading.DispatcherTimer _deleteBehaviorFlyoutCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public SubjectManagementPage(Shell.V2AppContext context)
    {
        _context = context;
        InitializeComponent();
        Loaded += SubjectManagementPage_Loaded;
        Unloaded += SubjectManagementPage_Unloaded;
        _context.Initialized += Context_Initialized;
        _context.TrackerService.SessionChanged += TrackerService_SessionChanged;
        _context.UndoRequested += Context_UndoRequested;
        _deleteBehaviorFlyoutCloseTimer.Tick += DeleteBehaviorFlyoutCloseTimer_Tick;
    }

    private void SubjectManagementPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _context.Initialized -= Context_Initialized;
        _context.TrackerService.SessionChanged -= TrackerService_SessionChanged;
        _context.UndoRequested -= Context_UndoRequested;
        _deleteBehaviorFlyoutCloseTimer.Stop();
        _deleteBehaviorFlyoutCloseTimer.Tick -= DeleteBehaviorFlyoutCloseTimer_Tick;
    }

    private void Context_UndoRequested(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        if (_context.TryUndo())
        {
            RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
        }
    }

    private void TrackerService_SessionChanged(object? sender, SessionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => TrackerService_SessionChanged(sender, e));
            return;
        }

        // 会话计时每秒都会触发 SessionChanged。分类页只需要在分类配置变化后刷新，
        // 如果用户正在输入分类/关键词，自动重绘会抢走 TextBox 焦点，导致“只能输入一瞬间”。
        if (IsSubjectInputFocused())
        {
            return;
        }

        RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
    }

    private async void SubjectManagementPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await EnsureInitializedAndRefreshAsync();
    }

    private async void Context_Initialized(object? sender, EventArgs e)
    {
        await Dispatcher.InvokeAsync(async () => await EnsureInitializedAndRefreshAsync());
    }

    private async Task EnsureInitializedAndRefreshAsync()
    {
        try
        {
            await _context.Initialization;
        }
        catch
        {
            // 初始化错误由全局加载状态展示；分类页保持空列表。
        }

        RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
    }

    private void RefreshDefinitions(bool preserveSelection = true, bool preferFirstSelection = false)
    {
        var hasExplicitMajorSelection = _selectedMajorKeys.Count > 0;
        var hasExplicitParentSelection = _selectedParentKeys.Count > 0;
        var hasExplicitChildSelection = _selectedChildKeys.Count > 0;
        var majorName = preserveSelection && hasExplicitMajorSelection ? _selectedMajor?.Name : null;
        var parentName = preserveSelection && hasExplicitParentSelection ? _selectedParent?.Name : null;
        var childName = preserveSelection && hasExplicitChildSelection ? _selectedChild : null;
        _definitions = _context.TrackerService.GetSubjectDefinitions().ToList();

        _selectedMajor = !string.IsNullOrWhiteSpace(majorName)
            ? _definitions.FirstOrDefault(x => string.Equals(x.Name, majorName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (_selectedMajor is null && preferFirstSelection)
        {
            _selectedMajor = _definitions.FirstOrDefault();
        }

        _selectedParent = _selectedMajor is not null && !string.IsNullOrWhiteSpace(parentName)
            ? _selectedMajor.Parents.FirstOrDefault(x => string.Equals(x.Name, parentName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (_selectedParent is null && preferFirstSelection)
        {
            _selectedParent = _selectedMajor?.Parents.FirstOrDefault();
        }

        _selectedChild = _selectedParent is not null && !string.IsNullOrWhiteSpace(childName)
            ? _selectedParent.Children.FirstOrDefault(x => string.Equals(x, childName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (_selectedChild is null && preferFirstSelection)
        {
            _selectedChild = _selectedParent?.Children.FirstOrDefault();
        }

        SyncSelectionSetsWithCurrentDefinitions();
        RenderAll();
    }

    private void RenderAll()
    {
        Focusable = true;
        RenderMajorPanel();
        RenderParentPanel();
        RenderChildPanel();
        RenderDetails();
        RenderKeywords();
        RenderParallelWhitelist();
        UpdateDeleteBehaviorButton();
    }

    private void RenderMajorPanel()
    {
        MajorPanel.Children.Clear();
        for (var index = 0; index < _definitions.Count; index++)
        {
            var currentIndex = index;
            var major = _definitions[currentIndex];
            MajorPanel.Children.Add(CreateItem(major.Name, $"{major.Parents.Count} 父类", _selectedMajorKeys.Contains(major.Name), () => SelectMajor(major, currentIndex)));
        }
    }

    private void RenderParentPanel()
    {
        ParentPanel.Children.Clear();
        if (_selectedMajor is null) return;
        for (var index = 0; index < _selectedMajor.Parents.Count; index++)
        {
            var currentIndex = index;
            var parent = _selectedMajor.Parents[currentIndex];
            ParentPanel.Children.Add(CreateItem(parent.Name, $"{parent.Children.Count} 子类", _selectedParentKeys.Contains(parent.Name), () => SelectParent(parent, currentIndex)));
        }
    }

    private void RenderChildPanel()
    {
        ChildPanel.Children.Clear();
        if (_selectedParent is null) return;
        for (var index = 0; index < _selectedParent.Children.Count; index++)
        {
            var currentIndex = index;
            var child = _selectedParent.Children[currentIndex];
            var keywordCount = _context.TrackerService.GetSubjectKeywordRules(child).Count;
            ChildPanel.Children.Add(CreateItem(child, $"{keywordCount} 规则", _selectedChildKeys.Contains(child), () => SelectChild(child, currentIndex)));
        }
    }

    private void RenderDetails()
    {
        var selected = GetSelectedSubjectName();
        var level = _selectedMajor is null ? "未选择" : _selectedParent is null ? _selectedMajor.Name : _selectedChild is null ? $"{_selectedMajor.Name} / {_selectedParent.Name}" : $"{_selectedMajor.Name} / {_selectedParent.Name} / {_selectedChild}";
        CurrentPathText.Text = $"当前路径：{level}";
        SelectedNameText.Text = $"名称：{selected ?? "未选择"}";
        SelectedLevelText.Text = $"层级：{level}";
    }

    private void SelectMajor(SubjectDefinition major, int index)
    {
        ApplySelection(_selectedMajorKeys, _definitions.Select(x => x.Name).ToList(), major.Name, index, ref _lastMajorIndex);
        _selectedMajor = _selectedMajorKeys.Contains(major.Name) ? major : _definitions.FirstOrDefault(x => _selectedMajorKeys.Contains(x.Name));
        _selectedParent = null;
        _selectedChild = null;
        _selectedParentKeys.Clear();
        _selectedChildKeys.Clear();
        _selectedKeywordKeys.Clear();
        _selectedKeyword = null;
        RenderAll();
    }

    private void SelectParent(SubjectParentDefinition parent, int index)
    {
        if (_selectedMajor is null) return;
        ApplySelection(_selectedParentKeys, _selectedMajor.Parents.Select(x => x.Name).ToList(), parent.Name, index, ref _lastParentIndex);
        _selectedParent = _selectedParentKeys.Contains(parent.Name) ? parent : _selectedMajor.Parents.FirstOrDefault(x => _selectedParentKeys.Contains(x.Name));
        _selectedChild = null;
        _selectedChildKeys.Clear();
        _selectedKeywordKeys.Clear();
        _selectedKeyword = null;
        RenderAll();
    }

    private void SelectChild(string child, int index)
    {
        if (_selectedParent is null) return;
        ApplySelection(_selectedChildKeys, _selectedParent.Children.ToList(), child, index, ref _lastChildIndex);
        _selectedChild = _selectedChildKeys.Contains(child) ? child : _selectedParent.Children.FirstOrDefault(x => _selectedChildKeys.Contains(x));
        _selectedKeywordKeys.Clear();
        _selectedKeyword = null;
        RenderAll();
    }

    private void SelectKeyword(string keyword, int index)
    {
        var selected = GetSelectedSubjectName();
        if (string.IsNullOrWhiteSpace(selected)) return;
        var keywords = _context.TrackerService.GetSubjectKeywordRules(selected).ToList();
        ApplySelection(_selectedKeywordKeys, keywords, keyword, index, ref _lastKeywordIndex);
        _selectedKeyword = _selectedKeywordKeys.Contains(keyword) ? keyword : keywords.ToList().FirstOrDefault(x => _selectedKeywordKeys.Contains(x));
        RenderKeywords();
    }

    private static void ApplySelection(HashSet<string> selectedKeys, IReadOnlyList<string> orderedKeys, string key, int index, ref int lastIndex)
    {
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 && lastIndex >= 0 && orderedKeys.Count > 0)
        {
            var start = Math.Min(lastIndex, index);
            var end = Math.Max(lastIndex, index);
            if ((modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
            {
                selectedKeys.Clear();
            }
            for (var i = start; i <= end; i++)
            {
                selectedKeys.Add(orderedKeys[i]);
            }
        }
        else if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            if (!selectedKeys.Add(key))
            {
                selectedKeys.Remove(key);
            }
            lastIndex = index;
        }
        else
        {
            if (selectedKeys.Count == 1 && selectedKeys.Contains(key))
            {
                selectedKeys.Clear();
            }
            else
            {
                selectedKeys.Clear();
                selectedKeys.Add(key);
            }
            lastIndex = index;
        }
    }

    private void SyncSelectionSetsWithCurrentDefinitions()
    {
        SyncSet(_selectedMajorKeys, _definitions.Select(x => x.Name));
        if (_selectedMajor is not null)
        {
            SyncSet(_selectedParentKeys, _selectedMajor.Parents.Select(x => x.Name));
        }
        else
        {
            _selectedParentKeys.Clear();
        }

        if (_selectedParent is not null)
        {
            SyncSet(_selectedChildKeys, _selectedParent.Children);
        }
        else
        {
            _selectedChildKeys.Clear();
        }

        var selected = GetSelectedSubjectName();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            SyncSet(_selectedKeywordKeys, _context.TrackerService.GetSubjectKeywordRules(selected));
        }
        else
        {
            _selectedKeywordKeys.Clear();
        }
    }

    private static void SyncSet(HashSet<string> selectedKeys, IEnumerable<string> validKeys)
    {
        var valid = validKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        selectedKeys.RemoveWhere(x => !valid.Contains(x));
    }

    private bool HasAnySelection()
    {
        return _selectedMajorKeys.Count > 0 || _selectedParentKeys.Count > 0 || _selectedChildKeys.Count > 0 || _selectedKeywordKeys.Count > 0 || _selectedParallelWhitelistProcessKeys.Count > 0;
    }

    private void SetStatus(string message, bool isError = false)
    {
        SubjectStatusText.Text = message;
        SubjectStatusText.Foreground = isError
            ? System.Windows.Media.Brushes.OrangeRed
            : (System.Windows.Application.Current.Resources["SecondaryTextBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray);
    }

    private void RenderKeywords()
    {
        KeywordPanel.Children.Clear();
        var selected = GetSelectedSubjectName();
        if (string.IsNullOrWhiteSpace(selected)) return;
        var keywords = _context.TrackerService.GetSubjectKeywordRules(selected);
        if (!keywords.Any(x => string.Equals(x, _selectedKeyword, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedKeyword = keywords.FirstOrDefault();
        }

        for (var index = 0; index < keywords.Count; index++)
        {
            var currentIndex = index;
            var keyword = keywords[currentIndex];
            KeywordPanel.Children.Add(CreateItem(keyword, "关键词", _selectedKeywordKeys.Contains(keyword), () => SelectKeyword(keyword, currentIndex)));
        }
    }

    private void RenderParallelWhitelist()
    {
        ParallelWhitelistPanel.Children.Clear();
        var processes = _context.TrackerService.ParallelActivityWhitelistProcesses.ToList();
        ParallelWhitelistCountText.Text = $"{processes.Count} 个";
        if (!processes.Any(x => string.Equals(x, _selectedParallelWhitelistProcess, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedParallelWhitelistProcess = processes.FirstOrDefault();
        }
        _selectedParallelWhitelistProcessKeys.RemoveWhere(x => !processes.Contains(x, StringComparer.OrdinalIgnoreCase));

        for (var index = 0; index < processes.Count; index++)
        {
            var currentIndex = index;
            var process = processes[currentIndex];
            ParallelWhitelistPanel.Children.Add(CreateItem(process, "并行", _selectedParallelWhitelistProcessKeys.Contains(process), () => SelectParallelWhitelistProcess(process, currentIndex)));
        }
    }

    private void SelectParallelWhitelistProcess(string process, int index)
    {
        var processes = _context.TrackerService.ParallelActivityWhitelistProcesses.ToList();
        ApplySelection(_selectedParallelWhitelistProcessKeys, processes, process, index, ref _lastParallelWhitelistProcessIndex);
        _selectedParallelWhitelistProcess = _selectedParallelWhitelistProcessKeys.Contains(process) ? process : processes.FirstOrDefault(x => _selectedParallelWhitelistProcessKeys.Contains(x));
        RenderParallelWhitelist();
    }

    private System.Windows.Controls.Border CreateItem(string name, string meta, bool selected, Action click)
    {
        var border = new System.Windows.Controls.Border
        {
            Style = (System.Windows.Style)FindResource("CategoryItemStyle"),
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(selected ? "MenuSelectedBrush" : "InputBackgroundBrush"),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
        var title = new System.Windows.Controls.TextBlock { Text = name, VerticalAlignment = System.Windows.VerticalAlignment.Center, FontWeight = System.Windows.FontWeights.Black };
        var metaText = new System.Windows.Controls.TextBlock { Text = meta, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Center, FontWeight = System.Windows.FontWeights.Black };
        if (selected)
        {
            title.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentTextBrush");
            metaText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentTextBrush");
        }
        System.Windows.Controls.Grid.SetColumn(metaText, 1);
        grid.Children.Add(title);
        grid.Children.Add(metaText);
        border.Child = grid;
        border.MouseLeftButtonDown += (_, _) =>
        {
            click();
            Focus();
        };
        return border;
    }

    private string? GetSelectedSubjectName()
    {
        if (_selectedChild is not null)
        {
            return _selectedChild;
        }

        if (_selectedParent is not null)
        {
            return _selectedParent.Name;
        }

        return _selectedMajor?.Name;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
            && e.Key == System.Windows.Input.Key.Z)
        {
            if (_context.TryUndo())
            {
                RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back)
        {
            if (KeyboardFocusIsTextEditing()) return;
            if (HasAnySelection())
            {
                DeleteSelectedItems();
            }
            else
            {
                DeleteSubjectButton_Click(this, e);
            }
            e.Handled = true;
        }
    }

    private static bool KeyboardFocusIsTextEditing()
    {
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        return focused is System.Windows.Controls.TextBox
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.Primitives.TextBoxBase;
    }

    private bool IsSubjectInputFocused()
    {
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        return ReferenceEquals(focused, MajorInput)
            || ReferenceEquals(focused, ParentInput)
            || ReferenceEquals(focused, ChildInput)
            || ReferenceEquals(focused, KeywordInput)
            || ReferenceEquals(focused, ParallelWhitelistInput);
    }

    private void MajorInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            AddMajorButton_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back) && IsTextBoxDeleteShortcut(e))
        {
            DeleteSubjectButton_Click(sender, e);
            e.Handled = true;
        }
    }
    private void ParentInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            AddParentButton_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back) && IsTextBoxDeleteShortcut(e))
        {
            DeleteSubjectButton_Click(sender, e);
            e.Handled = true;
        }
    }
    private void ChildInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            AddChildButton_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back) && IsTextBoxDeleteShortcut(e))
        {
            DeleteSubjectButton_Click(sender, e);
            e.Handled = true;
        }
    }
    private void KeywordInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            AddKeywordButton_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back) && IsTextBoxDeleteShortcut(e))
        {
            DeleteLastKeywordButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ParallelWhitelistInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            AddParallelWhitelistButton_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back) && IsTextBoxDeleteShortcut(e))
        {
            DeleteSelectedParallelWhitelistButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private static bool IsTextBoxDeleteShortcut(System.Windows.Input.KeyEventArgs e)
    {
        return (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
    }

    private void DetailsPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back)
        {
            if (KeyboardFocusIsTextEditing()) return;
            DeleteSelectedItems();
            e.Handled = true;
        }
    }

    private void KeywordPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back)
        {
            if (KeyboardFocusIsTextEditing()) return;
            DeleteSelectedKeywords();
            e.Handled = true;
        }
    }

    private void ParallelWhitelistPanel_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back)
        {
            if (KeyboardFocusIsTextEditing()) return;
            DeleteSelectedParallelWhitelistButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AddMajorButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var names = SplitInputValues(MajorInput.Text).ToList();
        if (names.Count == 0)
        {
            SetStatus("请输入大类名称。", isError: true);
            return;
        }
        var addedNames = new List<string>();
        var failedNames = new List<string>();
        foreach (var name in names)
        {
            if (_context.TrackerService.AddSubject(name))
            {
                addedNames.Add(name);
            }
            else
            {
                failedNames.Add(name);
            }
        }
        if (addedNames.Count > 0)
        {
            MajorInput.Clear();
            _selectedMajor = new SubjectDefinition { Name = addedNames.Last() };
            _selectedParent = null;
            _selectedChild = null;
            RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
            var message = $"已新增大类：{string.Join("、", addedNames)}。";
            if (failedNames.Count > 0)
            {
                message += $" 未添加：{string.Join("、", failedNames)}。";
            }
            SetStatus(message, isError: failedNames.Count > 0);
        }
        else
        {
            SetStatus($"新增失败：{string.Join("、", failedNames)} 已存在或不可用。", isError: true);
        }
    }

    private void AddParentButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedMajor is null)
        {
            SetStatus("请先选择一个大类。", isError: true);
            return;
        }
        var names = SplitInputValues(ParentInput.Text).ToList();
        if (names.Count == 0)
        {
            SetStatus("请输入父类名称。", isError: true);
            return;
        }
        var addedNames = new List<string>();
        var failedNames = new List<string>();
        foreach (var name in names)
        {
            if (_context.TrackerService.AddChildSubject(_selectedMajor.Name, name))
            {
                addedNames.Add(name);
            }
            else
            {
                failedNames.Add(name);
            }
        }
        if (addedNames.Count > 0)
        {
            ParentInput.Clear();
            _selectedParent = new SubjectParentDefinition { Name = addedNames.Last() };
            _selectedChild = null;
            RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
            var message = $"已在“{_selectedMajor?.Name ?? "当前大类"}”下新增父类：{string.Join("、", addedNames)}。";
            if (failedNames.Count > 0)
            {
                message += $" 未添加：{string.Join("、", failedNames)}。";
            }
            SetStatus(message, isError: failedNames.Count > 0);
        }
        else
        {
            SetStatus($"新增失败：{string.Join("、", failedNames)} 已存在或不可用。", isError: true);
        }
    }

    private void AddChildButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedMajor is null || _selectedParent is null)
        {
            SetStatus("请先选择大类和父类。", isError: true);
            return;
        }
        var names = SplitInputValues(ChildInput.Text).ToList();
        if (names.Count == 0)
        {
            SetStatus("请输入子类名称。", isError: true);
            return;
        }
        var addedNames = new List<string>();
        var failedNames = new List<string>();
        foreach (var name in names)
        {
            if (_context.TrackerService.AddGrandChildSubject(_selectedMajor.Name, _selectedParent.Name, name))
            {
                addedNames.Add(name);
            }
            else
            {
                failedNames.Add(name);
            }
        }
        if (addedNames.Count > 0)
        {
            ChildInput.Clear();
            _selectedChild = addedNames.Last();
            RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
            var message = $"已在“{_selectedParent?.Name ?? "当前父类"}”下新增子类：{string.Join("、", addedNames)}。";
            if (failedNames.Count > 0)
            {
                message += $" 未添加：{string.Join("、", failedNames)}。";
            }
            SetStatus(message, isError: failedNames.Count > 0);
        }
        else
        {
            SetStatus($"新增失败：{string.Join("、", failedNames)} 已存在或不可用。", isError: true);
        }
    }

    private void AddKeywordButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = GetSelectedSubjectName();
        if (string.IsNullOrWhiteSpace(selected))
        {
            SetStatus("请先选择一个分类。", isError: true);
            return;
        }
        var keywords = SplitInputValues(KeywordInput.Text).ToList();
        if (keywords.Count == 0)
        {
            SetStatus("请输入关键词规则。", isError: true);
            return;
        }
        var addedKeywords = new List<string>();
        var failedKeywords = new List<string>();
        foreach (var keyword in keywords)
        {
            if (_context.TrackerService.AddSubjectKeywordRule(selected, keyword))
            {
                addedKeywords.Add(keyword);
            }
            else
            {
                failedKeywords.Add(keyword);
            }
        }
        if (addedKeywords.Count > 0)
        {
            var undoSubject = selected;
            var undoKeywords = addedKeywords.ToList();
            _context.RegisterUndo(() =>
            {
                foreach (var keyword in undoKeywords)
                {
                    _context.TrackerService.RemoveSubjectKeywordRule(undoSubject, keyword);
                }
            });

            KeywordInput.Clear();
            _selectedKeyword = addedKeywords.Last();
            RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
            var message = $"已给“{selected}”添加关键词：{string.Join("、", addedKeywords)}。";
            if (failedKeywords.Count > 0)
            {
                message += $" 未添加：{string.Join("、", failedKeywords)}。";
            }
            SetStatus(message, isError: failedKeywords.Count > 0);
        }
        else
        {
            SetStatus($"添加失败：{string.Join("、", failedKeywords)} 可能已存在。", isError: true);
        }
    }

    private void AddParallelWhitelistButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var processes = SplitInputValues(ParallelWhitelistInput.Text).ToList();
        if (processes.Count == 0)
        {
            SetStatus("请输入并行白名单进程名。", isError: true);
            return;
        }

        var added = new List<string>();
        var failed = new List<string>();
        foreach (var process in processes)
        {
            if (_context.TrackerService.AddParallelActivityWhitelistProcess(process))
            {
                added.Add(process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.Trim() : process.Trim() + ".exe");
            }
            else
            {
                failed.Add(process);
            }
        }

        if (added.Count > 0)
        {
            var undoProcesses = added.ToList();
            _context.RegisterUndo(() =>
            {
                foreach (var process in undoProcesses)
                {
                    _context.TrackerService.RemoveParallelActivityWhitelistProcess(process);
                }
            });

            ParallelWhitelistInput.Clear();
            _selectedParallelWhitelistProcess = added.Last();
            RenderParallelWhitelist();
            var message = $"已添加并行白名单：{string.Join("、", added)}。";
            if (failed.Count > 0)
            {
                message += $" 未添加：{string.Join("、", failed)}。";
            }
            SetStatus(message, isError: failed.Count > 0);
        }
        else
        {
            SetStatus($"添加失败：{string.Join("、", failed)} 已存在或不可用。", isError: true);
        }
    }

    private static IEnumerable<string> SplitInputValues(string? text)
    {
        return (text ?? string.Empty)
            .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void DeleteSubjectButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (HasAnySelection())
        {
            DeleteSelectedItems();
            return;
        }

        if (_selectedMajor is null)
        {
            SetStatus("请先选择要删除的分类。", isError: true);
            return;
        }

        var target = GetSelectedSubjectName();
        if (string.IsNullOrWhiteSpace(target))
        {
            SetStatus("请先选择要删除的分类。", isError: true);
            return;
        }

        var promoteToParent = _context.TrackerService.SubjectDeleteBehavior == SubjectDeleteBehavior.PromoteToParent;

        var deleted = false;
        if (_selectedChild is not null && _selectedParent is not null)
        {
            deleted = _context.TrackerService.RemoveGrandChildSubject(_selectedMajor.Name, _selectedParent.Name, _selectedChild, promoteToParent);
            _selectedChild = null;
        }
        else if (_selectedParent is not null)
        {
            deleted = _context.TrackerService.RemoveChildSubject(_selectedMajor.Name, _selectedParent.Name, promoteToParent);
            _selectedParent = null;
        }
        else
        {
            deleted = _context.TrackerService.RemoveSubject(_selectedMajor.Name);
            _selectedMajor = null;
        }

        if (deleted)
        {
            RefreshDefinitions();
            SetStatus($"已删除分类：{target}。删除方式：{GetDeleteBehaviorText()}。");
        }
        else
        {
            SetStatus($"删除失败：未找到分类“{target}”。", isError: true);
        }
    }

    private void DeleteLastKeywordButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedKeywordKeys.Count > 0)
        {
            DeleteSelectedKeywords();
            return;
        }

        var selected = GetSelectedSubjectName();
        if (string.IsNullOrWhiteSpace(selected) || string.IsNullOrWhiteSpace(_selectedKeyword))
        {
            SetStatus("请先选择要删除的关键词。", isError: true);
            return;
        }

        var keyword = _selectedKeyword;
        if (_context.TrackerService.RemoveSubjectKeywordRule(selected, keyword))
        {
            var undoSubject = selected;
            var undoKeyword = keyword;
            _context.RegisterUndo(() => _context.TrackerService.AddSubjectKeywordRule(undoSubject, undoKeyword));

            _selectedKeyword = null;
            RefreshDefinitions();
            SetStatus($"已从“{selected}”删除关键词：{keyword}。");
        }
        else
        {
            SetStatus($"删除失败：未找到关键词“{keyword}”。", isError: true);
        }
    }

    private void DeleteSelectedItems()
    {
        var deleted = new List<string>();
        var promoteToParent = _context.TrackerService.SubjectDeleteBehavior == SubjectDeleteBehavior.PromoteToParent;

        if (_selectedKeywordKeys.Count > 0)
        {
            DeleteSelectedKeywords();
            return;
        }

        if (_selectedParallelWhitelistProcessKeys.Count > 0)
        {
            DeleteSelectedParallelWhitelistButton_Click(this, new System.Windows.RoutedEventArgs());
            return;
        }

        if (_selectedMajor is not null && _selectedParent is not null && _selectedChildKeys.Count > 0)
        {
            foreach (var child in _selectedChildKeys.ToList())
            {
                if (_context.TrackerService.RemoveGrandChildSubject(_selectedMajor.Name, _selectedParent.Name, child, promoteToParent))
                {
                    deleted.Add($"子类：{child}");
                }
            }
        }
        else if (_selectedMajor is not null && _selectedParentKeys.Count > 0)
        {
            foreach (var parent in _selectedParentKeys.ToList())
            {
                if (_context.TrackerService.RemoveChildSubject(_selectedMajor.Name, parent, promoteToParent))
                {
                    deleted.Add($"父类：{parent}");
                }
            }
        }
        else if (_selectedMajorKeys.Count > 0)
        {
            foreach (var major in _selectedMajorKeys.ToList())
            {
                if (_context.TrackerService.RemoveSubject(major))
                {
                    deleted.Add($"大类：{major}");
                }
            }
        }

        if (deleted.Count == 0)
        {
            SetStatus("请先选择要删除的分类或关键词。", isError: true);
            return;
        }

        ClearSelectionSets();
        _selectedMajor = null;
        _selectedParent = null;
        _selectedChild = null;
        _selectedKeyword = null;
        RefreshDefinitions();
        SetStatus($"已删除：{string.Join("、", deleted)}。");
    }

    private void DeleteSelectedKeywords()
    {
        var selected = GetSelectedSubjectName();
        if (string.IsNullOrWhiteSpace(selected) || _selectedKeywordKeys.Count == 0)
        {
            SetStatus("请先选择要删除的关键词。", isError: true);
            return;
        }

        var deleted = new List<string>();
        foreach (var keyword in _selectedKeywordKeys.ToList())
        {
            if (_context.TrackerService.RemoveSubjectKeywordRule(selected, keyword))
            {
                deleted.Add(keyword);
            }
        }

        if (deleted.Count == 0)
        {
            SetStatus("删除失败：未找到选中的关键词。", isError: true);
            return;
        }

        var undoSubject = selected;
        var undoKeywords = deleted.ToList();
        _context.RegisterUndo(() =>
        {
            foreach (var keyword in undoKeywords)
            {
                _context.TrackerService.AddSubjectKeywordRule(undoSubject, keyword);
            }
        });

        _selectedKeywordKeys.Clear();
        _selectedKeyword = null;
        RefreshDefinitions();
        SetStatus($"已从“{selected}”删除关键词：{string.Join("、", deleted)}。");
    }

    private void ClearSelectionSets()
    {
        _selectedMajorKeys.Clear();
        _selectedParentKeys.Clear();
        _selectedChildKeys.Clear();
        _selectedKeywordKeys.Clear();
        _selectedParallelWhitelistProcessKeys.Clear();
        _lastMajorIndex = -1;
        _lastParentIndex = -1;
        _lastChildIndex = -1;
        _lastKeywordIndex = -1;
        _lastParallelWhitelistProcessIndex = -1;
    }

    private void DeleteSelectedParallelWhitelistButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var targets = _selectedParallelWhitelistProcessKeys.Count > 0
            ? _selectedParallelWhitelistProcessKeys.ToList()
            : string.IsNullOrWhiteSpace(_selectedParallelWhitelistProcess)
                ? []
                : [_selectedParallelWhitelistProcess];

        if (targets.Count == 0)
        {
            SetStatus("请先选择要删除的并行白名单进程。", isError: true);
            return;
        }

        var deleted = new List<string>();
        foreach (var process in targets)
        {
            if (_context.TrackerService.RemoveParallelActivityWhitelistProcess(process))
            {
                deleted.Add(process);
            }
        }

        if (deleted.Count == 0)
        {
            SetStatus("删除失败：未找到选中的并行白名单进程。", isError: true);
            return;
        }

        var undoProcesses = deleted.ToList();
        _context.RegisterUndo(() =>
        {
            foreach (var process in undoProcesses)
            {
                _context.TrackerService.AddParallelActivityWhitelistProcess(process);
            }
        });

        _selectedParallelWhitelistProcessKeys.Clear();
        _selectedParallelWhitelistProcess = null;
        RenderParallelWhitelist();
        SetStatus($"已删除并行白名单：{string.Join("、", deleted)}。");
    }

    private void DeleteSubjectButton_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowDeleteBehaviorFlyout();
    }

    private void DeleteActionHost_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _deleteBehaviorFlyoutCloseTimer.Stop();
        _deleteBehaviorFlyoutCloseTimer.Start();
    }

    private void DeleteBehaviorFlyoutCloseTimer_Tick(object? sender, EventArgs e)
    {
        _deleteBehaviorFlyoutCloseTimer.Stop();
        if (!DeleteActionHost.IsMouseOver && !DeleteBehaviorFlyout.IsMouseOver && !DeleteSubjectButton.IsMouseOver)
        {
            HideDeleteBehaviorFlyout();
        }
    }

    private void DeleteBehaviorButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var next = _context.TrackerService.SubjectDeleteBehavior == SubjectDeleteBehavior.PromoteToParent
            ? SubjectDeleteBehavior.MatchRules
            : SubjectDeleteBehavior.PromoteToParent;
        _context.TrackerService.SetSubjectDeleteBehavior(next);
        UpdateDeleteBehaviorButton();
        HideDeleteBehaviorFlyout();
        SetStatus($"删除方式已切换为：{GetDeleteBehaviorText()}。");
    }

    private void ShowDeleteBehaviorFlyout()
    {
        _deleteBehaviorFlyoutCloseTimer.Stop();
        UpdateDeleteBehaviorButton();
        if (_isDeleteBehaviorFlyoutOpen) return;
        _isDeleteBehaviorFlyoutOpen = true;
        DeleteBehaviorFlyout.Visibility = System.Windows.Visibility.Visible;
        AnimateDeleteBehaviorFlyout(show: true);
    }

    private void HideDeleteBehaviorFlyout()
    {
        if (!_isDeleteBehaviorFlyoutOpen) return;
        _isDeleteBehaviorFlyoutOpen = false;
        AnimateDeleteBehaviorFlyout(show: false);
    }

    private void AnimateDeleteBehaviorFlyout(bool show)
    {
        var duration = new System.Windows.Duration(TimeSpan.FromMilliseconds(240));
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var opacity = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = duration,
            EasingFunction = ease
        };
        var slide = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 0 : -18,
            Duration = duration,
            EasingFunction = ease
        };
        if (!show)
        {
            opacity.Completed += (_, _) =>
            {
                if (!_isDeleteBehaviorFlyoutOpen)
                {
                    DeleteBehaviorFlyout.Visibility = System.Windows.Visibility.Collapsed;
                }
            };
        }

        DeleteBehaviorFlyout.BeginAnimation(System.Windows.UIElement.OpacityProperty, opacity);
        DeleteBehaviorFlyoutTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
    }

    private void UpdateDeleteBehaviorButton()
    {
        if (DeleteBehaviorButton is null) return;
        var promote = _context.TrackerService.SubjectDeleteBehavior == SubjectDeleteBehavior.PromoteToParent;
        var accentBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentRedBrush");
        var primaryBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("PrimaryTextBrush");
        var secondaryBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("SecondaryTextBrush");

        DeleteBehaviorButton.Background = System.Windows.Media.Brushes.Transparent;
        DeleteBehaviorButton.BorderBrush = System.Windows.Media.Brushes.Transparent;
        DeleteBehaviorFlyout.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ButtonBackgroundBrush");
        DeleteBehaviorFlyout.BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ButtonBorderBrush");

        UpdateDeleteModeTextVisual(PromoteDeleteModeText, promote, accentBrush, primaryBrush);
        UpdateDeleteModeTextVisual(MatchRulesDeleteModeText, !promote, accentBrush, primaryBrush);
        DeleteModeSeparatorText.Foreground = secondaryBrush;
        DeleteModeSeparatorText.FontWeight = System.Windows.FontWeights.SemiBold;
        DeleteModeSeparatorText.FontSize = 14;

        DeleteBehaviorButton.ToolTip = null;
    }

    private static void UpdateDeleteModeTextVisual(System.Windows.Controls.TextBlock textBlock, bool active, System.Windows.Media.Brush accentBrush, System.Windows.Media.Brush primaryBrush)
    {
        textBlock.Foreground = active ? accentBrush : primaryBrush;
        textBlock.FontWeight = active ? System.Windows.FontWeights.Black : System.Windows.FontWeights.SemiBold;
        textBlock.FontSize = active ? 15.5 : 14;
        textBlock.Opacity = active ? 1.0 : 0.82;
    }

    private string GetDeleteBehaviorText()
    {
        return _context.TrackerService.SubjectDeleteBehavior == SubjectDeleteBehavior.PromoteToParent
            ? "归为上级"
            : "匹配规则";
    }

    private void RenameButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var current = GetSelectedSubjectName();
        if (string.IsNullOrWhiteSpace(current))
        {
            SetStatus("请先选择要重命名的分类。", isError: true);
            return;
        }

        var input = RenameSubjectDialog.Show(System.Windows.Window.GetWindow(this), current)?.Trim();
        if (string.IsNullOrWhiteSpace(input) || string.Equals(input, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var renamed = false;
        if (_selectedChild is not null && _selectedMajor is not null && _selectedParent is not null)
        {
            renamed = _context.TrackerService.RenameGrandChildSubject(_selectedMajor.Name, _selectedParent.Name, _selectedChild, input);
            if (renamed) _selectedChild = input;
        }
        else if (_selectedParent is not null && _selectedMajor is not null)
        {
            renamed = _context.TrackerService.RenameChildSubject(_selectedMajor.Name, _selectedParent.Name, input);
            if (renamed) _selectedParent = new SubjectParentDefinition { Name = input };
        }
        else if (_selectedMajor is not null)
        {
            renamed = _context.TrackerService.RenameSubject(_selectedMajor.Name, input);
            if (renamed) _selectedMajor = new SubjectDefinition { Name = input };
        }

        if (renamed)
        {
            RefreshDefinitions(preserveSelection: true, preferFirstSelection: false);
            SetStatus($"已将“{current}”重命名为“{input}”，原有关键词规则、子项和历史分类已保留。");
        }
        else
        {
            SetStatus($"重命名失败：“{input}”可能已存在或不可用。", isError: true);
        }
    }
}
