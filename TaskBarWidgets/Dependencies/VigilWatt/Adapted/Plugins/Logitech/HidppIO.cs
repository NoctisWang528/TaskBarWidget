// Adapted from VigilWatt (MIT). Copyright (c) 2026 evren; see upstream/LICENSE.
using VigilWatt.Core.Hid;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 功 能： HID++ 底层 I/O：通过 vendor 接口写 output report 发请求、读 input report 匹配响应（报文格式对照 Solaar base.py）。
/// 2026/10/06 15:53:36 xliu
/// 详见 docs/hardware/logitech.md。
/// </summary>
internal sealed class HidppIO : IDisposable
{
    public const byte SwId = 0x0B; // Solaar 约定，避开 G HUB(0x0D)/OpenRGB(0x07)/LGSTray(0x0A)

    private readonly HidDeviceHandle _handle;
    private readonly byte _reportId;
    private readonly bool _longMessage;
    private readonly object _ioGate = new();

    /// <summary>底层句柄（供探测工具使用）。</summary>
    public HidDeviceHandle Handle => _handle;

    /// <summary>本接口实际使用的 report ID（0x10 或 0x11）。</summary>
    public byte ReportId => _reportId;

    /// <summary>设备自发通知：参数为 (devIdx, data)，data 从 feature 字节起（与 <see cref="Request"/> 返回值同布局）。
    /// 在请求等待与空闲 Pump 时都会触发；订阅方必须快速返回、不重入 Request。</summary>
    public Action<byte, byte[]>? NotificationReceived;

    /// <summary>
    /// 写入许可（安全闸门）：返回 false 时 <see cref="Request"/> **一帧都不发**、直接返回 null。
    /// 这是罗技插件唯一的写入收口（safety.md §3 规则 1/2），由插件按用户开关刷新。
    /// </summary>
    public Func<bool>? WritesAllowed;

    public HidppIO(HidDeviceHandle handle)
    {
        _handle = handle;
        _reportId = HidppCodec.SelectReportId(handle.Info.OutputReportLength, handle.Info.OutputReportId);
        _longMessage = HidppCodec.IsLongMessage(_reportId);
    }

    /// <summary>发送一条 HID++ 请求并等待匹配的响应（返回响应 data 部分，不含 report id 和 devIdx）；等待期间穿插的设备通知经 <see cref="NotificationReceived"/> 抛出。</summary>
    public byte[]? Request(byte devIndex, ushort featureIdAndFunc, ReadOnlySpan<byte> args,
        bool patchSw = true, TimeSpan? timeout = null)
    {
        ushort requestId = patchSw
            ? (ushort)((featureIdAndFunc & 0xFFF0) | SwId)   // (feature<<8)|(func<<4)|sw
            : featureIdAndFunc;
        int dataLen = _longMessage ? 18 : 5;
        var data = new byte[dataLen];
        data[0] = (byte)(requestId >> 8);
        data[1] = (byte)requestId;
        for (int i = 0; i < args.Length && 2 + i < dataLen; i++)
            data[2 + i] = args[i];

        var packet = new byte[dataLen + 2];
        packet[0] = _reportId;
        packet[1] = devIndex;
        Array.Copy(data, 0, packet, 2, dataLen);

        if (packet.Length < _handle.Info.OutputReportLength)
            Array.Resize(ref packet, _handle.Info.OutputReportLength);

        var wait = timeout ?? TimeSpan.FromSeconds(3);

        lock (_ioGate)
        {
            // 写入闸门（安全规则 R1）：这是本插件唯一的写点，许可关闭时连一次 WriteOutput 都不发。
            if (WritesAllowed != null && !WritesAllowed())
            {
                Log.Debug($"HID++ 写入被开关挡住 dev={devIndex} req=0x{requestId:X4}");
                return null;
            }

            if (!_handle.WriteOutput(packet, TimeSpan.FromMilliseconds(500)))
            {
                Log.Warn($"HID++ 写失败 dev={devIndex} req=0x{requestId:X4} report=0x{_reportId:X2}");
                return null;
            }

            return ReadMatching(devIndex, data, wait);
        }
    }

    /// <summary>空闲泵：只读不写，把超时窗口内到达的通知交给 <see cref="NotificationReceived"/>；与 <see cref="Request"/> 互斥（同一把锁），零写入，供后台线程持续监听插拔电等事件。</summary>
    public void PumpNotifications(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero) return;
        lock (_ioGate)
        {
            var deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                var input = _handle.ReadInput(remaining > TimeSpan.FromMilliseconds(50)
                    ? remaining
                    : TimeSpan.FromMilliseconds(50));
                if (input == null) break;
                TryDispatchNotification(input);
            }
        }
    }

    /// <summary>读输入流直到出现前两字节与请求匹配的响应；通知报文分发给订阅方后继续等。</summary>
    private byte[]? ReadMatching(byte devIndex, byte[] requestData, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var input = _handle.ReadInput(remaining > TimeSpan.FromMilliseconds(100)
                ? remaining
                : TimeSpan.FromMilliseconds(100));
            if (input == null) break;

            if (!IsValidFrame(input)) continue;
            if (TryMatchResponse(input, devIndex, requestData, out bool error))
                return error ? null : input[2..];
            TryDispatchNotification(input);
        }
        Log.Warn($"HID++ 响应超时 dev={devIndex} req=0x{requestData[0]:X2}{requestData[1]:X2} report=0x{_reportId:X2}");
        return null;
    }

    internal static bool IsValidFrame(ReadOnlySpan<byte> input) => input.Length > 0 && (input[0] switch
    {
        HidppCodec.ShortReportId => input.Length == 7,
        HidppCodec.LongReportId => input.Length == HidppCodec.LongReportLength,
        _ => false,
    });

    internal static bool TryMatchResponse(ReadOnlySpan<byte> input, byte devIndex,
        ReadOnlySpan<byte> requestData, out bool error)
    {
        error = false;
        if (!IsValidFrame(input) || requestData.Length < 2 || input[1] != devIndex) return false;
        if (input[2] is 0x8F or 0xFF)
        {
            // HID++ 1.0/2.0 errors echo the failed request after their error marker.
            error = input[3] == requestData[0] && input[4] == requestData[1] && input[5] != 0;
            return error;
        }
        return input[2] == requestData[0] && input[3] == requestData[1];
    }

    /// <summary>若是设备自发通知则分发并返回 true。HID++ 2.0 通知的 software id（低 4 位）为 0；请求应答带 SwId=0x0B。</summary>
    private bool TryDispatchNotification(byte[] input)
    {
        if (!IsValidFrame(input) || input[2] is 0x8F or 0xFF) return false;
        byte funcSw = input[3];
        if ((funcSw & 0x0F) != 0) return false; // 带 SwId 的是应答，不是通知

        byte devIdx = input[1];
        int dataLen = input.Length - 2;
        var data = new byte[dataLen];
        Array.Copy(input, 2, data, 0, dataLen);
        try
        {
            NotificationReceived?.Invoke(devIdx, data);
        }
        catch (Exception ex)
        {
            Log.Warn($"HID++ 通知处理异常: {ex.Message}");
        }
        return true;
    }

    public void Dispose() => _handle.Dispose();
}
