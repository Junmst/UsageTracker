using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace UsageTrackerNative.Modules.Settings;

public partial class ImportOptionsDialog : Window
{
    private readonly ImportPackagePreview _preview;
    private readonly ImportPayloadKind _kind;
    private bool _isInitializing;

    private ImportOptionsDialog(ImportPackagePreview preview, ImportPayloadKind kind)
    {
        _preview = preview;
        _kind = kind;
        InitializeComponent();
        InitializeOptions();
        ApplyPreview();
        RefreshPreviewRows();
        RefreshDangerState();
    }

    public ImportDataMode SelectedDataMode { get; private set; } = ImportDataMode.ViewOnly;
    public ImportSettingsMode SelectedSettingsMode { get; private set; } = ImportSettingsMode.None;
    public ImportConflictStrategy SelectedConflictStrategy { get; private set; } = ImportConflictStrategy.KeepLocal;
    public bool ShouldApply { get; private set; }

    public static ImportOptionsDialog? Show(Window? owner, ImportPackagePreview preview, ImportPayloadKind kind)
    {
        var dialog = new ImportOptionsDialog(preview, kind)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true ? dialog : null;
    }

    private void InitializeOptions()
    {
        _isInitializing = true;
        DataModeBox.ItemsSource = new[]
        {
            new OptionItem<ImportDataMode>("仅查看", ImportDataMode.ViewOnly),
            new OptionItem<ImportDataMode>("合并到本地", ImportDataMode.Merge),
            new OptionItem<ImportDataMode>("替换本地", ImportDataMode.Replace)
        };
        ConflictModeBox.ItemsSource = new[]
        {
            new OptionItem<ImportConflictStrategy>("保留本地", ImportConflictStrategy.KeepLocal),
            new OptionItem<ImportConflictStrategy>("使用导入文件", ImportConflictStrategy.UseIncoming),
            new OptionItem<ImportConflictStrategy>("仅导入新增", ImportConflictStrategy.NewOnly)
        };
        SettingsModeBox.ItemsSource = _kind == ImportPayloadKind.Full
            ? new[]
            {
                new OptionItem<ImportSettingsMode>("不导入配置", ImportSettingsMode.None),
                new OptionItem<ImportSettingsMode>("仅查看配置", ImportSettingsMode.ViewOnly),
                new OptionItem<ImportSettingsMode>("仅导入规则", ImportSettingsMode.RulesOnly),
                new OptionItem<ImportSettingsMode>("仅导入基础设置", ImportSettingsMode.BasicOnly),
                new OptionItem<ImportSettingsMode>("合并配置", ImportSettingsMode.Merge),
                new OptionItem<ImportSettingsMode>("替换配置", ImportSettingsMode.Replace)
            }
            : new[]
            {
                new OptionItem<ImportSettingsMode>("仅查看", ImportSettingsMode.ViewOnly),
                new OptionItem<ImportSettingsMode>("仅导入规则", ImportSettingsMode.RulesOnly),
                new OptionItem<ImportSettingsMode>("仅导入基础设置", ImportSettingsMode.BasicOnly),
                new OptionItem<ImportSettingsMode>("合并配置", ImportSettingsMode.Merge),
                new OptionItem<ImportSettingsMode>("替换配置", ImportSettingsMode.Replace)
            };
        SearchModeBox.ItemsSource = new[]
        {
            new OptionItem<SessionSearchMode>("全盘", SessionSearchMode.All),
            new OptionItem<SessionSearchMode>("分类", SessionSearchMode.Subject),
            new OptionItem<SessionSearchMode>("标题", SessionSearchMode.Title),
            new OptionItem<SessionSearchMode>("进程", SessionSearchMode.Process)
        };

        DataOptionsPanel.Visibility = _kind == ImportPayloadKind.Settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsOptionsPanel.Visibility = _kind == ImportPayloadKind.Usage ? Visibility.Collapsed : Visibility.Visible;
        DataModeBox.SelectedIndex = _kind == ImportPayloadKind.Full ? 2 : 1;
        ConflictModeBox.SelectedIndex = 0;
        SettingsModeBox.SelectedIndex = _kind == ImportPayloadKind.Full ? 5 : 1;
        SearchModeBox.SelectedIndex = 0;
        _isInitializing = false;
        UpdateSelections();
    }

    private void ApplyPreview()
    {
        TitleText.Text = _kind switch
        {
            ImportPayloadKind.Usage => "导入数据预览",
            ImportPayloadKind.Settings => "导入配置预览",
            _ => "导入完整备份预览"
        };
        SubtitleText.Text = _preview.FilePath;
        TotalRecordsText.Text = _preview.TotalRecords.ToString();
        DiffText.Text = $"{_preview.DataPreview.NonConflictCount}/{_preview.DataPreview.ConflictCount}";
        RulesText.Text = $"{_preview.SubjectDefinitionCount}/{_preview.SubjectKeywordRuleCount}";
        RangeText.Text = _preview.EarliestStartTime is null || _preview.LatestStartTime is null
            ? "无数据"
            : $"{_preview.EarliestStartTime:MM-dd HH:mm}\n{_preview.LatestStartTime:MM-dd HH:mm}";
        ConfigSummaryText.Text = $"主题：{_preview.Theme ?? "-"}\n主题色：{_preview.ThemeAccentColor ?? "-"}\n空闲：{(_preview.IdleTimeoutMinutes?.ToString() ?? "-")} 分钟\n快捷键：{_preview.ManualIdleShortcutText ?? "-"}\n删除行为：{_preview.SubjectDeleteBehavior ?? "-"}";
    }

    private void RefreshPreviewRows()
    {
        var searchMode = SearchModeBox.SelectedItem is OptionItem<SessionSearchMode> option ? option.Value : SessionSearchMode.All;
        PreviewGrid.ItemsSource = _preview.State.History is null
            ? Array.Empty<UsageSession>()
            : UsageTrackerPreviewSearch.GetPreviewSessions(_preview, SearchBox.Text?.Trim() ?? string.Empty, searchMode);
    }

    private void UpdateSelections()
    {
        if (DataModeBox.SelectedItem is OptionItem<ImportDataMode> dataOption)
        {
            SelectedDataMode = _kind == ImportPayloadKind.Settings ? ImportDataMode.ViewOnly : dataOption.Value;
        }
        if (ConflictModeBox.SelectedItem is OptionItem<ImportConflictStrategy> conflictOption)
        {
            SelectedConflictStrategy = conflictOption.Value;
        }
        if (SettingsModeBox.SelectedItem is OptionItem<ImportSettingsMode> settingsOption)
        {
            SelectedSettingsMode = _kind == ImportPayloadKind.Usage ? ImportSettingsMode.None : settingsOption.Value;
        }
    }

    private void RefreshDangerState()
    {
        UpdateSelections();
        var isDangerous = SelectedDataMode == ImportDataMode.Replace || SelectedSettingsMode == ImportSettingsMode.Replace;
        DangerConfirmBox.Visibility = isDangerous ? Visibility.Visible : Visibility.Collapsed;
        if (!isDangerous)
        {
            DangerConfirmBox.IsChecked = false;
        }

        ApplyButton.Content = SelectedDataMode == ImportDataMode.ViewOnly && SelectedSettingsMode is ImportSettingsMode.None or ImportSettingsMode.ViewOnly
            ? "进入仅查看"
            : "执行导入";
        ApplyButton.IsEnabled = !isDangerous || DangerConfirmBox.IsChecked == true;
        ConflictModeBox.IsEnabled = SelectedDataMode == ImportDataMode.Merge;
    }

    private void Options_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        RefreshDangerState();
    }

    private void SearchBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        RefreshPreviewRows();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelections();
        ShouldApply = !(SelectedDataMode == ImportDataMode.ViewOnly && SelectedSettingsMode is ImportSettingsMode.None or ImportSettingsMode.ViewOnly);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var easing = new CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        DialogRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
        DialogTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
    }

    private sealed record OptionItem<T>(string Text, T Value)
    {
        public override string ToString() => Text;
    }
}
