using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PopBat;

internal static class ResourceCheck
{
    // Measures only this process. No game enumeration or other process handles.
    internal static async Task<string> RunAsync(MainWindow window)
    {
        double maxUiDelay = 0;
        using var probeStop = new CancellationTokenSource();
        var probe = Task.Run(async () => {
            try
            {
                while (true)
                {
                    var delay = Stopwatch.StartNew();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, probeStop.Token).Task;
                    maxUiDelay = Math.Max(maxUiDelay, delay.Elapsed.TotalMilliseconds);
                    await Task.Delay(100, probeStop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        await window.InitialRefresh;
        var startupFirst = window.FirstResultMilliseconds;
        double startupTotal = window.LastRefreshMilliseconds;
        int initialRequests = window.RefreshRequests;
        window.StartMonitoring(); // A second start/show must not trigger another hardware round.
        await Task.Delay(TimeSpan.FromSeconds(10));
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        long firstPrivate = process.PrivateMemorySize64, peakWorking = process.WorkingSet64, peakPrivate = firstPrivate;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(125))
        {
            await Task.Delay(1000); process.Refresh();
            peakWorking = Math.Max(peakWorking, process.WorkingSet64);
            peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
        }
        double cpuCorePercent = (process.TotalProcessorTime - cpu).TotalSeconds / watch.Elapsed.TotalSeconds * 100;
        double idleDuration = watch.Elapsed.TotalSeconds;
        long idlePrivateEnd = process.PrivateMemorySize64;
        int idleRequests = window.RefreshRequests;
        var cards = window.CardSnapshot();
        await window.RefreshAsync(true); // Explicit diagnostic action exercises the manual path.
        var manualFirst = window.FirstResultMilliseconds;
        double manualTotal = window.LastRefreshMilliseconds;
        var after = window.CardSnapshot();
        var recreated = cards.Where(p => after.ContainsKey(p.Key) && !ReferenceEquals(p.Value, after[p.Key])).Select(p => p.Value.Data.Name).ToArray();
        bool reused = recreated.Length == 0;
        probeStop.Cancel(); await probe;
        if (initialRequests != 1 || idleRequests != initialRequests || window.RefreshRequests != idleRequests + 1)
            throw new InvalidOperationException("Unexpected automatic or overlapping refresh.");
        if (!reused) throw new InvalidOperationException("Device cards were recreated during refresh.");
        return JsonSerializer.Serialize(new
        {
            Version = "0.4.7", Mode = "StartupAndManualOnly", WarmupSeconds = 10,
            DurationSeconds = Math.Round(idleDuration, 2), window.RefreshCount,
            StartupRequests = initialRequests, RequestsAfterIdle = idleRequests, RequestsAfterManual = window.RefreshRequests,
            NoAutomaticRefresh = idleRequests == initialRequests, CardInstancesReused = reused, RecreatedCards = recreated,
            StartupFirstResultMs = startupFirst, StartupCompleteMs = startupTotal,
            ManualFirstResultMs = manualFirst, ManualCompleteMs = manualTotal,
            MaxUiDispatchDelayMs = Math.Round(maxUiDelay, 2),
            AverageCpuOneCorePercent = Math.Round(cpuCorePercent, 3),
            AverageCpuTotalCapacityPercent = Math.Round(cpuCorePercent / Environment.ProcessorCount, 3),
            PeakWorkingSetMB = Math.Round(peakWorking / 1048576.0, 2),
            PeakPrivateMemoryMB = Math.Round(peakPrivate / 1048576.0, 2),
            PrivateMemoryStartMB = Math.Round(firstPrivate / 1048576.0, 2),
            PrivateMemoryEndMB = Math.Round(idlePrivateEnd / 1048576.0, 2),
            DeviceHistoryWritten = false, GameCompatibilityTested = false
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
