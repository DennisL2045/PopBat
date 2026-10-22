using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal sealed record KeyboardQueryInspection(int ControlBytes, bool Attempted, bool QueryWritten, int ReportsReceived, bool HeaderMatched, DeviceState State);
internal sealed class LegacyKeyboardReader
{
    private readonly bool _diagnostics;
    private readonly List<KeyboardQueryInspection> _inspection = new();
    internal IReadOnlyList<KeyboardQueryInspection> Inspection => _inspection.ToArray();
    internal LegacyKeyboardReader(bool diagnostics = false) { _diagnostics = diagnostics; }
    private Task<IReadOnlyList<DeviceBattery>>? _pending;
    internal bool IsPending => _pending is { IsCompleted: false };
    private readonly Dictionary<string, (int Failures, DateTimeOffset Next)> _failures = new();
    private int _cursor;
    internal void RetryNow() { lock (_failures) _failures.Clear(); }
    internal async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken token, IReadOnlySet<string>? covered = null)
    {
        token.ThrowIfCancellationRequested();
        if (IsPending) return Array.Empty<DeviceBattery>();
        if (_diagnostics) _inspection.Clear();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var snapshot = covered?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operationToken = deadline.Token;
        _pending = Task.Run(() => ReadCoreAsync(snapshot, operationToken), CancellationToken.None);
        try { return await _pending.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Array.Empty<DeviceBattery>(); } // Retain one pending round and discard late results.
    }
    private async Task<IReadOnlyList<DeviceBattery>> ReadCoreAsync(IReadOnlySet<string>? covered, CancellationToken token)
    {
        var output = new List<DeviceBattery>();
        try
        {
            var candidates = StandardHidReader.Discover(token, true).Where(LegacyKeyboardProtocol.IsTarget)
                .GroupBy(d => d.Group).Select(g => g.OrderBy(d => d.InputLength).First()).ToArray();
            lock (_failures)
            {
                var present = candidates.Select(d => d.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string stale in _failures.Keys.Where(k => !present.Contains(k)).ToArray()) _failures.Remove(stale);
            }
            int start = candidates.Length == 0 ? 0 : _cursor % candidates.Length;
            _cursor = candidates.Length == 0 ? 0 : (start + 1) % candidates.Length;
            foreach (var candidate in candidates.Skip(start).Concat(candidates.Take(start)).Take(4))
            {
                token.ThrowIfCancellationRequested();
                if (covered?.Contains(candidate.Group) == true) continue;
                BatteryReading reading = BatteryReading.Empty(DeviceState.NoReply);
                bool written = false, matched = false; int received = 0;
                bool retry;
                lock (_failures) retry = !_failures.TryGetValue(candidate.Group, out var failure) || DateTimeOffset.UtcNow >= failure.Next;
                if (retry)
                {
                    using var handle = Native.CreateFile(candidate.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (handle.IsInvalid) reading = BatteryReading.Empty(Marshal.GetLastWin32Error() == 5 ? DeviceState.AccessDenied : DeviceState.ReadError);
                    else
                    {
                        using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
                        try
                        {
                            await stream.WriteAsync(LegacyKeyboardProtocol.Build(candidate.OutputLength), timeout.Token);
                            written = true;
                            reading = await ReceiveAsync(stream, candidate.InputLength, timeout.Token,
                                header => { received++; matched |= header; }) ?? reading;
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        catch (IOException) { reading = BatteryReading.Empty(DeviceState.ReadError); }
                    }
                    lock (_failures)
                    {
                        if (reading.State == DeviceState.Connected) _failures.Remove(candidate.Group);
                        else
                        {
                            int failures = _failures.TryGetValue(candidate.Group, out var previous) ? Math.Min(4, previous.Failures + 1) : 1;
                            _failures[candidate.Group] = (failures, DateTimeOffset.UtcNow.AddMinutes(1 << (failures - 1)));
                        }
                    }
                }
                if (_diagnostics) _inspection.Add(new(candidate.InputLength, retry, written, received, matched, reading.State));
                output.Add(new("legacy-keyboard-" + candidate.Group, "鍵盤接收器 · " + candidate.Name, "2.4 GHz · 電量相容性查詢", reading,
                    PhysicalKey: candidate.Group, Note: reading.State == DeviceState.Connected
                        ? "電量直接來自接收器回覆；名稱為 USB 接收器描述，此查詢未驗證周邊型號，充電狀態未知。"
                        : "已找到接收器，但此查詢未取得有效電量回覆；可能為傳輸或協定不符。失敗時會減少查詢，不沿用藍牙百分比。"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return output;
    }
    private static async Task<BatteryReading?> ReceiveAsync(FileStream stream, int length, CancellationToken token, Action<bool> observe)
    {
        var bytes = new byte[length];
        for (int index = 0; index < 8; index++)
        {
            int received = await stream.ReadAsync(bytes, token);
            if (received == 0) return null;
            observe(LegacyKeyboardProtocol.Matches(bytes.AsSpan(0, received)));
            var result = LegacyKeyboardProtocol.Decode(bytes.AsSpan(0, received));
            if (result != null) return result;
        }
        return null;
    }
}
