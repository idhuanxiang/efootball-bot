using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Vision.Sidecar;

namespace EfootballBot.Core.Vision;

/// <summary>检测器抽象：进程内 YoloDetector 与 sidecar 版共用，PerceptionPipeline 只认这一个。</summary>
internal interface IDetector : IDisposable
{
    string Label { get; }
    Task<IReadOnlyList<YoloDetection>> DetectAsync(FrameData frame, CancellationToken ct);
}

/// <summary>进程内 ONNX Runtime 检测（历史路径）。</summary>
internal sealed class InProcDetector : IDetector
{
    private readonly YoloDetector _yolo;
    public InProcDetector(YoloDetector yolo) => _yolo = yolo;
    public string Label => _yolo.ModelLabel;
    public Task<IReadOnlyList<YoloDetection>> DetectAsync(FrameData frame, CancellationToken ct) =>
        Task.Run(() => _yolo.Detect(frame, ct), ct);
    public void Dispose() => _yolo.Dispose();
}

/// <summary>
/// sidecar 版 YOLO（M2）：模型在 vision-svc 里跑，主进程不加载 ONNX 检测模型。
/// 与 OCR 共用同一个侧车进程；识别刚捕获帧时同样走 <c>src=capture</c> 跳过上传。
/// </summary>
internal sealed class SidecarDetector : IDetector
{
    private readonly VisionSidecarConfig _cfg;
    private readonly string[] _classes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VisionSidecarClient? _client;
    private bool _owned;
    private bool _disposed;
    // 与 OCR 侧对称的熔断（审查修复）：检测器断连即重启，视觉服务反复崩溃时
    // 会陷入"每帧拉起一个进程"的风暴，且永远不会回退进程内 YOLO。
    private int _rebuildFailStreak;
    private const int MaxRebuildFailures = 3;

    public SidecarDetector(VisionSidecarConfig cfg)
    {
        _cfg = cfg;
        // 构造时先校验 vision-svc.exe 是否就位，缺失立即抛出以触发回退进程内 YOLO
        if (VisionSidecarRuntime.SharedClient is null && VisionSidecarLocator.Locate(cfg.ExePath) is null)
            throw new global::System.IO.FileNotFoundException("未找到 vision-svc.exe", cfg.ExePath);
        var classesPath = AppPaths.FindBundledFile("models", "yolo", "classes.txt");
        // 注意：命名空间 EfootballBot.Core.System 会遮蔽 System.IO，必须写 global::
        _classes = classesPath is not null && global::System.IO.File.Exists(classesPath)
            ? global::System.IO.File.ReadAllLines(classesPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();
    }

    public string Label => $"yolo26n（vision-svc，{_classes.Length} 类）";

    private async Task<VisionSidecarClient> EnsureClientAsync(CancellationToken ct)
    {
        var shared = VisionSidecarRuntime.SharedClient;
        if (shared is { IsDisconnected: false }) return shared;

        if (_client is { IsDisconnected: true })
        {
            _rebuildFailStreak++;
            if (_rebuildFailStreak >= MaxRebuildFailures)
                throw new InvalidOperationException(
                    $"vision-svc 检测连续 {_rebuildFailStreak} 次断连重建，停止重试；请检查模型/D3D 环境");
            if (_owned) { try { await _client.DisposeAsync().ConfigureAwait(false); } catch { } }
            _client = null;
            _owned = false;
        }
        if (_client is null)
        {
            try
            {
                _client = await VisionSidecarClient
                    .StartAsync(_cfg, hwnd: 0, width: 1920, height: 1080, ct)
                    .ConfigureAwait(false);
                _owned = true;
                // 启动成功不等于健康：计数只在一次成功 Detect 后清零（见 DetectAsync）
            }
            catch
            {
                _rebuildFailStreak++;
                if (_rebuildFailStreak >= MaxRebuildFailures)
                    throw new InvalidOperationException(
                        $"vision-svc 检测连续 {_rebuildFailStreak} 次启动失败，停止重建以避免死循环；请检查模型/D3D 环境");
                throw;
            }
        }
        return _client;
    }

    public async Task<IReadOnlyList<YoloDetection>> DetectAsync(FrameData frame, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            var fromCapture = ReferenceEquals(client, VisionSidecarRuntime.SharedClient)
                              && VisionSidecarRuntime.MatchesCapturedFrame(frame);
            var raw = await client.DetectAsync(frame, fromCapture, ct).ConfigureAwait(false);
            _rebuildFailStreak = 0; // 真正跑通一次检测才清零（与 OCR 侧同一规则）
            return raw
                .Select(b => new YoloDetection(NameOf(b.C), b.C, b.S, b.X, b.Y, b.W, b.H))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string NameOf(int id) =>
        id >= 0 && id < _classes.Length ? _classes[id] : $"cls{id}";

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_owned && _client is not null)
            {
                try { _client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            _client = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
