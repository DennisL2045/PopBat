using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal enum BatteryProviderKind { Standard, Targeted, VendorFallback }
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
        Action<IReadOnlyList<DeviceBattery>>? progress = null, Action<BatteryProviderDiagnostic>? diagnostics = null)
    {
        var context = new BatteryQueryContext(); var output = new List<DeviceBattery>();
        foreach (var provider in providers.OrderBy(p => p.Kind).ThenBy(p => p.Priority))
        {
            token.ThrowIfCancellationRequested();
            if (provider.Kind == BatteryProviderKind.VendorFallback && context.DeferVendorReads) { diagnostics?.Invoke(new(provider.Id, "Deferred", 0, 0, 0)); continue; }
            var watch = System.Diagnostics.Stopwatch.StartNew();
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
            IReadOnlyList<DeviceBattery> readings; string outcome = "Completed";
            try { readings = await provider.ReadAsync(context, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { diagnostics?.Invoke(new(provider.Id, "Cancelled", watch.ElapsedMilliseconds, 0, 0)); throw; }
            catch (Exception) {
                outcome = "Failed";
                readings = new[] { new DeviceBattery(provider.Id + "-status", "電量查詢暫時失敗", provider.Id,
                    BatteryReading.Empty(DeviceState.ReadError), Note: "此電池協定未完成；其他裝置查詢會繼續。") };
            }
            finally { Volatile.Write(ref stageActive, 0); }
            token.ThrowIfCancellationRequested();
            diagnostics?.Invoke(new(provider.Id, outcome, watch.ElapsedMilliseconds, readings.Count, readings.Count(d => d.Reading.State == DeviceState.Connected)));
            var fresh = Merge(output, readings, context.CoveredPhysicalKeys, provider.Kind);
            context.CoveredPhysicalKeys.UnionWith(fresh);
            progress?.Invoke(Visible(output));
        }
        return Visible(output);
    }
    // Separate transports retain independent pending flags and publish results independently.
    internal static async Task<IReadOnlyList<DeviceBattery>> ReadGroupsAsync(IReadOnlyList<IReadOnlyList<BatteryProvider>> groups,
        CancellationToken token, Action<IReadOnlyList<DeviceBattery>>? progress = null, Action<BatteryProviderDiagnostic>? diagnostics = null)
    {
        var sync = new object();
        var latest = new Dictionary<int, IReadOnlyList<DeviceBattery>>();
        IReadOnlyList<DeviceBattery> Combined() {
            var result = new List<DeviceBattery>(); var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in latest.OrderBy(p => p.Key)) {
                var batch = group.Value.Where(d => d.PhysicalKey == null || !covered.Contains(d.PhysicalKey)).ToArray();
                var fresh = batch.Where(d => d.PhysicalKey != null && d.Reading.State == DeviceState.Connected).Select(d => d.PhysicalKey!).ToArray();
                result.RemoveAll(d => d.PhysicalKey != null && fresh.Contains(d.PhysicalKey, StringComparer.OrdinalIgnoreCase) && d.Reading.State != DeviceState.Connected);
                result.AddRange(batch); covered.UnionWith(fresh);
            }
            return Visible(result);
        }
        var tasks = groups.Select((group, index) => Task.Run(async () => {
            void Report(IReadOnlyList<DeviceBattery> batch) {
                lock (sync) {
                    if (token.IsCancellationRequested) return;
                    latest[index] = batch; progress?.Invoke(Combined());
                }
            }
            var result = await ReadAsync(group, token, Report, diagnostics);
            lock (sync) latest[index] = result;
        }, token)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (sync) return Combined();
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
            if (kind is BatteryProviderKind.VendorFallback or BatteryProviderKind.Targeted)
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
