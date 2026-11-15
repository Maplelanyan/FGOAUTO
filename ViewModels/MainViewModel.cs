using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoAuto.Models;
using FgoAuto.Services;

namespace FgoAuto.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const double DefaultMatchThreshold = ImageClickRule.DefaultThreshold;
    private readonly AndroidEmulatorService _emulator;
    private readonly ImageMatchService _imageMatcher = new();
    private readonly string _localDirectory;
    private readonly string _templateDirectory;
    private readonly string _rulesPath;
    private readonly string _legacyRulesPath;
    private readonly RunLogWriter _runLog;
    private readonly Dictionary<string, DateTime> _runObservations = [];
    private readonly PreviewTouchDispatcher _previewTouches;
    private bool _previewTouchLifecycleBusy;
    private CancellationTokenSource? _runCancellation;
    private byte[]? _latestScreenshot;
    private bool _disposed;

    partial void InitializePreview();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "尚未准备 Android 模拟器";

    [ObservableProperty]
    public partial string EmulatorStatus { get; set; } = "尚未准备 Android 模拟器";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrepareEmulator))]
    public partial bool AreRuntimeLicensesAccepted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrepareEmulator), nameof(PrepareEmulatorButtonText))]
    public partial bool IsPreparingEmulator { get; set; }

    [ObservableProperty]
    public partial string PrepareProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double PrepareProgressValue { get; set; }

    [ObservableProperty]
    public partial bool IsPrepareProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrepareEmulator))]
    public partial bool IsEmulatorPrepared { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperateEmulator), nameof(CanPrepareEmulator))]
    public partial bool IsEmulatorRunning { get; set; }

    [ObservableProperty]
    public partial bool UsePerformanceMode { get; set; }

    [ObservableProperty]
    public partial bool UseOpenGlMode { get; set; }

    partial void OnUseOpenGlModeChanged(bool value)
    {
        try
        {
            _emulator.SetOpenGlMode(value);
            StatusText = value ? "已选择 OpenGL 游戏模式，重启模拟器后生效；游戏无法启动时可取消此项。"
                : "已选择标准图形模式，重启模拟器后生效。";
        }
        catch (Exception error) { StatusText = $"无法保存图形模式：{error.Message}"; }
    }

    partial void OnUsePerformanceModeChanged(bool value)
    {
        try
        {
            _emulator.SetPerformanceMode(value);
            StatusText = value ? "已选择 720p 游戏渲染和匹配，重启模拟器后生效。" : "已选择 1080p 游戏渲染，重启模拟器后生效。";
        }
        catch (Exception error) { StatusText = $"无法保存画面模式：{error.Message}"; }
    }

    [ObservableProperty]
    public partial string ScreenSizeText { get; set; } = "未读取屏幕";

    [ObservableProperty]
    public partial string TemplateSelectionHint { get; set; } = "读取截图后，框选按钮、图标或其周围的独特边缘。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedPreviewImage))]
    public partial Bitmap? PreviewImage { get; set; }

    [ObservableProperty]
    public partial ImageMatchTarget? SelectedImageTarget { get; set; }

    [ObservableProperty]
    public partial string SelectedImageTargetName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ImageClickRule? SelectedClickRule { get; set; }

    [ObservableProperty]
    public partial string SelectedRuleThreshold { get; set; } = DefaultMatchThreshold.ToString("0.00", CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string SelectedRuleTriggerLimit { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedRuleModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedPreviewImage), nameof(PreviewStatusText), nameof(CanOperateEmulator))]
    public partial bool IsSelectingTemplate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationStateText), nameof(StartRunButtonText), nameof(CanOperateEmulator))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationStateText))]
    public partial string RunStageText { get; set; } = "准备开始";

    public string AutomationStateText => IsRunning ? $"执行“{SelectedRuleSet?.Name}” · {RunStageText}" : "尚未开始识别 · 点击“开始执行”运行套装";
    public string StartRunButtonText => IsRunning ? "识别执行中…" : "开始执行";
    public string PrepareEmulatorButtonText => IsPreparingEmulator ? "正在准备…" : "准备模拟器";
    public bool CanOperateEmulator => IsEmulatorRunning && _emulator.HasLiveScreenshotStream && !IsRunning && !IsSelectingTemplate
        && !_emulator.Diagnostics.IsPreviewPaused && !IsApplyingPerformanceDiagnostic
        && _uploadedFrameReceivedAt > 0 && _uploadedFrameReceivedAt >= _emulator.PreviewInteractionAfterTimestamp;
    public bool CanPrepareEmulator => AreRuntimeLicensesAccepted && !IsPreparingEmulator && !IsEmulatorRunning;
    public string RunLogPath => _runLog.FilePath;

    public ObservableCollection<ImageMatchTarget> ImageTargets { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public IReadOnlyList<string> RuleModeNames { get; } = ["普通单点（唯一匹配）", "列表目标（选最上方）", "普通指令卡（随机补齐）", "宝具卡（优先选择）"];
    public string RulesPath => _rulesPath;

    public MainViewModel() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FgoAuto"))
    { }

    public MainViewModel(string localDirectory)
    {
        _localDirectory = Path.GetFullPath(localDirectory);
        _templateDirectory = Path.Combine(_localDirectory, "templates");
        _rulesPath = Path.Combine(_localDirectory, "rule-sets.json");
        _legacyRulesPath = Path.Combine(_localDirectory, "click-rules.txt");
        _runLog = new RunLogWriter(_localDirectory);
        _emulator = new AndroidEmulatorService(_localDirectory);
        _previewTouches = new PreviewTouchDispatcher((x, y, pressure, token) =>
            _emulator.IsRunning
                ? _emulator.SendTouchAsync(x, y, pressure, identifier: 2, cancellationToken: token)
                : Task.CompletedTask,
            error => Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                StatusText = $"模拟器触控失败：{error.Message}";
                AddLog(StatusText);
            }));
        UsePerformanceMode = _emulator.UsePerformanceMode;
        UseOpenGlMode = _emulator.UseOpenGlMode;
        _emulator.FrameAvailable += OnEmulatorFrameAvailable;
        _emulator.StatusChanged += OnEmulatorServiceStatusChanged;
        _emulator.LogReceived += OnEmulatorLogReceived;
        IsEmulatorPrepared = _emulator.IsPrepared;
        IsEmulatorRunning = _emulator.IsRunning;
        EmulatorStatus = _emulator.Status;
        InitializePreview();
        LoadImageTargets();
        LoadRuleSetsFromDisk();
        AddLog("FgoAuto 将使用本机管理的 Android 14 模拟器；图片模板和规则套装已载入。");
        AddLog($"运行环境：.NET {Environment.Version}；{(Environment.Is64BitProcess ? "64" : "32")} 位；"
            + $"GC {(System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation")} / {System.Runtime.GCSettings.LatencyMode}；"
            + $"逻辑处理器 {Environment.ProcessorCount}。性能日志中的 GC 和分配统计仅属于 FgoAuto 进程。");
    }

    [RelayCommand]
    private void OpenAndroidSdkLicense()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://developer.android.com/studio/terms") { UseShellExecute = true });
        }
        catch (Exception exception) { StatusText = $"无法打开许可页面：{exception.Message}"; }
    }

    [RelayCommand]
    private void OpenGooglePlayTerms()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://play.google.com/about/play-terms/") { UseShellExecute = true });
        }
        catch (Exception exception) { StatusText = $"无法打开 Google Play 条款：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task PrepareEmulatorAsync()
    {
        if (!AreRuntimeLicensesAccepted)
        {
            StatusText = "请先阅读 Android SDK 与 Google Play 相关许可，然后勾选接受许可。";
            return;
        }
        if (IsPreparingEmulator || IsEmulatorRunning)
            return;
        IsPreparingEmulator = true;
        PrepareProgressText = "正在检查模拟器组件…";
        PrepareProgressValue = 0;
        IsPrepareProgressIndeterminate = true;
        try
        {
            StatusText = "正在准备 Android 14 模拟器；首次下载需要数 GB 空间和稳定网络。";
            var progress = new Progress<string>(UpdatePrepareProgress);
            await _emulator.PrepareAsync(AreRuntimeLicensesAccepted, progress);
            IsEmulatorPrepared = _emulator.IsPrepared;
            EmulatorStatus = _emulator.Status;
            PrepareProgressText = _emulator.Status;
            PrepareProgressValue = 100;
            IsPrepareProgressIndeterminate = false;
            StatusText = _emulator.Status;
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
        finally { IsPreparingEmulator = false; }
    }

    private void UpdatePrepareProgress(string message)
    {
        PrepareProgressText = message;
        StatusText = message;
        var percent = Regex.Match(message, @"(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%");
        if (percent.Success && double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            PrepareProgressValue = Math.Clamp(value, 0, 100);
            IsPrepareProgressIndeterminate = false;
        }
        else if (message.Contains("正在下载并安装 Android SDK 组件", StringComparison.Ordinal))
        {
            PrepareProgressValue = 0;
            IsPrepareProgressIndeterminate = true;
        }
    }

    [RelayCommand]
    private async Task StartEmulatorAsync()
    {
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            await _emulator.StartAsync(progress);
            IsEmulatorPrepared = _emulator.IsPrepared;
            IsEmulatorRunning = _emulator.IsRunning;
            StatusText = _emulator.Status;
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            IsEmulatorRunning = _emulator.IsRunning;
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private async Task StopEmulatorAsync()
    {
        _previewTouchLifecycleBusy = true;
        try
        {
            await StopAutomationBeforeLifecycleChangeAsync();
            await _emulator.StopAsync();
            IsEmulatorRunning = false;
            StatusText = _emulator.Status;
        }
        catch (Exception exception) { if (!_disposed) { StatusText = exception.Message; AddLog(exception.Message); } }
        finally { _previewTouchLifecycleBusy = false; }
    }

    [RelayCommand]
    private async Task RestartEmulatorAsync()
    {
        _previewTouchLifecycleBusy = true;
        try
        {
            await StopAutomationBeforeLifecycleChangeAsync();
            await _emulator.StopAsync();
            IsEmulatorRunning = false;
            StatusText = "正在重新启动 Android 模拟器…";
            var progress = new Progress<string>(message => StatusText = message);
            await _emulator.StartAsync(progress);
            IsEmulatorRunning = _emulator.IsRunning;
            StatusText = _emulator.Status;
            AddLog("Android 模拟器已重新启动并恢复截图流。");
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            IsEmulatorRunning = _emulator.IsRunning;
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
        finally { _previewTouchLifecycleBusy = false; }
    }

    private async Task StopAutomationBeforeLifecycleChangeAsync()
    {
        if (IsRunning)
        {
            StopRun();
            while (IsRunning)
                await Task.Delay(25);
        }
        await _previewTouches.CancelPendingAsync();
    }

    [RelayCommand]
    private void CaptureScreenshot()
    {
        var frame = _emulator.CaptureLatestFrame();
        if (frame is null)
        {
            StatusText = "先准备并启动 Android 模拟器。";
            return;
        }

        try
        {
            UpdatePreview(frame.EncodePng());
            StatusText = "截图已更新。可以框选图片目标。";
            AddLog($"已从 Android 模拟器读取截图（{ScreenSizeText}，帧 {frame.FrameId}）。");
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private async Task SelectTemplateAsync()
    {
        if (IsRunning)
        {
            StatusText = "请先停止自动执行，再框选图片目标。";
            return;
        }
        if (!_emulator.IsRunning)
        {
            StatusText = "先启动 Android 模拟器，再框选图片目标。";
            return;
        }

        _previewTouchLifecycleBusy = true;
        try
        {
            await _previewTouches.CancelPendingAsync();
            var frame = _emulator.CaptureLatestFrame() ?? await _emulator.CaptureFreshFrameAsync();
            UpdatePreview(await Task.Run(frame.EncodePng));
            IsSelectingTemplate = true;
            TemplateSelectionHint = "在右侧截图上按住鼠标拖出目标区域，边框会显示实际选区。";
            StatusText = "请在截图中框选图片目标。";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
        finally { _previewTouchLifecycleBusy = false; }
    }

    [RelayCommand]
    private void CancelTemplateSelection()
    {
        IsSelectingTemplate = false;
        TemplateSelectionHint = "读取截图后，框选按钮、图标或其周围的独特边缘。";
        StatusText = "已取消框选。";
    }

    public void UpdateTemplateSelectionHint(int left, int top, int width, int height)
    {
        TemplateSelectionHint = $"选区：({left}, {top}) · {width} × {height} 像素";
    }

    public void CreateTemplateFromSelection(int left, int top, int width, int height)
    {
        if (!IsSelectingTemplate || _latestScreenshot is null)
            return;

        IsSelectingTemplate = false;
        if (width < 12 || height < 12)
        {
            StatusText = "选区太小。请重新框选，至少 12 × 12 像素。";
            TemplateSelectionHint = StatusText;
            return;
        }

        try
        {
            var nextIndex = ImageTargets
                .Select(target => int.TryParse(target.Id.AsSpan("target_".Length), out var index) ? index : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var id = $"target_{nextIndex:D3}";
            var path = Path.Combine(_templateDirectory, $"{id}.png");
            _imageMatcher.SaveTemplate(_latestScreenshot, left, top, width, height, path);
            var target = new ImageMatchTarget(id, path, width, height, new Bitmap(path));
            File.WriteAllText(GetTargetNamePath(path), target.Name);
            ImageTargets.Add(target);
            SelectedImageTarget = target;
            TemplateSelectionHint = $"已保存 {target.Display}。建议目标位于选区中间。";
            StatusText = $"已创建图片目标：{target.Display}";
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            TemplateSelectionHint = StatusText;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private void RenameSelectedImageTarget()
    {
        if (IsRunning)
        {
            StatusText = "请先停止自动执行，再修改图片目标。";
            return;
        }
        if (SelectedImageTarget is not { } target)
        {
            StatusText = "先选择一个图片目标。";
            return;
        }

        var name = SelectedImageTargetName.Trim();
        if (name.Length is < 1 or > 40)
        {
            StatusText = "名称长度需为 1 到 40 个字符。";
            return;
        }

        try
        {
            File.WriteAllText(GetTargetNamePath(target.ImagePath), name);
            var replacement = new ImageMatchTarget(target.Id, target.ImagePath, target.Width, target.Height, target.Thumbnail, name);
            var index = ImageTargets.IndexOf(target);
            if (index >= 0)
                ImageTargets[index] = replacement;
            SelectedImageTarget = replacement;
            ReplaceRuleTarget(replacement);
            StatusText = $"图片目标已改名为“{name}”。";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private void DeleteSelectedImageTarget()
    {
        if (IsRunning)
        {
            StatusText = "请先停止自动执行，再删除图片目标。";
            return;
        }
        if (SelectedImageTarget is not { } target)
        {
            StatusText = "先选择一个图片目标。";
            return;
        }

        try
        {
            File.Delete(target.ImagePath);
            File.Delete(GetTargetNamePath(target.ImagePath));
            foreach (var set in RuleSets)
                foreach (var rule in set.Rules.Where(rule => rule.Target.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)).ToArray())
                    set.Rules.Remove(rule);
            ImageTargets.Remove(target);
            target.Thumbnail.Dispose();
            SelectedImageTarget = ImageTargets.FirstOrDefault();
            SelectedClickRule = ClickRules.FirstOrDefault();
            StatusText = $"已删除图片目标“{target.Name}”及全部套装中引用它的规则。";
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private async Task TestSelectedImageMatchAsync()
    {
        if (!TryGetSelectedImageTarget(out var target))
            return;

        var frame = _emulator.CaptureLatestFrame();
        if (frame is null)
        {
            StatusText = "先启动 Android 模拟器，再测试匹配。";
            return;
        }

        try
        {
            var bytes = await Task.Run(frame.EncodePng);
            UpdatePreview(bytes);
            var rule = ClickRules.FirstOrDefault(item => item.Target.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            var threshold = rule?.Threshold ?? DefaultMatchThreshold;
            var isNoblePhantasm = rule?.IsNoblePhantasm ?? ImageClickRule.IsNoblePhantasmName(target.Name);
            var isCard = isNoblePhantasm || (rule?.IsCommandCard ?? ImageClickRule.IsCommandCardName(target.Name));
            var preferTopmost = !isCard && (rule?.PreferTopmostMatch ?? ImageClickRule.IsSupportName(target.Name));
            var evaluation = (await Task.Run(() => _imageMatcher.FindAll(frame,
                [new ImageMatchQuery(target.Id, target.ImagePath, threshold, isCard, preferTopmost, isNoblePhantasm)])))[0];
            if (isCard)
                StatusText = $"{(isNoblePhantasm ? "宝具" : "普通指令卡")}“{target.Name}”匹配到 {evaluation.MultipleHits?.Count ?? 0} 个位置（阈值 {threshold:P0}）；优先选宝具，普通卡补齐三张。测试不会点击。";
            else if (preferTopmost && evaluation.Hit is { } listHit)
                StatusText = $"“{target.Name}”匹配到 {evaluation.MultipleHits?.Count ?? 1} 处，将点击最上方匹配项 ({listHit.CenterX}, {listHit.CenterY})，匹配度 {listHit.Score:P1}。测试不会点击。";
            else if (evaluation.IsAmbiguous)
                StatusText = $"“{target.Name}”匹配到多个相似位置；列表目标可启用“多处命中选最上方”，指令卡请启用指令卡模式。";
            else
                StatusText = evaluation.Hit is not { } hit
                    ? $"当前画面没有匹配“{target.Name}”（最高 {evaluation.BestScore:P1}，阈值 {threshold:P0}）。"
                    : $"匹配成功：{hit.Score:P1}，中心点 ({hit.CenterX}, {hit.CenterY})。测试不会点击。";
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private void AddSelectedImageRule() => AddSelectedImageRuleCore(false);

    [RelayCommand]
    private void AddSelectedNoblePhantasmRule() => AddSelectedImageRuleCore(true);

    private void AddSelectedImageRuleCore(bool asNoblePhantasm)
    {
        if (IsRunning)
        {
            StatusText = "请先停止自动执行，再添加规则。";
            return;
        }
        if (!TryGetSelectedImageTarget(out var target))
            return;
        var existing = ClickRules.FirstOrDefault(rule => rule.Target.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (asNoblePhantasm)
            {
                var converted = existing.WithSettings(existing.Threshold, existing.MaxTriggerCount, false, false, true);
                ClickRules[ClickRules.IndexOf(existing)] = converted;
                SelectedClickRule = converted;
                StatusText = $"“{target.Name}”已设为宝具卡，选卡时优先点击。";
                return;
            }
            StatusText = $"“{target.Name}”已经是一条自动点击规则。";
            return;
        }

        var rule = new ImageClickRule(target, DefaultMatchThreshold, isNoblePhantasm: asNoblePhantasm ? true : null);
        ClickRules.Add(rule);
        SelectedClickRule = rule;
        SelectedRuleThreshold = DefaultMatchThreshold.ToString("0.00", CultureInfo.InvariantCulture);
        StatusText = $"已将“{target.Name}”添加到套装“{SelectedRuleSet?.Name}”。";
    }

    [RelayCommand]
    private void DeleteSelectedClickRule()
    {
        if (IsRunning)
        {
            StatusText = "请先停止自动执行，再移除规则。";
            return;
        }
        if (SelectedClickRule is not { } rule)
            return;
        ClickRules.Remove(rule);
        SelectedClickRule = ClickRules.FirstOrDefault();
        StatusText = $"已移除“{rule.Target.Name}”自动点击规则。";
    }

    [RelayCommand]
    private void ApplySelectedRuleSettings()
    {
        if (IsRunning)
        {
            StatusText = "请先停止自动执行，再修改规则设置。";
            return;
        }
        if (SelectedClickRule is not { } rule)
        {
            StatusText = "先选择一条自动点击规则。";
            return;
        }
        if (!TryParseThreshold(SelectedRuleThreshold, out var threshold))
        {
            StatusText = "匹配阈值需在 0.50 到 0.99 之间，例如 0.82。";
            return;
        }
        if (!ClickRuleStorage.TryParseTriggerLimit(SelectedRuleTriggerLimit, out var triggerLimit))
        {
            StatusText = "触发次数上限需为正整数，例如 10；留空表示无限次。";
            return;
        }
        if (SelectedRuleModeIndex is < 0 or > 3)
        {
            StatusText = "请选择有效的规则执行方式。";
            return;
        }

        var replacement = rule.WithSettings(threshold, triggerLimit, SelectedRuleModeIndex == 2, SelectedRuleModeIndex == 1, SelectedRuleModeIndex == 3);
        var index = ClickRules.IndexOf(rule);
        ClickRules[index] = replacement;
        SelectedClickRule = replacement;
        StatusText = triggerLimit is { } limit
            ? $"已更新“{rule.Target.Name}”：阈值 {threshold:P0}，触发 {limit} 次后停止整个流程。"
            : $"已更新“{rule.Target.Name}”：阈值 {threshold:P0}，触发次数无限。";
        if (replacement.IsNoblePhantasm)
            StatusText += " 宝具模式：优先点击上方宝具，普通卡补齐三张。";
        else if (replacement.IsCommandCard)
            StatusText += " 普通指令卡模式：优先选宝具后，随机补齐三张。";
        else if (replacement.PreferTopmostMatch)
            StatusText += " 多处匹配时只点击最上方一处。";
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        if (SelectedRuleSet is null)
            return;
        try
        {
            var snapshot = RuleSetStorage.Snapshot(RuleSets, SelectedRuleSet.Id);
            await Task.Run(() => RuleSetStorage.Save(_rulesPath, snapshot));
            StatusText = $"已保存全部 {snapshot.RuleSets.Count} 套规则。";
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private async Task LoadRulesAsync()
    {
        if (IsRunning)
        {
            StatusText = "请先停止自动执行，再重新载入套装。";
            return;
        }
        try
        {
            if (!File.Exists(_rulesPath) && !File.Exists(_legacyRulesPath))
            {
                StatusText = "还没有保存的套装。配置完成后点“保存全部套装”。";
                return;
            }

            var library = await Task.Run(() => RuleSetStorage.Load(_rulesPath, _legacyRulesPath));
            if (IsRunning)
                return;
            ApplyRuleSetLibrary(library);
            StatusText = $"已载入 {RuleSets.Count} 套规则，当前为“{SelectedRuleSet?.Name}”。";
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
    }

    [RelayCommand]
    private async Task StartRunAsync()
    {
        if (IsRunning)
            return;
        if (!_emulator.IsRunning || !_emulator.HasLiveScreenshotStream)
        {
            StatusText = "请确认 Android 模拟器正在运行且截图流已就绪；必要时点“重启模拟器”。";
            return;
        }
        if (SelectedRuleSet is not { } runSet || runSet.Rules.Count == 0)
        {
            StatusText = "先添加至少一条自动点击规则。";
            return;
        }

        _runCancellation = new CancellationTokenSource();
        foreach (var rule in runSet.Rules)
            rule.ResetForRun();
        IsRunning = true;
        StatusText = $"执行套装“{runSet.Name}”（{runSet.Rules.Count} 条规则）；从 0 开始计数，任一规则达限或按“停止”时结束。";
        AddLog(StatusText);
        AddLog($"执行设备：FgoAuto 管理的 Android 模拟器；日志：{RunLogPath}");
        try
        {
            await _previewTouches.CancelPendingAsync();
            _runCancellation.Token.ThrowIfCancellationRequested();
            await RunAutomaticClickLoopAsync(runSet, _runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止自动执行。";
            AddLog(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            AddLog(exception.Message);
        }
        finally
        {
            IsRunning = false;
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    [RelayCommand]
    private void StopRun()
    {
        _runCancellation?.Cancel();
    }

    private async Task RunAutomaticClickLoopAsync(RuleSet runSet, CancellationToken cancellationToken)
    {
        var ambiguityReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = runSet.Rules.ToArray();
        var commandCards = new CommandCardSession();
        long lastProcessedFrame = -1;
        long lastScanStartedAt = 0;
        var queries = new ImageMatchQuery[rules.Length];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_emulator.Diagnostics.IsRecognitionPaused)
            {
                RunStageText = "性能诊断暂停识别";
                await Task.Delay(50, cancellationToken);
                continue;
            }
            var recognitionEpoch = _emulator.Diagnostics.RecognitionEpoch;
            try
            {
                if (lastScanStartedAt > 0)
                {
                    var remaining = TimeSpan.FromMilliseconds(50) - Stopwatch.GetElapsedTime(lastScanStartedAt);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
                }
                lastScanStartedAt = Stopwatch.GetTimestamp();
                if (_emulator.Diagnostics.IsRecognitionPaused) continue;
                if (!_emulator.IsRunning || !_emulator.HasLiveScreenshotStream)
                    throw new InvalidOperationException("Android 模拟器截图流已经中断；自动执行已停止，请重启模拟器恢复。");
                var cycleTimer = Stopwatch.StartNew();
                if (rules.Length == 0)
                    throw new OperationCanceledException(cancellationToken);

                var captureTimer = Stopwatch.StartNew();
                RunStageText = "读取画面";
                var frame = _emulator.CaptureLatestFrame();
                if (frame is not null && frame.Age > TimeSpan.FromMilliseconds(250))
                    frame = await _emulator.CaptureFreshFrameAsync(cancellationToken);
                if (_emulator.Diagnostics.IsRecognitionPaused || recognitionEpoch != _emulator.Diagnostics.RecognitionEpoch)
                    continue;
                if (frame is null)
                {
                    await Task.Delay(40, cancellationToken);
                    continue;
                }
                if (frame.FrameId == lastProcessedFrame)
                {
                    await Task.Delay(12, cancellationToken);
                    continue;
                }
                lastProcessedFrame = frame.FrameId;
                captureTimer.Stop();
                for (var i = 0; i < rules.Length; i++)
                {
                    var rule = rules[i];
                    var query = queries[i];
                    if (query is null || query.Threshold != rule.Threshold || query.FindMultiple != rule.IsCardRule
                        || query.PreferTopmostMatch != rule.PreferTopmostMatch || query.IsNoblePhantasm != rule.IsNoblePhantasm
                        || query.Id != rule.Target.Id || query.ImagePath != rule.Target.ImagePath)
                        queries[i] = new(rule.Target.Id, rule.Target.ImagePath, rule.Threshold, rule.IsCardRule, rule.PreferTopmostMatch, rule.IsNoblePhantasm);
                }
                var matchStartedAt = Stopwatch.GetTimestamp();
                var matchTimer = Stopwatch.StartNew();
                RunStageText = $"识别 {rules.Length} 条规则";
                var (evaluations, frameWidth, frameHeight) = await Task.Run(() =>
                {
                    var matches = _imageMatcher.FindAll(frame, queries, out var width, out var height);
                    return (matches, width, height);
                }, cancellationToken);
                matchTimer.Stop();
                _recognitionStatistics.Record(matchStartedAt);
                if (_emulator.Diagnostics.IsRecognitionPaused || recognitionEpoch != _emulator.Diagnostics.RecognitionEpoch)
                    continue;
                ScreenSizeText = $"{frameWidth} × {frameHeight}";
                if (frame.Age > TimeSpan.FromMilliseconds(900))
                {
                    StatusText = "本次识别画面已过期，已放弃旧坐标并重新截图。";
                    ReportRunObservation(StatusText);
                    continue;
                }
                var evaluationById = evaluations.ToDictionary(result => result.Id, StringComparer.OrdinalIgnoreCase);
                // Keep ordinary-button visibility up to date even when this frame is handled by the card batch.
                foreach (var rule in rules.Where(rule => !rule.IsCardRule))
                {
                    if (!evaluationById.TryGetValue(rule.Target.Id, out var evaluation) || evaluation.IsAmbiguous || evaluation.Hit is null)
                        rule.WasVisible = false;
                    if (evaluation is { IsAmbiguous: true })
                    {
                        if (ambiguityReported.Add(rule.Target.Id))
                            AddLog($"“{rule.Target.Name}”命中多个相似位置（最高 {evaluation.BestScore:P1}），当前要求唯一，因此未点击；列表目标可启用“多处命中选最上方”。");
                    }
                    else
                        ambiguityReported.Remove(rule.Target.Id);
                }
                var cardMatches = new List<(ImageClickRule Rule, ImageMatchHit Hit)>();
                foreach (var rule in rules.Where(rule => rule.IsCardRule))
                {
                    if (evaluationById.TryGetValue(rule.Target.Id, out var evaluation) && evaluation.MultipleHits is { } hits)
                        cardMatches.AddRange(hits.Select(hit => (rule, hit)));
                }
                var cardPlan = commandCards.Plan(cardMatches, frameWidth, frameHeight, DateTime.UtcNow,
                    waitForNoblePhantasms: rules.Any(rule => rule.IsNoblePhantasm));
                var visibleButtons = rules.Where(rule => !rule.IsCardRule)
                    .Where(rule => evaluationById.TryGetValue(rule.Target.Id, out var result) && result.Hit is not null)
                    .Select(rule => (Rule: rule, Hit: evaluationById[rule.Target.Id].Hit!)).ToArray();
                // Skill icons and the Attack button fan can resemble card labels. Reliable button matches
                // must remain actionable when a card hand has not been established.
                var useOrdinaryButtons = visibleButtons.Any(button => button.Hit.Score >= 0.95)
                    || (commandCards.VisibleNormalCardCount < 5 && visibleButtons.Length > 0);
                if (useOrdinaryButtons) cardPlan = [];
                if (cardPlan.Count > 0)
                {
                    RunStageText = "选择指令卡";
                    var clickFrame = frame;
                    var firstCard = true;
                    var skippedReason = string.Empty;
                    var result = await commandCards.ExecuteVerifiedAsync(cardPlan,
                        async (candidate, token) =>
                        {
                            if (_emulator.Diagnostics.IsRecognitionPaused || recognitionEpoch != _emulator.Diagnostics.RecognitionEpoch)
                                return null;
                            if (firstCard) { firstCard = false; return candidate; }
                            clickFrame = await _emulator.CaptureFreshFrameAsync(token);
                            if (!await Task.Run(() => FrameGuard.IsSameScene(frame, clickFrame), token))
                            {
                                skippedReason = "选卡画面已经切换";
                                return null;
                            }
                            var hit = await Task.Run(() => _imageMatcher.FindNear(clickFrame,
                                candidate.Rule.Target.ImagePath, candidate.Rule.Threshold, candidate.Hit), token);
                            if (hit is null) skippedReason = "下一张卡已移动或消失";
                            return hit is null ? null : candidate with { Hit = hit };
                        },
                        async (candidate, token) =>
                        {
                            var click = await TryTapVerifiedAsync(clickFrame, candidate.Rule, candidate.Hit, token, recognitionEpoch);
                            if (!click.Clicked) skippedReason = click.Reason;
                            return click.Clicked;
                        },
                        candidate => AddLog($"选卡：已点击{candidate.PositionDescription}“{candidate.Rule.Target.Name}”，{candidate.Rule.TriggerCountDisplay}；坐标 ({candidate.Hit.CenterX}, {candidate.Hit.CenterY})。"),
                        cancellationToken);
                    if (result.LimitRule is { } limitRule)
                    {
                        StopAtTriggerLimit(limitRule);
                        return;
                    }
                    if (result.ScreenChanged)
                    {
                        StatusText = $"已取消剩余选卡并重新识别：{skippedReason}。";
                        AddLog(StatusText);
                        continue;
                    }
                    cycleTimer.Stop();
                    var nobleCount = cardPlan.Count(candidate => candidate.Rule.IsNoblePhantasm);
                    StatusText = $"已选三张不同卡片（宝具 {nobleCount} 张、普通卡 {cardPlan.Count - nobleCount} 张），每张一次；等待进入战斗。本轮 {cycleTimer.Elapsed.TotalSeconds:F2}s。";
                    AddLog(StatusText);
                    continue;
                }
                string? incompleteCardStatus = null;
                if (commandCards.VisibleCardCount > 0 && !useOrdinaryButtons)
                {
                    StatusText = commandCards.IsComplete
                        ? "本轮三张卡已选完，等待选卡界面结束，不重复点击。"
                        : commandCards.WaitingForSettle
                            ? $"已识别宝具 {commandCards.VisibleNoblePhantasmCount} 张、普通卡 {commandCards.VisibleNormalCardCount} 张，等待画面稳定后优先选宝具。"
                            : $"已识别宝具 {commandCards.VisibleNoblePhantasmCount} 张、普通卡 {commandCards.VisibleNormalCardCount} 张，需要合计至少三张；继续等待完整画面。";
                    ReportRunObservation(StatusText);
                    // A partial/spurious card match must not block visible ordinary buttons.
                    // Once a hand has been selected, keep its existing duplicate-click protection.
                    if (commandCards.HasSelectedCards && !useOrdinaryButtons)
                        continue;
                    incompleteCardStatus = StatusText;
                }

                var newlyVisible = new List<(ImageClickRule Rule, ImageMatchHit Hit)>();
                var now = DateTime.UtcNow;

                foreach (var rule in rules)
                {
                    if (rule.IsCardRule)
                        continue;
                    if (!evaluationById.TryGetValue(rule.Target.Id, out var evaluation))
                    {
                        rule.WasVisible = false;
                        continue;
                    }
                    if (evaluation.IsAmbiguous)
                    {
                        rule.WasVisible = false;
                        continue;
                    }
                    if (evaluation.Hit is null)
                    {
                        rule.WasVisible = false;
                        continue;
                    }

                    if (!rule.WasVisible && (now - rule.LastTriggeredUtc).TotalMilliseconds >= 900)
                        newlyVisible.Add((rule, evaluation.Hit));
                }

                var clickTimer = Stopwatch.StartNew();
                if (newlyVisible.Count > 0)
                {
                    var match = newlyVisible.OrderByDescending(candidate => candidate.Hit.Score).First();
                    RunStageText = $"点击“{match.Rule.Target.Name}”";
                    var guardedClick = await TryTapVerifiedAsync(frame, match.Rule, match.Hit, cancellationToken, recognitionEpoch);
                    clickTimer.Stop();
                    if (!guardedClick.Clicked)
                    {
                        StatusText = $"画面已变化或识别已过期，已取消“{match.Rule.Target.Name}”点击并重新识别（{guardedClick.Reason}）。";
                        ReportRunObservation(StatusText);
                        continue;
                    }
                    var reachedLimit = match.Rule.RegisterSuccessfulTrigger();
                    cycleTimer.Stop();
                    var action = match.Rule.PreferTopmostMatch ? "已点击最上方匹配项" : "已点击中心";
                    StatusText = newlyVisible.Count > 1
                        ? $"同时命中多条规则，已点击匹配度最高的“{match.Rule.Target.Name}”（{match.Hit.Score:P1}）；本轮 {cycleTimer.Elapsed.TotalSeconds:F1} 秒。"
                        : $"命中“{match.Rule.Target.Name}”（{match.Hit.Score:P1}），{action}；本轮 {cycleTimer.Elapsed.TotalSeconds:F1} 秒。";
                    AddLog($"{StatusText} {match.Rule.TriggerCountDisplay}；gRPC 新帧 {captureTimer.Elapsed.TotalSeconds:F2}s，匹配 {matchTimer.Elapsed.TotalSeconds:F2}s，点击 {clickTimer.Elapsed.TotalSeconds:F2}s；坐标 ({match.Hit.CenterX}, {match.Hit.CenterY})。");
                    if (reachedLimit)
                    {
                        StopAtTriggerLimit(match.Rule);
                        return;
                    }
                }
                else
                {
                    cycleTimer.Stop();
                    var ambiguousNames = rules.Where(rule => ambiguityReported.Contains(rule.Target.Id)).Select(rule => rule.Target.Name).Take(3).ToArray();
                    StatusText = ambiguousNames.Length > 0
                        ? $"“{string.Join("、", ambiguousNames)}”命中多处，当前要求唯一而未点击；列表目标可启用“多处命中选最上方”。"
                        : $"持续监控中：本轮 {cycleTimer.Elapsed.TotalSeconds:F2}s（gRPC 新帧 {captureTimer.Elapsed.TotalSeconds:F2}s，匹配 {matchTimer.Elapsed.TotalSeconds:F2}s）。";
                    var visible = rules.Where(rule => !rule.IsCardRule && evaluationById.TryGetValue(rule.Target.Id, out var e) && e.Hit is not null).Select(rule => rule.Target.Name).Take(3).ToArray();
                    if (visible.Length == 0 && incompleteCardStatus is not null)
                    {
                        StatusText = incompleteCardStatus;
                        ReportRunObservation(StatusText);
                    }
                    else if (ambiguousNames.Length > 0)
                        ReportRunObservation(StatusText);
                    else if (visible.Length > 0)
                    {
                        StatusText = $"已识别“{string.Join("、", visible)}”，当前等待目标消失后重新触发或点击冷却结束。";
                        ReportRunObservation(StatusText);
                    }
                    else
                    {
                        var best = evaluations.MaxBy(e => e.BestScore);
                        var bestRule = best is null ? null : rules.FirstOrDefault(rule => rule.Target.Id == best.Id);
                        StatusText = bestRule is null ? "当前画面未命中规则。"
                            : $"当前画面未命中可执行规则；最高“{bestRule.Target.Name}”{best!.BestScore:P1}，阈值 {bestRule.Threshold:P0}。";
                        ReportRunObservation(StatusText);
                    }
                }
            }
            catch (InvalidOperationException) when (_emulator.Diagnostics.IsRecognitionPaused
                || recognitionEpoch != _emulator.Diagnostics.RecognitionEpoch)
            {
                // The diagnostic can pause the stream between the pre-check and
                // an awaited fresh-frame request. Resume the loop, not old work.
                continue;
            }
        }
    }

    private void StopAtTriggerLimit(ImageClickRule rule)
    {
        StatusText = $"规则“{rule.Target.Name}”已触发 {rule.TriggerCount} / {rule.MaxTriggerCount} 次，达到上限，整个流程已停止。";
        AddLog(StatusText);
    }

    private async Task<(bool Clicked, string Reason)> TryTapVerifiedAsync(
        GameFrame referenceFrame, ImageClickRule rule, ImageMatchHit expectedHit, CancellationToken cancellationToken,
        long recognitionEpoch)
    {
        if (_emulator.Diagnostics.IsRecognitionPaused || recognitionEpoch != _emulator.Diagnostics.RecognitionEpoch)
            return (false, "性能诊断已暂停或更改，重新识别后才能点击");
        if (referenceFrame.Age > TimeSpan.FromMilliseconds(900))
            return (false, "匹配所用画面已过期");

        var freshFrame = await _emulator.CaptureFreshFrameAsync(cancellationToken);
        if (freshFrame.Age > TimeSpan.FromMilliseconds(700))
            return (false, "点击复核画面已过期");
        if (!await Task.Run(() => FrameGuard.IsSameScene(referenceFrame, freshFrame), cancellationToken))
            return (false, "模拟器画面已切换");

        var freshHit = await Task.Run(() => _imageMatcher.FindNear(
            freshFrame, rule.Target.ImagePath, rule.Threshold, expectedHit), cancellationToken);
        if (freshHit is null)
            return (false, "目标在点击前已移动或消失");
        if (freshFrame.Age > TimeSpan.FromMilliseconds(700))
            return (false, "目标复核耗时过长，画面已过期");
        if (_emulator.Diagnostics.IsRecognitionPaused || recognitionEpoch != _emulator.Diagnostics.RecognitionEpoch)
            return (false, "复核期间性能诊断已更改，已取消点击");

        var pressed = false;
        try
        {
            pressed = true;
            await _emulator.SendTouchAsync(freshHit.CenterX, freshHit.CenterY, 1, cancellationToken: cancellationToken);
            await Task.Delay(55, cancellationToken);
        }
        finally
        {
            if (pressed)
                await _emulator.SendTouchAsync(freshHit.CenterX, freshHit.CenterY, 0, cancellationToken: CancellationToken.None);
        }
        return (true, string.Empty);
    }

    public void QueuePreviewTouch(int x, int y, int pressure)
    {
        if (pressure == 0 && (_disposed || _previewTouchLifecycleBusy || !CanOperateEmulator))
        {
            // Reset the physical pointer state even when its preview disappeared.
            _ = _previewTouches.CancelPendingAsync(pointerReleased: true);
            return;
        }
        if (_disposed || _previewTouchLifecycleBusy || !CanOperateEmulator) return;
        var image = LivePreviewImage;
        if (image is null || _previewNativeWidth <= 0 || _previewNativeHeight <= 0) return;
        var nativeX = Math.Clamp((int)Math.Round(x * (double)_previewNativeWidth / image.PixelSize.Width), 0, _previewNativeWidth - 1);
        var nativeY = Math.Clamp((int)Math.Round(y * (double)_previewNativeHeight / image.PixelSize.Height), 0, _previewNativeHeight - 1);
        _previewTouches.Submit(nativeX, nativeY, pressure);
    }

    private void OnEmulatorServiceStatusChanged(string status) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        EmulatorStatus = status;
        IsEmulatorPrepared = _emulator.IsPrepared;
        IsEmulatorRunning = _emulator.IsRunning;
        OnPropertyChanged(nameof(CanOperateEmulator));
        if (!IsEmulatorRunning)
        {
            _ = _previewTouches.CancelPendingAsync();
            ClearVideoBitmap();
            LivePreviewStatus = "Android 模拟器未运行";
        }
        else if (!_emulator.HasLiveScreenshotStream && !_emulator.Diagnostics.IsScreenshotStreamPaused
            && status.Contains("截图流", StringComparison.OrdinalIgnoreCase))
        {
            _ = _previewTouches.CancelPendingAsync();
            ClearVideoBitmap();
            LivePreviewStatus = status;
        }
        if (!IsRunning)
            StatusText = status;
    });

    private void OnEmulatorLogReceived(string message) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) AddLog(message);
    });

    private void LoadImageTargets()
    {
        if (!Directory.Exists(_templateDirectory))
            return;

        foreach (var path in Directory.GetFiles(_templateDirectory, "target_*.png").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var (width, height) = _imageMatcher.GetImageSize(path);
                var namePath = GetTargetNamePath(path);
                var name = File.Exists(namePath) ? File.ReadAllText(namePath).Trim() : null;
                ImageTargets.Add(new ImageMatchTarget(Path.GetFileNameWithoutExtension(path), path, width, height, new Bitmap(path), name));
            }
            catch (Exception exception)
            {
                AddLog($"忽略无法载入的图片模板：{Path.GetFileName(path)}（{exception.Message}）");
            }
        }
        SelectedImageTarget = ImageTargets.FirstOrDefault();
    }

    private void ReplaceRuleTarget(ImageMatchTarget target)
    {
        foreach (var set in RuleSets)
        {
            for (var index = 0; index < set.Rules.Count; index++)
            {
                var rule = set.Rules[index];
                if (!rule.Target.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                var replacement = rule.WithTarget(target);
                set.Rules[index] = replacement;
                if (ReferenceEquals(SelectedClickRule, rule))
                    SelectedClickRule = replacement;
                break;
            }
        }
    }

    private bool TryGetSelectedImageTarget(out ImageMatchTarget target)
    {
        if (SelectedImageTarget is { } selected)
        {
            target = selected;
            return true;
        }
        target = null!;
        StatusText = "先读取截图并框选一个图片目标。";
        return false;
    }

    private void UpdatePreview(byte[] bytes)
    {
        _latestScreenshot = bytes;
        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        var previous = PreviewImage;
        PreviewImage = bitmap;
        previous?.Dispose();
        ScreenSizeText = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}";
    }

    private void AddLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        _runLog.Write(line);
        Logs.Insert(0, line);
        while (Logs.Count > 100)
            Logs.RemoveAt(Logs.Count - 1);
    }

    private void ReportRunObservation(string message)
    {
        var now = DateTime.UtcNow;
        if (_runObservations.TryGetValue(message, out var previous) && now - previous < TimeSpan.FromSeconds(3)) return;
        if (_runObservations.Count >= 64) _runObservations.Clear();
        _runObservations[message] = now;
        AddLog(message);
    }

    private static bool TryParseThreshold(string value, out double threshold) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
        && threshold is >= 0.5 and <= 0.99;

    private static string GetTargetNamePath(string imagePath) => Path.ChangeExtension(imagePath, ".name");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runCancellation?.Cancel();
        _previewTouches.Dispose();
        DisposePreview();
        _emulator.FrameAvailable -= OnEmulatorFrameAvailable;
        _emulator.StatusChanged -= OnEmulatorServiceStatusChanged;
        _emulator.LogReceived -= OnEmulatorLogReceived;
        _emulator.Dispose();
        _imageMatcher.Dispose();
        _runLog.Dispose();
    }

    partial void OnSelectedImageTargetChanged(ImageMatchTarget? oldValue, ImageMatchTarget? newValue)
    {
        SelectedImageTargetName = newValue?.Name ?? string.Empty;
    }

    partial void OnSelectedClickRuleChanged(ImageClickRule? oldValue, ImageClickRule? newValue)
    {
        SelectedRuleThreshold = newValue?.Threshold.ToString("0.00", CultureInfo.InvariantCulture)
            ?? DefaultMatchThreshold.ToString("0.00", CultureInfo.InvariantCulture);
        SelectedRuleTriggerLimit = newValue?.MaxTriggerCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SelectedRuleModeIndex = newValue is null ? 0 : newValue.IsNoblePhantasm ? 3 : newValue.IsCommandCard ? 2 : newValue.PreferTopmostMatch ? 1 : 0;
    }
}
