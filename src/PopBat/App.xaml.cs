using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopBat;

public partial class App : Application
{
    private Mutex? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (Array.IndexOf(e.Args, "--self-test") >= 0) { Shutdown(SelfTests.Run()); return; }
            _instance = new Mutex(false, "Local\\PopBat-DeltaII-01");
            bool acquired;
            try { acquired = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                if (e.Args.Length == 0) MessageBox.Show("PopBat 已在執行。請從右下角通知區開啟視窗。", "PopBat");
                else Console.WriteLine("PopBat already running; no second device reader started.");
                _instance.Dispose(); _instance = null; Shutdown(2); return;
            }
            if (Array.IndexOf(e.Args, "--resource-check") >= 0)
            {
                var window = new MainWindow(diagnostics: true); MainWindow = window; window.StartMonitoring();
                string report = await ResourceCheck.RunAsync(window);
                string? destination = Option(e.Args, "--output");
                if (destination != null) File.WriteAllText(destination, report);
                Console.WriteLine(report); window.ExitApp(); return;
            }
            if (Array.IndexOf(e.Args, "--probe-keyboard") >= 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var reader = new LegacyKeyboardReader(diagnostics: true);
                var readings = await reader.ReadAsync(timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Version = "0.4.5", Queries = reader.Inspection, Devices = readings.Select(d => new {
                    d.Name, d.Connection, d.Reading.State, d.Reading.Percent, d.Reading.Charging, d.Note }) }, new JsonSerializerOptions { WriteIndented = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
                Shutdown(readings.Any(d => d.Reading.State == DeviceState.Connected) ? 0 : 1); return;
            }
            if (Array.IndexOf(e.Args, "--inspect-hid-collections") >= 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var devices = await StandardHidReader.InspectCollectionsAsync(timeout.Token).WaitAsync(timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Version = "0.4.5", Devices = devices }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (Array.IndexOf(e.Args, "--inspect-battery") >= 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var devices = await BluetoothBatteryInspection.RunAsync(timeout.Token).WaitAsync(timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Version = "0.4.5", Source = "DirectGattUncached", NameSource = "CurrentConnectionName", Devices = devices }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (Array.IndexOf(e.Args, "--inspect-hid") >= 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var inventory = await StandardHidReader.InspectAsync(timeout.Token).WaitAsync(timeout.Token);
                // Descriptor metadata only; never expose identity grouping keys, addresses or paths.
                Console.WriteLine(JsonSerializer.Serialize(new { Version = "0.4.5", Devices = inventory.Select(d => new {
                    d.Name, d.Manufacturer, VendorId = d.Vendor.ToString("X4"), ProductId = d.Product.ToString("X4"),
                    d.Usb, d.Peripheral, d.InputBattery, d.SafeFeatureBattery }) }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (Array.IndexOf(e.Args, "--probe") >= 0 || Array.IndexOf(e.Args, "--probe-all") >= 0 || Array.IndexOf(e.Args, "--preview") >= 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                bool legacy = Array.IndexOf(e.Args, "--probe") >= 0;
                var readings = legacy ? new[] { new DeviceBattery("delta", "ROG Delta II", "2.4 GHz", await new DeltaHidReader().ReadAsync(timeout.Token)) }
                    : await new DeviceMonitor().ReadAsync(timeout.Token);
                var reading = readings.Count > 0 ? readings[0].Reading : BatteryReading.Empty(DeviceState.NoReply);
                object result = legacy ? new { Device = "ROG Delta II", VendorId = "0B05", ProductId = "1AFA", reading.State, reading.Percent, reading.Charging, reading.CheckedAt, reading.TargetInterfaces, reading.VendorInterfaces }
                    : new { Version = "0.4.5", Devices = readings.Select(d => new { d.Name, d.Connection, d.Reading.State, d.Reading.Percent, d.Reading.Charging, d.Level, d.Note, d.Reading.CheckedAt }).ToArray() };
                string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
                string? output = Option(e.Args, "--output");
                if (Array.IndexOf(e.Args, "--preview") >= 0)
                {
                    var window = new MainWindow(true); window.ApplyReadings(readings);
                    // Render only this app's detached visual tree; never capture the desktop.
                    var content = (System.Windows.Controls.Border)window.Content;
                    window.Content = null; content.Background = window.Background;
                    System.Windows.Documents.TextElement.SetForeground(content, window.Foreground);
                    System.Windows.Documents.TextElement.SetFontFamily(content, window.FontFamily);
                    content.Measure(new Size(window.Width, window.Height)); content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
                    var image = new RenderTargetBitmap((int)(window.Width * 1.5), (int)(window.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
                    image.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(output ?? Path.Combine(AppContext.BaseDirectory, "preview.png")); encoder.Save(file);
                }
                else if (output != null) File.WriteAllText(output, json);
                Console.WriteLine(json);
                Shutdown(readings.Any(d => d.Reading.State == DeviceState.Connected) ? 0 : 1); return;
            }
            var main = new MainWindow(); MainWindow = main;
            if (Array.IndexOf(e.Args, "--background") < 0) main.Show();
            main.StartMonitoring();
        }
        catch (Exception)
        {
            // Never expose native paths, raw reports, serials, or exception payloads.
            if (e.Args.Length == 0) MessageBox.Show("PopBat 無法完成啟動。請確認程式資料夾可以存取，並重新執行。", "PopBat");
            else Console.WriteLine("PopBat operation failed; no private diagnostic data retained.");
            Shutdown(3);
        }
    }
    private static string? Option(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance != null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
