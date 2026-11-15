using CommunityToolkit.Mvvm.ComponentModel;

namespace FgoAuto.Models;

public sealed class ImageClickRule : ObservableObject
{
    public const double DefaultThreshold = 0.82;
    private long _triggerCount;

    public ImageMatchTarget Target { get; }
    public double Threshold { get; }
    public int? MaxTriggerCount { get; }
    public bool IsCommandCard { get; }
    public bool IsNoblePhantasm { get; }
    public bool IsCardRule => IsCommandCard || IsNoblePhantasm;
    public bool PreferTopmostMatch { get; }
    public long TriggerCount => _triggerCount;
    public bool HasReachedTriggerLimit => MaxTriggerCount is { } limit && TriggerCount >= limit;
    public string Display => IsNoblePhantasm
        ? $"{Target.Name}  ·  宝具优先（{Threshold:P0}）"
        : IsCommandCard
            ? $"{Target.Name}  ·  普通指令卡补齐三张（{Threshold:P0}）"
            : PreferTopmostMatch
            ? $"{Target.Name}  ·  多处命中选最上方（{Threshold:P0}）"
            : $"{Target.Name}  ·  命中后点击中心（{Threshold:P0}）";
    public string TriggerCountDisplay => MaxTriggerCount is { } limit
        ? $"已触发 {TriggerCount} / {limit} 次 · 达限停止全部"
        : $"已触发 {TriggerCount} 次 · 无限次";
    public Avalonia.Media.Imaging.Bitmap Thumbnail => Target.Thumbnail;

    public bool WasVisible { get; set; }
    public DateTime LastTriggeredUtc { get; set; }

    public ImageClickRule(ImageMatchTarget target, double threshold, int? maxTriggerCount = null, bool? isCommandCard = null, bool? preferTopmostMatch = null, bool? isNoblePhantasm = null)
    {
        if (maxTriggerCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTriggerCount), "触发次数上限必须为正整数。");
        Target = target;
        Threshold = threshold;
        MaxTriggerCount = maxTriggerCount;
        IsNoblePhantasm = isNoblePhantasm ?? IsNoblePhantasmName(target.Name);
        IsCommandCard = !IsNoblePhantasm && (isCommandCard ?? IsCommandCardName(target.Name));
        PreferTopmostMatch = !IsCardRule && (preferTopmostMatch ?? IsSupportName(target.Name));
    }

    public void ResetForRun()
    {
        WasVisible = false;
        LastTriggeredUtc = DateTime.MinValue;
        UpdateTriggerCount(0);
    }

    public bool RegisterSuccessfulTrigger()
    {
        WasVisible = true;
        LastTriggeredUtc = DateTime.UtcNow;
        UpdateTriggerCount(TriggerCount + 1);
        return HasReachedTriggerLimit;
    }

    public ImageClickRule WithSettings(double threshold, int? maxTriggerCount, bool? isCommandCard = null, bool? preferTopmostMatch = null, bool? isNoblePhantasm = null) =>
        Copy(Target, threshold, maxTriggerCount, isCommandCard ?? IsCommandCard, preferTopmostMatch ?? PreferTopmostMatch, isNoblePhantasm ?? IsNoblePhantasm);
    public ImageClickRule WithTarget(ImageMatchTarget target) => Copy(target, Threshold, MaxTriggerCount, IsCommandCard, PreferTopmostMatch, IsNoblePhantasm);

    public static bool IsCommandCardName(string name) => new[] { "红卡", "蓝卡", "绿卡", "Buster", "Arts", "Quick" }
        .Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsSupportName(string name)
    {
        var normalized = name.Trim();
        return normalized.EndsWith("助战", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Support", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNoblePhantasmName(string name)
    {
        var normalized = name.Trim();
        return new[] { "宝具", "宝具1", "宝具2", "宝具3", "宝具一", "宝具二", "宝具三", "大招", "大招1", "大招2", "大招3" }
            .Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            || normalized.Equals("NP", StringComparison.OrdinalIgnoreCase);
    }

    private ImageClickRule Copy(ImageMatchTarget target, double threshold, int? maxTriggerCount, bool isCommandCard, bool preferTopmostMatch, bool isNoblePhantasm) => new(target, threshold, maxTriggerCount, isCommandCard, preferTopmostMatch, isNoblePhantasm)
    {
        _triggerCount = this._triggerCount,
        WasVisible = this.WasVisible,
        LastTriggeredUtc = this.LastTriggeredUtc,
    };

    private void UpdateTriggerCount(long value)
    {
        if (SetProperty(ref _triggerCount, value, nameof(TriggerCount)))
        {
            OnPropertyChanged(nameof(TriggerCountDisplay));
            OnPropertyChanged(nameof(HasReachedTriggerLimit));
        }
    }
}
