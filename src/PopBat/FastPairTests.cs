using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal static class FastPairTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); count++; }
        static byte[] Frame(byte code, params byte[] body) => new byte[] { 3, code, (byte)(body.Length >> 8), (byte)body.Length }.Concat(body).ToArray();
        static FastPairSnapshot? Read(Fixture channel, CancellationToken token = default) =>
            Task.Run(() => FastPairProtocol.ReadAsync(channel, token)).GetAwaiter().GetResult();
        static Fixture Channel(byte left, byte right, byte chargingCase, byte? active = 2) => new(
            Frame(3, left, right, chargingCase).Concat(active is byte mask ? Frame(6, mask) : Array.Empty<byte>()).ToArray());
        static DeviceBattery Card(FastPairSnapshot snapshot) => FastPairProtocol.ToDevice("fixture", "Unknown Brand Model", "fixture", snapshot);

        Check(FastPairProtocol.ActiveComponentsRequest().SequenceEqual(new byte[] { 3, 5, 0, 0 }), "only documented fixed active-components GET is sent");
        var channel = Channel(63, 41, 0x7f); channel.Fragment = 1;
        var snapshot = Read(channel)!;
        Check(channel.Requests == 1 && snapshot.Left.Percent == 63 && snapshot.Right.Percent == 41, "fragmented RFCOMM header and payload reassembled without model mapping");
        Check(snapshot.ActiveComponents == 2 && Card(snapshot).Reading.Percent == 63, "single left uses active left rather than lower inactive right");
        Check(Card(snapshot).Note!.Contains("右耳：未啟用") && !Card(snapshot).Note!.Contains("41%"), "inactive peer reported battery is not displayed as current");
        Check(Card(snapshot).Name == "Unknown Brand Model" && Card(snapshot).Note!.Contains("連線資訊"), "connection-name fallback identified explicitly");
        Check(!Card(snapshot).AllowEstimate, "multi-component device cannot mix discharge histories into runtime estimate");
        Check(snapshot.Case.Percent == null && snapshot.Case.Charging == null, "unknown case battery has no invented charging status");
        snapshot = Read(Channel(63, 41, 1, 1))!;
        Check(Card(snapshot).Reading.Percent == 41 && Card(snapshot).Note!.Contains("左耳：未啟用"), "single right uses bit zero and never case battery");
        snapshot = Read(Channel(63, 41, 1, 3))!;
        Check(Card(snapshot).Reading.Percent == 41 && Card(snapshot).Reading.Charging == false, "both active shows lower valid bud percentage without case");
        snapshot = Read(Channel(0, 100, 0xff, 3))!;
        Check(Card(snapshot).Reading.Percent == 0 && snapshot.Right.Percent == 100, "zero and full percentages accepted exactly");
        Check(snapshot.Case.Percent == null && snapshot.Case.Charging == null, "charging unknown case remains fully unknown");
        snapshot = Read(Channel(0x80 | 63, 41, 0x80 | 25, 2))!;
        Check(Card(snapshot).Reading.Charging == true && snapshot.Case.Charging == true && snapshot.Case.Percent == 25, "charging bit decoded independently of percentage");
        snapshot = Read(Channel(0x80 | 63, 41, 0x7f, 3))!;
        Check(Card(snapshot).Reading.Charging == null, "mixed bud charging states cannot claim an aggregate charging state");
        snapshot = Read(Channel(63, 0x7f, 100, 3))!;
        Check(Card(snapshot).Reading.Percent == null, "missing active bud percentage cannot be replaced by other bud or case");
        foreach (byte invalid in new byte[] { 101, 126, 127, 229, 254, 255 })
        {
            snapshot = Read(Channel(invalid, 41, 0x7f, 2))!;
            Check(snapshot.Left.Percent == null && snapshot.Left.Charging == null && Card(snapshot).Reading.Percent == null,
                "reserved or unknown percentage is never scaled or treated as valid");
        }
        snapshot = Read(Channel(63, 41, 0x7f, 0))!;
        Check(Card(snapshot).Reading.Percent == null && Card(snapshot).Level == "耳機未啟用", "neither active yields no current aggregate battery");
        snapshot = Read(Channel(63, 41, 0x7f, null))!;
        Check(snapshot.ActiveComponents == null && Card(snapshot).Reading.Percent == null && Card(snapshot).Note!.Contains("啟用狀態未知"),
            "missing active response displays component values with unknown use state and no aggregate");
        snapshot = Read(Channel(63, 41, 0x7f, 4))!;
        Check(snapshot.ActiveComponents == null && Card(snapshot).Reading.Percent == null, "reserved active component mask rejected");
        channel = new(Frame(6, 1).Concat(Frame(3, 63, 41, 0x7f)).ToArray());
        Check(Read(channel)?.ActiveComponents == 1, "active response can arrive before battery event");
        channel = new(Frame(3, 63, 41, 0x7f).Concat(Frame(3, 62, 40, 0x7f)).Concat(Frame(6, 2)).ToArray());
        Check(Read(channel)?.Left.Percent == 62, "latest complete battery event in the same round takes precedence");
        channel = new(Frame(1, 1, 2, 3).Concat(Frame(2, 1, 2, 3, 4, 5, 6)).Concat(Frame(3, 63, 41, 0x7f)).Concat(Frame(6, 2)).ToArray());
        Check(Read(channel)?.Left.Percent == 63, "model and address frames ignored rather than interpreted as battery");
        channel = new(new byte[] { 9, 3, 0, 3, 63, 41, 0x7f }.Concat(Frame(6, 2)).ToArray());
        Check(Read(channel) == null, "battery code in wrong message group ignored");
        Check(Read(new(Frame(3, 63, 41))) == null, "incorrect battery payload length ignored");
        channel = new(new byte[] { 3, 3, 1, 0 }.Concat(new byte[256]).ToArray());
        Check(Read(channel) == null && channel.BytesRead == 260, "payload length is big endian and bounded");
        channel = new(new byte[] { 3, 3, 2, 1 });
        Check(Read(channel) == null && channel.BytesRead == 4, "oversized body rejected before loading it");
        Check(Read(new(new byte[] { 3, 3, 0 })) == null, "truncated header produces no battery");
        Check(Read(new(new byte[] { 3, 3, 0, 3, 63, 41 })) == null, "truncated battery payload produces no battery");
        channel = new(Enumerable.Range(0, 20).SelectMany(_ => Frame(1)).Concat(Frame(3, 63, 41, 0x7f)).ToArray());
        Check(Read(channel) == null && channel.BytesRead == 64, "frame count limits unproductive status streams");
        channel = new(Enumerable.Range(0, 8).SelectMany(_ => Frame(1, new byte[512])).ToArray());
        Check(Read(channel) == null && channel.BytesRead <= 2048, "total byte budget limits repeated irrelevant frames");
        channel = Channel(63, 41, 0x7f); channel.DisconnectAfterBytes = 7;
        Check(Read(channel) == null, "link disconnect after battery cannot publish a current percentage");
        channel = Channel(63, 41, 0x7f); channel.Connected = false;
        Check(Read(channel) == null && channel.Requests == 0, "disconnected channel never queried");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool observed = false; channel = Channel(63, 41, 0x7f);
        try { Read(channel, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
        Check(observed && channel.Requests == 0, "cancellation before request performs no I/O");
        using var midRead = new CancellationTokenSource();
        channel = Channel(63, 41, 0x7f); channel.AfterRead = () => midRead.Cancel();
        observed = false;
        try { Read(channel, midRead.Token); } catch (OperationCanceledException) { observed = true; }
        Check(observed, "explicit cancellation discards partial results");
        Check(Read(new(Array.Empty<byte>())) == null, "new empty round never reuses prior round battery");
        channel = Channel(63, 41, 0x7f, null); channel.WaitAtEnd = true;
        snapshot = Read(channel)!;
        Check(snapshot.Left.Percent == 63 && snapshot.ActiveComponents == null && Card(snapshot).Reading.Percent == null,
            "bounded active-response timeout preserves only fresh labelled component reports");
        return count;
    }

    private sealed class Fixture : IFastPairStatusChannel
    {
        private readonly byte[] _bytes;
        internal Fixture(byte[] bytes) { _bytes = bytes; }
        public bool Connected { get; set; } = true;
        internal int Requests, BytesRead, Fragment = 512;
        internal int? DisconnectAfterBytes;
        internal Action? AfterRead;
        internal bool WaitAtEnd;
        public Task RequestActiveComponentsAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests++; return Task.CompletedTask; }
        public async Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(Math.Min(count, Fragment), _bytes.Length - BytesRead);
            if (length == 0 && WaitAtEnd) await Task.Delay(Timeout.Infinite, token);
            Array.Copy(_bytes, BytesRead, buffer, offset, length); BytesRead += length;
            if (DisconnectAfterBytes is int threshold && BytesRead >= threshold) Connected = false;
            AfterRead?.Invoke();
            return length;
        }
    }
}
