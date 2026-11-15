using System.Text.Json;
using FgoAuto.Models;

namespace FgoAuto.Services;

public sealed record RuleSetConfiguration(string Id, string Name, IReadOnlyList<ClickRuleConfiguration> Rules);
public sealed record RuleSetLibrary(int Version, string SelectedRuleSetId, IReadOnlyList<RuleSetConfiguration> RuleSets);

public static class RuleSetStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static RuleSetLibrary Snapshot(IEnumerable<RuleSet> sets, string selectedId) => new(1, selectedId,
        sets.Select(set => new RuleSetConfiguration(set.Id, set.Name,
            set.Rules.Select(rule => new ClickRuleConfiguration(rule.Target.Id, rule.Threshold, rule.MaxTriggerCount, rule.IsCommandCard, rule.PreferTopmostMatch, rule.IsNoblePhantasm)).ToArray())).ToArray());

    public static RuleSetLibrary FromLegacy(IEnumerable<string> lines) => new(1, "default",
        [new RuleSetConfiguration("default", "默认套装", ClickRuleStorage.Parse(lines))]);

    public static RuleSetLibrary Load(string path, string legacyPath)
    {
        if (File.Exists(path))
            return Deserialize(File.ReadAllText(path));
        return FromLegacy(File.Exists(legacyPath) ? File.ReadAllLines(legacyPath) : []);
    }

    public static string Serialize(RuleSetLibrary library)
    {
        Validate(library);
        return JsonSerializer.Serialize(library, JsonOptions);
    }

    public static RuleSetLibrary Deserialize(string json)
    {
        var library = JsonSerializer.Deserialize<RuleSetLibrary>(json, JsonOptions)
            ?? throw new InvalidDataException("套装配置为空。");
        Validate(library);
        return library;
    }

    public static void Save(string path, RuleSetLibrary library)
    {
        var json = Serialize(library);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void Validate(RuleSetLibrary library)
    {
        if (library.Version != 1)
            throw new InvalidDataException($"不支持此套装配置版本：{library.Version}。");
        if (library.RuleSets is null || library.RuleSets.Count == 0)
            throw new InvalidDataException("套装配置至少需要包含一套规则。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in library.RuleSets)
        {
            if (set is null || string.IsNullOrWhiteSpace(set.Id) || !ids.Add(set.Id))
                throw new InvalidDataException("套装编号为空或重复。");
            if (string.IsNullOrWhiteSpace(set.Name) || set.Name.Length > 40 || !names.Add(set.Name.Trim()))
                throw new InvalidDataException("套装名称需为 1 到 40 个字符，并且不能重复。");
            if (set.Rules is null)
                throw new InvalidDataException($"套装“{set.Name}”缺少规则列表。");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in set.Rules)
            {
                if (rule is null || string.IsNullOrWhiteSpace(rule.TargetId) || !targets.Add(rule.TargetId))
                    throw new InvalidDataException($"套装“{set.Name}”包含空白或重复的图片目标。");
                if (!double.IsFinite(rule.Threshold) || rule.Threshold is < 0.5 or > 0.99 || rule.MaxTriggerCount is <= 0)
                    throw new InvalidDataException($"套装“{set.Name}”的规则阈值或触发次数上限无效。");
            }
        }
        if (string.IsNullOrEmpty(library.SelectedRuleSetId) || !ids.Contains(library.SelectedRuleSetId))
            throw new InvalidDataException("当前选择的套装不存在。");
    }
}
