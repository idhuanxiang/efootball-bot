using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Vision.Sidecar;

namespace EfootballBot.Core.Vision;

/// <summary>
/// Rust sidecar OCR 后端：帧经上传共享内存送给 vision-svc，识别在 Rust/ONNX Runtime 里跑。
/// 若侧车正在做捕获（<see cref="VisionSidecarRuntime"/>），自动复用同一个进程；
/// 识别"刚捕获的那一帧"时走 <c>src=capture</c>，跳过上传拷贝。
/// </summary>
internal sealed class SidecarOcrBackend : IOcrBackend
{
    private readonly VisionSidecarConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VisionSidecarClient? _client;
    private bool _owned;
    private bool _disposed;
    // 连续重建失败计数（审查修复③）：sidecar 运行时反复崩溃时不能每帧重启进程，
    // 超过阈值抛异常让上层 OcrRecognizer 回退到别的引擎。
    private int _rebuildFailStreak;
    private const int MaxRebuildFailures = 3;

    public SidecarOcrBackend(VisionSidecarConfig cfg)
    {
        _cfg = cfg;
        // 构造时校验 vision-svc.exe 是否存在，缺失立即抛异常以触发 SimdPaddle/RapidV6 回退
        if (VisionSidecarRuntime.SharedClient is null && VisionSidecarLocator.Locate(cfg.ExePath) is null)
            throw new global::System.IO.FileNotFoundException("未找到 vision-svc.exe", cfg.ExePath);
        Label = "vision-svc(ort)";
    }

    public string Label { get; }

    private async Task<VisionSidecarClient> EnsureClientAsync(CancellationToken ct)
    {
        var shared = VisionSidecarRuntime.SharedClient;
        if (shared is { IsDisconnected: false }) return shared;

        if (_client is { IsDisconnected: true })
        {
            // 运行时崩溃也计入失败（复查修复②a）：原本只在 StartAsync 抛异常时计数，
            // 但"启动成功→下一帧断连→重建成功"这条最常见路径 counter 恒为0、熔断永不触发。
            _rebuildFailStreak++;
            if (_rebuildFailStreak >= MaxRebuildFailures)
                throw new InvalidOperationException(
                    $"vision-svc 连续 {_rebuildFailStreak} 次断连重建，停止重试；请检查模型/D3D 环境");
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
                // 不在这里清零（审查修复）：启动成功 ≠ 恢复健康。
                // 「崩溃→断连+1→重启成功→清零→下一帧再崩」会让计数永远到不了 3，
                // 结果每帧拉起一个 vision-svc 进程。只有真正完成一次识别才证明链路可用。
            }
            catch
            {
                _rebuildFailStreak++;
                if (_rebuildFailStreak >= MaxRebuildFailures)
                    throw new InvalidOperationException(
                        $"vision-svc 连续 {_rebuildFailStreak} 次启动失败，停止重建以避免死循环；请检查模型/D3D 环境");
                throw; // 让本轮 OCR 失败退避，下一轮再试（但有上限）
            }
        }
        return _client;
    }

    public async Task<OcrResult> RecognizeAsync(FrameData frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            var fromCapture = ReferenceEquals(client, VisionSidecarRuntime.SharedClient)
                              && VisionSidecarRuntime.MatchesCapturedFrame(frame);
            var result = await client.RecognizeAsync(frame, fromCapture, ct).ConfigureAwait(false);
            // 真正跑通一次识别才清零熔断计数（崩溃循环无法靠"启动成功"洗白）
            _rebuildFailStreak = 0;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

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
