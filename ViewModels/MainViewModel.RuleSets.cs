using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoAuto.Models;
using FgoAuto.Services;

namespace FgoAuto.ViewModels;

public partial class MainViewModel
{
    private readonly ObservableCollection<ImageClickRule> _emptyClickRules = [];
    private RuleSet? _selectedRuleSet;

    public ObservableCollection<RuleSet> RuleSets { get; } = [];
    public ObservableCollection<ImageClickRule> ClickRules => SelectedRuleSet?.Rules ?? _emptyClickRules;

    public RuleSet? SelectedRuleSet
    {
        get => _selectedRuleSet;
        set
        {
            if (IsRunning || value is null || !RuleSets.Contains(value) || !SetProperty(ref _selectedRuleSet, value))
                return;
            SelectedRuleSetName = value.Name;
            OnPropertyChanged(nameof(ClickRules));
            SelectedClickRule = value.Rules.FirstOrDefault();
            StatusText = $"已切换到套装“{value.Name}”（{value.Rules.Count} 条规则）。";
        }
    }

    [ObservableProperty]
    public partial string SelectedRuleSetName { get; set; } = string.Empty;

    [RelayCommand]
    private void CreateRuleSet()
    {
        if (IsRunning)
            return;
        var set = new RuleSet(Guid.NewGuid().ToString("N"), GetUniqueRuleSetName("新套装"));
        RuleSets.Add(set);
        SelectedRuleSet = set;
        StatusText = $"已新建“{set.Name}”。从图片目标库添加需要的规则，完成后保存全部套装。";
    }

    [RelayCommand]
    private void DuplicateRuleSet()
    {
        if (IsRunning || SelectedRuleSet is not { } current)
            return;
        var copy = current.Duplicate(GetUniqueRuleSetName(current.Name + " 副本"));
        RuleSets.Add(copy);
        SelectedRuleSet = copy;
        StatusText = $"已复制为“{copy.Name}”，可以单独修改或移除其中的规则。";
    }

    [RelayCommand]
    private void RenameRuleSet()
    {
        if (IsRunning || SelectedRuleSet is not { } current)
            return;
        var name = SelectedRuleSetName.Trim();
        if (name.Length is < 1 or > 40)
        {
            StatusText = "套装名称长度需为 1 到 40 个字符。";
            return;
        }
        if (RuleSets.Any(set => set != current && set.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "已经存在同名套装，请换一个名称。";
            return;
        }
        current.Rename(name);
        SelectedRuleSetName = current.Name;
        StatusText = $"套装已改名为“{name}”。";
    }

    [RelayCommand]
    private void DeleteRuleSet()
    {
        if (IsRunning || SelectedRuleSet is not { } current)
            return;
        if (RuleSets.Count == 1)
        {
            StatusText = "至少保留一套规则；可以先新建套装，再删除这一套。";
            return;
        }
        var index = RuleSets.IndexOf(current);
        RuleSets.Remove(current);
        SelectedRuleSet = RuleSets[Math.Min(index, RuleSets.Count - 1)];
        StatusText = $"已删除套装“{current.Name}”。图片目标库中的素材仍可使用。";
    }

    private string GetUniqueRuleSetName(string baseName)
    {
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? string.Empty : $" {index}";
            var candidate = baseName[..Math.Min(baseName.Length, 40 - suffix.Length)] + suffix;
            if (RuleSets.All(set => !set.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private void ApplyRuleSetLibrary(RuleSetLibrary library)
    {
        var targets = ImageTargets.ToDictionary(target => target.Id, StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaded = new List<RuleSet>();
        foreach (var configuration in library.RuleSets)
        {
            var rules = new List<ImageClickRule>();
            foreach (var rule in configuration.Rules)
            {
                if (targets.TryGetValue(rule.TargetId, out var target))
                    rules.Add(new ImageClickRule(target, rule.Threshold, rule.MaxTriggerCount, rule.IsCommandCard, rule.PreferTopmostMatch, rule.IsNoblePhantasm));
                else
                    missing.Add(rule.TargetId);
            }
            loaded.Add(new RuleSet(configuration.Id, configuration.Name, rules));
        }
        RuleSets.Clear();
        foreach (var set in loaded)
            RuleSets.Add(set);
        SelectedRuleSet = RuleSets.First(set => set.Id.Equals(library.SelectedRuleSetId, StringComparison.OrdinalIgnoreCase));
        if (missing.Count > 0)
            AddLog($"有 {missing.Count} 个图片目标文件缺失，相关规则未载入：{string.Join("、", missing)}。");
    }

    private void LoadRuleSetsFromDisk()
    {
        try
        {
            var library = RuleSetStorage.Load(_rulesPath, _legacyRulesPath);
            ApplyRuleSetLibrary(library);
            if (!File.Exists(_rulesPath) && File.Exists(_legacyRulesPath))
            {
                RuleSetStorage.Save(_rulesPath, library);
                AddLog($"已将原有规则导入“默认套装”（{library.RuleSets[0].Rules.Count} 条），原规则文件仍保留。");
            }
        }
        catch (Exception exception)
        {
            AddLog($"载入规则套装失败：{exception.Message}");
        }
        if (RuleSets.Count == 0)
            ApplyRuleSetLibrary(RuleSetStorage.FromLegacy([]));
    }
}
