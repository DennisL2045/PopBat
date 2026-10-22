using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;
internal static class CompatibilityTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAIL: " + name); count++; }
        Check(DeviceAccessPolicy.AllowsProvider("rog-delta-ii") && DeviceAccessPolicy.AllowsProvider("bluetooth-battery-service")
            && DeviceAccessPolicy.AllowsProvider("logitech-hidpp"), "general scope retains supported wireless protocols");
        Check(!DeviceAccessPolicy.AllowsProvider("arbitrary-command-scan"), "unknown provider fails closed");
        Check(DeltaProtocol.AllowsControlCollection(0xFF00, 1, 64, 64, true), "target vendor control collection permitted");
        Check(!DeltaProtocol.AllowsControlCollection(1, 6, 64, 64, true), "keyboard collection cannot become target control");
        Check(!DeltaProtocol.AllowsControlCollection(0xFF00, 1, 64, 64, false), "missing target report rejected");
        Check(!DeltaProtocol.AllowsControlCollection(0xFF00, 1, 257, 64, true), "oversized control report rejected");
        DeviceBattery Reading(string key) => new(key, "fixture", "fixture", new(DeviceState.Connected, 42, false, DateTimeOffset.UtcNow));
        var stages = new List<BatteryProviderDiagnostic>(); int reads = 0;
        var pending = new[] {
            new BatteryProvider("standard", BatteryProviderKind.Standard, 0, (context, _) => { context.DeferVendorReads = true; return Task.FromResult<IReadOnlyList<DeviceBattery>>(Array.Empty<DeviceBattery>()); }),
            new BatteryProvider("known-control", BatteryProviderKind.Targeted, 0, (_, _) => { reads++; return Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("known") }); }),
            new BatteryProvider("deferred", BatteryProviderKind.VendorFallback, 0, (_, _) => throw new InvalidOperationException("must be deferred"))
        };
        var result = BatteryProviderPipeline.ReadAsync(pending, CancellationToken.None, diagnostics: stages.Add).GetAwaiter().GetResult();
        Check(reads == 1 && result.Any(d => d.Key == "known"), "pending standard operation cannot hide independently targeted battery protocol");
        Check(stages.Any(s => s.Provider == "deferred" && s.Outcome == "Deferred"), "deferred provider reason visible to diagnostics");
        var errors = new[] {
            new BatteryProvider("broken", BatteryProviderKind.Standard, 0, (_, _) => throw new InvalidOperationException("private text must not escape")),
            new BatteryProvider("healthy", BatteryProviderKind.Standard, 1, (_, _) => Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("healthy") }))
        };
        result = BatteryProviderPipeline.ReadAsync(errors, CancellationToken.None).GetAwaiter().GetResult();
        Check(result.Any(d => d.Key == "healthy"), "one provider failure cannot stop following battery protocols");
        Check(result.All(d => d.Note?.Contains("private text") != true), "provider exception payload never exposed");
        var slowRelease = new TaskCompletionSource<IReadOnlyList<DeviceBattery>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var early = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var groups = new IReadOnlyList<BatteryProvider>[] {
            new[] { new BatteryProvider("usb", BatteryProviderKind.Standard, 0, (_, _) => Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] { Reading("usb") })) },
            new[] { new BatteryProvider("bluetooth", BatteryProviderKind.Standard, 0, (_, _) => slowRelease.Task) }
        };
        var work = BatteryProviderPipeline.ReadGroupsAsync(groups, CancellationToken.None, batch => { if (batch.Any(d => d.Key == "usb")) early.TrySetResult(true); });
        try { Check(early.Task.Wait(TimeSpan.FromSeconds(3)) && !work.IsCompleted, "USB result is published while Bluetooth is still pending"); }
        finally { slowRelease.TrySetResult(new[] { Reading("bluetooth") }); }
        result = work.GetAwaiter().GetResult();
        Check(result.Count == 2, "independent transports both remain in final results");
        result = MainWindow.InterruptedRound(new[] { Reading("old") }, new[] { Reading("fresh") });
        Check(result.Any(d => d.Key == "fresh" && d.Reading.Percent == 42), "fresh completed reading survives another device timeout");
        Check(result.Single(d => d.Key == "old").Reading.Percent == null, "unfinished previous-round reading is not presented as fresh");
        var targetFallback = new[] {
            new BatteryProvider("descriptor", BatteryProviderKind.Standard, 0, (_, _) => Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] {
                Reading("generic") with { PhysicalKey = "fixture-receiver", Reading = BatteryReading.Empty(DeviceState.UnsupportedInterface) } })),
            new BatteryProvider("known", BatteryProviderKind.Targeted, 0, (_, _) => Task.FromResult<IReadOnlyList<DeviceBattery>>(new[] {
                Reading("vendor") with { PhysicalKey = "fixture-receiver", Reading = BatteryReading.Empty(DeviceState.NoReply) } }))
        };
        result = BatteryProviderPipeline.ReadAsync(targetFallback, CancellationToken.None).GetAwaiter().GetResult();
        Check(result.Count == 1 && result[0].Key == "vendor", "targeted receiver replaces generic unsupported card even without a battery reply");
        return count;
    }
}