using System.IO.MemoryMappedFiles;
using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Vision.Sidecar;

/// <summary>
/// 上传帧环（C# 写 → Rust 读）：让 OCR/检测可以作用在**任意** FrameData 上（含场景裁切块），
/// 而不只是 sidecar 自己捕获的最新帧。布局与 <see cref="SharedFrameRing"/> 完全一致。
/// </summary>
public sealed class UploadFrameRing : IDisposable
{
    private const int HeaderBytes = 64;
    private const uint Magic = 0x5649534E;
    private const uint Version = 1;
    private const int OffWidth = 8, OffHeight = 12, OffStride = 16, OffSlots = 20, OffSlotBytes = 24;
    private const int OffFlags = 28, OffSeq = 32, OffTs = 40;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _slots;
    private readonly int _slotBytes;
    private long _seq;

    public string Name { get; }

    public UploadFrameRing(string name, int maxWidth, int maxHeight, int slots = 2)
    {
        Name = name;
        _slots = Math.Max(2, slots);
        _slotBytes = checked(maxWidth * maxHeight * 4);
        var total = HeaderBytes + (long)_slotBytes * _slots;
        _mmf = MemoryMappedFile.CreateNew(name, total);
        try
        {
            _view = _mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            _mmf.Dispose();
            throw;
        }

        _view.Write(0, Magic);
        _view.Write(4, Version);
        _view.Write(OffSlots, (uint)_slots);
        _view.Write(OffSlotBytes, (uint)_slotBytes);
        _view.Write(OffFlags, 1u);
        _view.Write(OffSeq, 0L);
        _view.Write(OffTs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _view.Flush();
    }

    /// <summary>写一帧并返回发布序号（偶数）。Rust 侧按同一公式取槽位。</summary>
    public long Publish(FrameData frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame), $"非法帧尺寸：{frame.Width}x{frame.Height}");
        long stride = (long)frame.Width * 4;
        long needLong = stride * frame.Height;
        if (needLong > _slotBytes || needLong > int.MaxValue)
            throw new InvalidOperationException($"帧 {frame.Width}x{frame.Height} 超过上传环容量（{_slotBytes} 字节）");
        int need = (int)needLong;
        if (frame.Bgra.Length < need)
            throw new ArgumentException($"帧缓冲不足：{frame.Bgra.Length} < {need}", nameof(frame));

        var nextOdd = _seq + 1;
        _view.Write(OffSeq, nextOdd); // 奇数 = 写入中
        Thread.MemoryBarrier();
        var slot = (long)((nextOdd + 1) / 2 % _slots);
        _view.WriteArray(HeaderBytes + slot * _slotBytes, frame.Bgra, 0, need);
        _view.Write(OffWidth, (uint)frame.Width);
        _view.Write(OffHeight, (uint)frame.Height);
        _view.Write(OffStride, (uint)stride);
        _view.Write(OffTs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Thread.MemoryBarrier();
        _seq = nextOdd + 1;
        _view.Write(OffSeq, _seq); // 偶数 = 稳定
        // 不要在这里 Flush()：映射是共享内存（非文件），对端可见性由内存一致性保证；
        // Flush 会把整块视图往磁盘/页文件回写，实测单次 1280×720 上传会多付 ~1s。
        return _seq;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
