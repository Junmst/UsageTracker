using System;
using System.Diagnostics;
using System.Windows;

namespace UsageTrackerNative
{
    /// <summary>
    /// 多语言服务单例。管理当前语言，通过替换 App.Resources.MergedDictionaries
    /// 实现运行时语言切换（所有 DynamicResource 引用自动刷新）。
    /// </summary>
    public sealed class LocalizationService
    {
        private static readonly LocalizationService _instance = new();
        public static LocalizationService Instance => _instance;

        private LocalizationService() { }

        public string CurrentLanguage { get; private set; } = "zh-CN";

        public event Action? LanguageChanged;

        /// <summary>
        /// 切换到指定语言。替换 App.Resources.MergedDictionaries 中已有的语言字典。
        /// </summary>
        public void SetLanguage(string language)
        {
            var lang = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
            if (lang == CurrentLanguage) return;

            CurrentLanguage = lang;
            var resources = System.Windows.Application.Current.Resources;
            var merged = resources.MergedDictionaries;

            // 找到并移除旧的语言字典
            ResourceDictionary? oldDict = null;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var d = merged[i];
                if (d.Source != null && d.Source.OriginalString.Contains("StringResources.", StringComparison.OrdinalIgnoreCase))
                {
                    oldDict = d;
                    merged.RemoveAt(i);
                    break;
                }
            }

            // 添加新的语言字典
            var uri = new Uri($"pack://application:,,,/Resources/StringResources.{lang}.xaml", UriKind.Absolute);
            var newDict = new ResourceDictionary { Source = uri };
            merged.Add(newDict);

            Debug.WriteLine($"[Loc] Language switched to {lang}");
            LanguageChanged?.Invoke();
        }

        /// <summary>
        /// 获取当前语言下指定 key 的字符串（用于 C# 代码）。找不到时返回 key 本身。
        /// </summary>
        public string Get(string key)
        {
            var resources = System.Windows.Application.Current.Resources;
            if (resources.Contains(key) && resources[key] is string s)
                return s;
            Debug.WriteLine($"[Loc] Key '{key}' not found in {CurrentLanguage}");
            return key;
        }
    }
}
