using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal static class StandardBatteryTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); count++; }
        var battery = new HidValueCapability { Page = 6, Usage = 0x20, Absolute = 1, BitSize = 8, Count = 1, Maximum = 100, ReportId = 3 };
        var noButtons = new HashSet<byte>();
        Check(Marshal.SizeOf<HidValueCapability>() == 72, "HID value capability native layout");
        Check(StandardHidProtocol.Valid(battery), "standard battery feature valid without brand or model lookup");
        Check(StandardHidProtocol.Percent(battery, 0) == 0 && StandardHidProtocol.Percent(battery, 100) == 100, "standard HID battery endpoints");
        Check(StandardHidProtocol.Percent(battery, 101) == null, "HID null out of range discarded");
        var scaled = battery; scaled.Maximum = 255;
        Check(StandardHidProtocol.Percent(scaled, 128) == 50, "Battery Strength uses descriptor proportion");
        scaled.Maximum = 10;
        Check(StandardHidProtocol.Percent(scaled, 3) == 30, "coarse descriptor battery proportion conversion");
        Check(StandardHidProtocol.SafeFeatures(new[] { battery }, noButtons).Count == 1, "battery-only feature allowed");
        Check(StandardHidProtocol.SafeFeatures(new[] { battery }, null).Count == 0, "unknown button capabilities fail closed");
        Check(StandardHidProtocol.SafeFeatures(new[] { battery }, new HashSet<byte> { 3 }).Count == 0, "feature with buttons rejected");
        var foreign = battery; foreign.Page = 0xFF00; foreign.Usage = 1;
        Check(StandardHidProtocol.SafeFeatures(new[] { battery, foreign }, noButtons).Count == 0, "mixed vendor and battery report never read");
        foreign.ReportId = 4;
        Check(StandardHidProtocol.SafeFeatures(new[] { battery, foreign }, noButtons).Count == 1, "unrelated report does not authorize it or block safe battery report");
        var keyboard = battery; keyboard.Page = 7; keyboard.Usage = 4;
        Check(!StandardHidProtocol.Valid(keyboard), "keyboard field cannot be read as battery");
        var invalid = battery; invalid.Absolute = 0;
        Check(!StandardHidProtocol.Valid(invalid), "relative motion rejected");
        invalid = battery; invalid.Count = 2;
        Check(!StandardHidProtocol.Valid(invalid), "battery array ambiguity rejected");
        invalid = battery; invalid.BitField = 1;
        Check(!StandardHidProtocol.Valid(invalid), "constant field not live battery");
        invalid = battery; invalid.Maximum = 0;
        Check(StandardHidProtocol.Percent(invalid, 0) == null, "zero logical range rejected");
        invalid = battery; invalid.IsRange = 1; invalid.UsageMaximum = 0x21;
        Check(!StandardHidProtocol.Valid(invalid), "mixed usage range rejected");
        invalid = battery; invalid.Alias = 1;
        Check(!StandardHidProtocol.Valid(invalid), "alias feature rejected conservatively");
        var capacity = battery; capacity.Page = 0x85; capacity.Usage = 0x64;
        Check(StandardHidProtocol.Percent(capacity, 68) == 68, "relative state of charge standard percentage");
        capacity.Usage = 0x66;
        Check(!StandardHidProtocol.Valid(capacity), "remaining capacity may be mAh not percentage");
        Check(BluetoothProtocol.IdentityText(Encoding.UTF8.GetBytes("Model Number")) == "", "firmware model placeholder rejected");
        Check(BluetoothProtocol.IdentityText(Encoding.UTF8.GetBytes("Manufacturer Name")) == "", "firmware manufacturer placeholder rejected");
        Check(BluetoothProtocol.IdentityText(Encoding.UTF8.GetBytes("Any New Brand")) == "Any New Brand", "unknown brand not rejected");
        var generic = new GenericChannel();
        var read = BluetoothProtocol.ReadAsync(generic, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(read?.Reading.Percent == 94 && read.Identity.Name == "Fixture Keyboard BT", "no model or vendor protocol needed for standard Bluetooth battery");
        Check(read?.Identity.Note?.Contains("連線名稱") == true, "connection-name fallback is clearly marked");
        Check(generic.Requests.All(pair => pair != (0x180A, 0x2A50)), "generic identification does not need vendor ID lookup");
        generic = new GenericChannel { DirectName = "Direct Fixture Name" };
        read = BluetoothProtocol.ReadAsync(generic, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(read?.Identity.Name == "Direct Fixture Name" && read.Identity.Note == null, "direct name preferred over OS connection name");
        foreach (byte reported in new byte[] { 94, 37, 12 })
        {
            read = BluetoothProtocol.ReadAsync(new GenericChannel { DirectName = "M75PRO-5.0-BT1", Battery = reported }, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(read?.Reading is { State: DeviceState.UnverifiedReading, Percent: null, Charging: null }, "known mismatched firmware source rejected independently of percentage");
        }
        var cachedIdentity = read!.Identity;
        read = BluetoothProtocol.ReadAsync(new GenericChannel { Battery = 53 }, cachedIdentity, CancellationToken.None).GetAwaiter().GetResult();
        Check(read?.Reading.State == DeviceState.UnverifiedReading && read.Reading.Percent == null, "identity cache cannot reenable rejected battery source");
        Check(!BluetoothProtocol.UnreliableBatterySource("Fixture M75PRO-5.0-BT1 Mouse"), "source exception uses exact firmware identity only");

        generic = new GenericChannel { DirectName = "M75PRO-5.0-BT1", Notification = new byte[] { 37 } };
        read = BluetoothProtocol.ReadAsync(generic, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(read?.Reading is { State: DeviceState.Connected, Percent: 37, Charging: null } && generic.Observations == 1,
            "fresh device notification replaces inaccurate read without scaling");
        Check(!generic.Requests.Contains((0x180F, 0x2A19)), "notification source skips the known inaccurate read entirely");
        generic.Battery = 12; generic.Notification = new byte[] { 80 };
        read = BluetoothProtocol.ReadAsync(generic, read!.Identity, CancellationToken.None).GetAwaiter().GetResult();
        Check(read?.Reading.Percent == 80, "cached identity continues using actual notification rather than read or prior update");
        foreach (byte[] invalidUpdate in new[] { new byte[] { 101 }, new byte[] { 37, 0 } })
        {
            generic.Notification = invalidUpdate;
            read = BluetoothProtocol.ReadAsync(generic, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(read?.Reading is { State: DeviceState.UnverifiedReading, Percent: null }, "invalid notification never enables bad read fallback");
        }
        generic.Notification = null;
        read = BluetoothProtocol.ReadAsync(generic, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(read?.Reading.Percent == null, "missing new notification never reuses previous round update");
        var now = DateTimeOffset.UtcNow;
        Check(BluetoothProtocol.NotifiedPercent(new(new byte[] { 25 }, now.AddSeconds(-20)), now, now) == null,
            "notification timestamp before current observation rejected");
        Check(BluetoothProtocol.NotifiedPercent(new(new byte[] { 25 }, now.AddSeconds(1)), now, now) == null,
            "future notification timestamp rejected");
        generic = new GenericChannel { DirectName = "Other Keyboard", Notification = new byte[] { 50 } };
        read = BluetoothProtocol.ReadAsync(generic, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(read?.Reading.Percent == 94 && generic.Observations == 0, "working generic read does not add event waits");
        generic = new GenericChannel { DirectName = "M75PRO-5.0-BT1", Notification = new byte[] { 37 }, DisconnectOnObserve = true };
        read = BluetoothProtocol.ReadAsync(generic, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(read == null, "disconnect after notification cannot publish its percentage");

        DeviceBattery Reading(string key, string physical, DeviceState state = DeviceState.Connected) => new(key, "Same Device Name", "fixture",
            new(state, state == DeviceState.Connected ? 50 : null, null, DateTimeOffset.UtcNow), PhysicalKey: physical);
        var order = new List<string>(); int vendorQueries = 0;
        var providers = new[] {
            new BatteryProvider("vendor", BatteryProviderKind.VendorFallback, 0, (context, _) => {
                order.Add("vendor"); if (context.CoveredPhysicalKeys.Contains("physical-a")) return Task.FromResult<IReadOnlyList<DeviceBattery>>(Array.Empty<DeviceBattery>());
                vendorQueries++; return Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("vendor", "physical-a") });
            }),
            new BatteryProvider("standard", BatteryProviderKind.Standard, 0, (_, _) => {
                order.Add("standard"); return Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("standard", "physical-a") });
            }) };
        var result = BatteryProviderPipeline.ReadAsync(providers, CancellationToken.None).GetAwaiter().GetResult();
        Check(order.SequenceEqual(new[] { "standard", "vendor" }), "standard providers ordered before vendor fallbacks");
        Check(vendorQueries == 0 && result.Count == 1 && result[0].Key == "standard", "standard success prevents duplicate vendor query");
        var sameNames = new[] { new BatteryProvider("standard", BatteryProviderKind.Standard, 0, (_, _) =>
            Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("a", "physical-a"), Reading("b", "physical-b") })) };
        Check(BatteryProviderPipeline.ReadAsync(sameNames, CancellationToken.None).GetAwaiter().GetResult().Count == 2, "same names on distinct physical devices remain distinct");
        var fallback = new[] {
            new BatteryProvider("standard", BatteryProviderKind.Standard, 0, (_, _) => Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("unknown", "receiver", DeviceState.UnsupportedInterface) })),
            new BatteryProvider("vendor", BatteryProviderKind.VendorFallback, 0, (_, _) => Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("slot-1", "receiver"), Reading("slot-2", "receiver") })) };
        result = BatteryProviderPipeline.ReadAsync(fallback, CancellationToken.None).GetAwaiter().GetResult();
        Check(result.Count == 2 && result.All(d => d.Key.StartsWith("slot-")), "fallback replaces unknown descriptor without collapsing receiver children");
        var unavailableFallback = new[] {
            fallback[0], new BatteryProvider("keyboard", BatteryProviderKind.VendorFallback, 0, (_, _) =>
                Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("keyboard", "receiver", DeviceState.NoReply) })) };
        result = BatteryProviderPipeline.ReadAsync(unavailableFallback, CancellationToken.None).GetAwaiter().GetResult();
        Check(result.Count == 1 && result[0].Key == "keyboard" && result[0].Reading.Percent == null, "receiver without peripheral has one honest unknown card");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool observed = false;
        try { BatteryProviderPipeline.ReadAsync(providers, cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { observed = true; }
        Check(observed, "pipeline cancellation before any provider operation");
        var bounded = new[] { new BatteryProvider("standard", BatteryProviderKind.Standard, 0, (_, _) =>
            Task.FromResult<IReadOnlyList<DeviceBattery>>(Enumerable.Range(0, 100).Select(i => Reading("d" + i, "p" + i)).ToArray())) };
        Check(BatteryProviderPipeline.ReadAsync(bounded, CancellationToken.None).GetAwaiter().GetResult().Count == 64, "combined visible device list bounded");
        int deferredCalls = 0;
        var deferred = new[] {
            new BatteryProvider("pending-standard", BatteryProviderKind.Standard, 0, (context, _) => {
                context.DeferVendorReads = true; return Task.FromResult<IReadOnlyList<DeviceBattery>>(Array.Empty<DeviceBattery>()); }),
            new BatteryProvider("vendor", BatteryProviderKind.VendorFallback, 0, (_, _) => {
                deferredCalls++; return Task.FromResult<IReadOnlyList<DeviceBattery>>(Array.Empty<DeviceBattery>()); }) };
        BatteryProviderPipeline.ReadAsync(deferred, CancellationToken.None).GetAwaiter().GetResult();
        Check(deferredCalls == 0, "pending standard operation defers all vendor I/O");

        var release = new TaskCompletionSource<IReadOnlyList<DeviceBattery>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<IReadOnlyList<DeviceBattery>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<IReadOnlyList<DeviceBattery>>? late = null; int reports = 0;
        var streaming = new[] { new BatteryProvider("streaming", BatteryProviderKind.Standard, 0, (context, _) => {
            late = context.ReportProgress;
            context.ReportProgress?.Invoke(new[] { Reading("fast", "fast") });
            return release.Task;
        }) };
        var pending = Task.Run(() => BatteryProviderPipeline.ReadAsync(streaming, CancellationToken.None, batch => {
            Interlocked.Increment(ref reports); first.TrySetResult(batch);
        }));
        var early = first.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Check(!pending.IsCompleted, "fast result reaches caller before a slow device completes");
        Check(early.Count == 1 && early[0].Key == "fast", "partial result contains only current completed devices");
        release.SetResult(new[] { Reading("fast", "fast"), Reading("slow", "slow") });
        result = pending.GetAwaiter().GetResult();
        Check(result.Count == 2, "complete round retains both early and late device results");
        int beforeLate = Volatile.Read(ref reports);
        late?.Invoke(new[] { Reading("stale", "stale") });
        Check(Volatile.Read(ref reports) == beforeLate, "completed provider cannot publish a late obsolete progress snapshot");

        var data = new MainWindow.DeviceCard("fixture", "fixture", "已連線", "50%", 50,
            System.Windows.Media.Brushes.SandyBrown, "fixture", "未知", "—", "—");
        var model = new MainWindow.DeviceCardViewModel("fixture", data); int changes = 0;
        model.PropertyChanged += (_, _) => changes++;
        model.Update(data);
        Check(changes == 0 && ReferenceEquals(model.Data, data), "unchanged card does not trigger binding or visual-tree work");
        model.Update(data with { BatteryText = "49%", BatteryValue = 49 });
        Check(changes == 1 && model.Key == "fixture" && model.Data.BatteryValue == 49, "existing card updates bindings without replacing its identity");
        var oldReceiver = Reading("known-receiver", "receiver", DeviceState.NoReply);
        var genericReceiver = Reading("generic-receiver", "receiver", DeviceState.UnsupportedInterface);
        var display = MainWindow.MergeProgress(new[] { oldReceiver }, new[] { genericReceiver });
        Check(display.Count == 1 && ReferenceEquals(display[0], oldReceiver), "temporary generic fallback cannot remove an existing receiver card");
        var freshReceiver = Reading("known-receiver", "receiver");
        display = MainWindow.MergeProgress(new[] { oldReceiver }, new[] { freshReceiver });
        Check(display.Count == 1 && ReferenceEquals(display[0], freshReceiver), "fresh receiver reply replaces pending snapshot without a duplicate");
        return count;
    }
    private sealed class GenericChannel : IBluetoothChannel
    {
        public bool Connected => !_disconnected;
        public string ConnectionName => "Fixture Keyboard BT";
        internal string? DirectName;
        internal byte Battery = 94;
        internal byte[]? Notification;
        internal int Observations;
        internal bool DisconnectOnObserve;
        private bool _disconnected;
        public Task<BluetoothBatteryUpdate?> ObserveBatteryAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Observations++;
            _disconnected = DisconnectOnObserve;
            return Task.FromResult(Notification == null ? null : new BluetoothBatteryUpdate(Notification, DateTimeOffset.UtcNow));
        }
        internal readonly List<(ushort, ushort)> Requests = new();
        public Task<byte[]?> ReadAsync(ushort service, ushort characteristic, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); BluetoothProtocol.Validate(service, characteristic); Requests.Add((service, characteristic));
            return Task.FromResult<byte[]?>(characteristic switch { 0x2A29 => Encoding.UTF8.GetBytes("Manufacturer Name"),
                0x2A24 => Encoding.UTF8.GetBytes("Model Number"), 0x2A00 => DirectName == null ? null : Encoding.UTF8.GetBytes(DirectName),
                0x2A19 => new byte[] { Battery }, _ => null });
        }
    }
}
