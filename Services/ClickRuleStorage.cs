using System.Globalization;
using FgoAuto.Models;

namespace FgoAuto.Services;

public sealed record ClickRuleConfiguration(string TargetId, double Threshold, int? MaxTriggerCount, bool? IsCommandCard = null, bool? PreferTopmostMatch = null, bool? IsNoblePhantasm = null);

public static class ClickRuleStorage
{
    public static string Serialize(ImageClickRule rule) =>
        $"{rule.Target.Id}\t{rule.Threshold.ToString("0.00", CultureInfo.InvariantCulture)}\t{rule.MaxTriggerCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}\t{rule.IsCommandCard}\t{rule.PreferTopmostMatch}\t{rule.IsNoblePhantasm}";

    public static IReadOnlyList<ClickRuleConfiguration> Parse(IEnumerable<string> lines)
    {
        var configurations = new List<ClickRuleConfiguration>();
        var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var parts = line.Split('\t');
            var targetId = parts[0].Trim();
            if (targetId.Length == 0)
                continue;
            var threshold = parts.Length > 1
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed is >= 0.5 and <= 0.99 ? parsed : ImageClickRule.DefaultThreshold;
            if (!TryParseTriggerLimit(parts.Length > 2 ? parts[2] : null, out var limit))
                throw new InvalidDataException($"第 {lineNumber} 行规则“{targetId}”的触发次数上限无效，需为正整数或留空。");
            bool? isCommandCard = null;
            if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]))
            {
                if (!bool.TryParse(parts[3], out var cardMode))
                    throw new InvalidDataException($"第 {lineNumber} 行规则“{targetId}”的指令卡设置无效。");
                isCommandCard = cardMode;
            }
            bool? preferTopmostMatch = null;
            if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]))
            {
                if (!bool.TryParse(parts[4], out var topmostMode))
                    throw new InvalidDataException($"第 {lineNumber} 行规则“{targetId}”的多处匹配设置无效。");
                preferTopmostMatch = topmostMode;
            }
            bool? isNoblePhantasm = null;
            if (parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]))
            {
                if (!bool.TryParse(parts[5], out var noblePhantasm))
                    throw new InvalidDataException($"第 {lineNumber} 行规则“{targetId}”的宝具设置无效。");
                isNoblePhantasm = noblePhantasm;
            }
            if (targetIds.Add(targetId))
                configurations.Add(new ClickRuleConfiguration(targetId, threshold, limit, isCommandCard, preferTopmostMatch, isNoblePhantasm));
        }
        return configurations;
    }

    public static bool TryParseTriggerLimit(string? value, out int? limit)
    {
        limit = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            return false;
        limit = parsed;
        return true;
    }
}
