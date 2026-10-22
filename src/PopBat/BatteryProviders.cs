using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal enum BatteryProviderKind { Standard, VendorFallback }
internal sealed class BatteryQueryContext
{
    internal readonly HashSet<string> CoveredPhysicalKeys = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<HidDeviceInventory> HidInventory = Array.Empty<HidDeviceInventory>();
    internal bool DeferVendorReads;
    internal Action<IReadOnlyList<DeviceBattery>>? ReportProgress;
}
internal sealed record BatteryProvider(string Id, BatteryProviderKind Kind, int Priority,
    Func<BatteryQueryContext, CancellationToken, Task<IReadOnlyList<DeviceBattery>>> ReadAsync);

internal static class BatteryProviderPipeline
{
    internal static async Task<IReadOnlyList<DeviceBattery>> ReadAsync(IReadOnlyList<BatteryProvider> providers, CancellationToken token,
        Action<IReadOnlyList<DeviceBattery>>? progress = null)
    {
        var context = new BatteryQueryContext(); var output = new List<DeviceBattery>();
        foreach (var provider in providers.OrderBy(p => p.Kind).ThenBy(p => p.Priority))
        {
            token.ThrowIfCancellationRequested();
            if (provider.Kind == BatteryProviderKind.VendorFallback && context.DeferVendorReads) continue;
            // A provider can publish completed devices while another device is still waiting.
            // Capture this stage's base so a delayed callback cannot mutate later stages.
            var completed = output.ToArray();
            var covered = context.CoveredPhysicalKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            int stageActive = 1;
            context.ReportProgress = progress == null ? null : batch => {
                if (token.IsCancellationRequested || Volatile.Read(ref stageActive) == 0) return;
                var partial = completed.ToList();
                Merge(partial, batch, covered, provider.Kind);
                progress(Visible(partial));
            };
            IReadOnlyList<DeviceBattery> readings;
            try { readings = await provider.ReadAsync(context, token); }
            finally { Volatile.Write(ref stageActive, 0); }
            token.ThrowIfCancellationRequested();
            var fresh = Merge(output, readings, context.CoveredPhysicalKeys, provider.Kind);
            context.CoveredPhysicalKeys.UnionWith(fresh);
            progress?.Invoke(Visible(output));
        }
        return Visible(output);
    }
    private static IReadOnlyList<DeviceBattery> Visible(IReadOnlyList<DeviceBattery> output)
    {
        bool hasDevices = output.Any(d => d.PhysicalKey != null || d.Reading.State == DeviceState.Connected);
        return output.Where(d => !hasDevices || d.Key != "bluetooth-status").Take(64).ToArray();
    }
    private static HashSet<string> Merge(List<DeviceBattery> output, IReadOnlyList<DeviceBattery> readings,
        IReadOnlySet<string> covered, BatteryProviderKind kind)
    {
            var accepted = readings.Where(d => d.PhysicalKey == null || !covered.Contains(d.PhysicalKey)).Take(64).ToArray();
            var fresh = accepted.Where(d => d.PhysicalKey != null && d.Reading.State == DeviceState.Connected)
                .Select(d => d.PhysicalKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            output.RemoveAll(d => d.PhysicalKey != null && fresh.Contains(d.PhysicalKey));
            if (kind == BatteryProviderKind.VendorFallback)
            {
                var known = accepted.Where(d => d.PhysicalKey != null && d.Reading.State != DeviceState.UnsupportedInterface)
                    .Select(d => d.PhysicalKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                output.RemoveAll(d => d.PhysicalKey != null && known.Contains(d.PhysicalKey) && d.Reading.State == DeviceState.UnsupportedInterface);
            }
            output.AddRange(accepted);
            // Claim after adding the whole batch: a receiver may contain several peripherals.
            return fresh;
    }
}
