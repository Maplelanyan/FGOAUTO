using CommunityToolkit.Mvvm.ComponentModel;
using FgoAuto.Models;

namespace FgoAuto.ViewModels;

public partial class MainViewModel
{
    private readonly RecognitionStatistics _recognitionStatistics = new();
    public IReadOnlyList<string> PerformanceDiagnosticModes { get; } = Enum.GetValues<PerformanceDiagnosticMode>()
        .Select(PerformanceDiagnostics.Describe).ToArray();

    [ObservableProperty]
    public partial int PerformanceDiagnosticModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperateEmulator))]
    public partial bool IsApplyingPerformanceDiagnostic { get; set; }

    partial void OnPerformanceDiagnosticModeIndexChanged(int value)
    {
        if (Enum.IsDefined((PerformanceDiagnosticMode)value)) _ = ApplyPerformanceDiagnosticAsync((PerformanceDiagnosticMode)value);
    }

    private async Task ApplyPerformanceDiagnosticAsync(PerformanceDiagnosticMode mode)
    {
        IsApplyingPerformanceDiagnostic = true;
        try
        {
            await _previewTouches.CancelPendingAsync();
            await _emulator.SetDiagnosticModeAsync(mode);
            if (_disposed) return;
            StatusText = $"性能诊断：{PerformanceDiagnostics.Describe(mode)}。仅本次运行生效。";
            ResetPreviewMeasurementWindow();
            OnPropertyChanged(nameof(CanOperateEmulator));
        }
        catch (Exception error)
        {
            if (!_disposed) { StatusText = $"无法切换性能诊断：{error.Message}"; AddLog(StatusText); }
        }
        finally { if (!_disposed) IsApplyingPerformanceDiagnostic = false; }
    }

    private static string FormatResource(ProcessResourceSnapshot process) => process.Available
        ? $"PID {process.ProcessId}，CPU {process.CpuCorePercent:F1}%（单核=100%），工作集/专用 {process.WorkingSetMB:F0}/{process.PrivateMB:F0} MB"
        : "进程未运行或暂不可读";
}
