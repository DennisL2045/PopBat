using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PopBat;

public partial class MainWindow : Window
{
    private readonly DeviceMonitor _monitor = new();
    private readonly BatteryHistory _history;
    private readonly PreferenceStore _preferences;
    private readonly Dictionary<string, BatteryHistory> _otherHistory = new();
    private readonly Dictionary<string, BatteryReading> _lastGood = new();
    private readonly Dictionary<string, string> _names = new();
    private readonly HashSet<string> _lowNotified = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<DeviceCardViewModel> _cards = new();
    private CancellationTokenSource? _currentQuery;
    private Forms.NotifyIcon? _tray;
    private bool _checking, _exiting, _ready, _started;
    private readonly bool _preview, _diagnostics;
    private IReadOnlyList<DeviceBattery> _current = Array.Empty<DeviceBattery>();
    internal int RefreshCount { get; private set; }
    internal int RefreshRequests { get; private set; }
    internal double? FirstResultMilliseconds { get; private set; }
    internal double LastRefreshMilliseconds { get; private set; }
    internal Task InitialRefresh { get; private set; } = Task.CompletedTask;
    internal IReadOnlyDictionary<string, DeviceCardViewModel> CardSnapshot() => _cards.ToDictionary(c => c.Key);
    private static readonly Brush GoodBatteryBrush = CreateGoodBatteryBrush();
    private static Brush CreateGoodBatteryBrush()
    { var brush = new SolidColorBrush(Color.FromRgb(184, 244, 107)); brush.Freeze(); return brush; }

    public MainWindow(bool preview = false, bool diagnostics = false)
    {
        _preview = preview; _diagnostics = diagnostics;
        string directory = Path.Combine(AppContext.BaseDirectory, "data");
        _history = new BatteryHistory(directory, !preview && !diagnostics);
        _preferences = new PreferenceStore(directory);
        var preferences = preview || diagnostics ? new Preferences(false, false) : _preferences.Load();
        InitializeComponent();
        DeviceCards.ItemsSource = _cards;
        NotifyCheck.IsChecked = preferences.Notify;
        _history.SaveEnabled = preferences.SaveHistory;
        _ready = true;
        if (!preview)
        {
            CreateTray();
            StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        }
        Closing += OnClosing;
    }
    internal void StartMonitoring()
    { if (_started) return; _started = true; InitialRefresh = RefreshAsync(); }
    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("顯示 PopBat", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("立即更新", null, (_, _) => Dispatcher.Invoke(async () => await RefreshAsync(true)));
        menu.Items.Add("取消本次更新", null, (_, _) => Dispatcher.Invoke(CancelRefresh));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("結束程式", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray = new Forms.NotifyIcon { Icon = CreateTrayIcon(), Visible = true, Text = "PopBat · 無線裝置電量", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var drawing = System.Drawing.Graphics.FromImage(bitmap))
        {
            drawing.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            drawing.Clear(System.Drawing.Color.Transparent);
            using var outline = new System.Drawing.Pen(System.Drawing.Color.FromArgb(184, 244, 107), 2);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(184, 244, 107));
            drawing.DrawRectangle(outline, 3, 8, 23, 16); drawing.FillRectangle(fill, 7, 12, 15, 8); drawing.FillRectangle(fill, 27, 13, 3, 6);
        }
        IntPtr handle = bitmap.GetHicon();
        try { using var temporary = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    public async Task RefreshAsync(bool retry = false)
    {
        if (_checking || _exiting) return;
        _checking = true; RefreshRequests++; FirstResultMilliseconds = null;
        var watch = Stopwatch.StartNew();
        RefreshButton.IsEnabled = false; CancelButton.IsEnabled = true;
        CheckText.Text = "更新中 · 結果會逐筆顯示";
        RenderCards(_current, new HashSet<string>());
        using var query = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        query.CancelAfter(TimeSpan.FromSeconds(20)); _currentQuery = query;
        void Report(IReadOnlyList<DeviceBattery> batch)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                if (!ReferenceEquals(_currentQuery, query) || query.IsCancellationRequested || _exiting) return;
                if (FirstResultMilliseconds == null && batch.Any(d => d.Reading.State == DeviceState.Connected))
                    FirstResultMilliseconds = watch.Elapsed.TotalMilliseconds;
                ApplyProgress(batch);
            }));
        }
        try
        {
            var readings = await _monitor.ReadAsync(query.Token, retry, Report);
            if (_exiting) return;
            query.Token.ThrowIfCancellationRequested();
            if (FirstResultMilliseconds == null && readings.Any(d => d.Reading.State == DeviceState.Connected))
                FirstResultMilliseconds = watch.Elapsed.TotalMilliseconds;
            RefreshCount++;
            var keys = readings.Select(d => d.Key).ToHashSet();
            foreach (string stale in _otherHistory.Keys.Where(k => !keys.Contains(k)).ToArray())
            { _otherHistory.Remove(stale); _lastGood.Remove(stale); _names.Remove(stale); _lowNotified.Remove(stale); }
            foreach (var device in readings)
            {
                if (_names.TryGetValue(device.Key, out string? old) && old != device.Name)
                { _otherHistory.Remove(device.Key); _lastGood.Remove(device.Key); _lowNotified.Remove(device.Key); }
                _names[device.Key] = device.Name;
                HistoryFor(device.Key).Add(device.Reading);
                if (device.Reading.State == DeviceState.Connected) _lastGood[device.Key] = device.Reading;
                if (!_diagnostics && device.Reading.State == DeviceState.Connected && device.Reading.Percent <= 20 && device.Reading.Charging == false && NotifyCheck.IsChecked == true && _lowNotified.Add(device.Key))
                    _tray?.ShowBalloonTip(7000, "裝置電量偏低", $"{device.Name}：目前 {device.Reading.Percent}%。", Forms.ToolTipIcon.Warning);
                if (device.Reading.Percent > 25 || device.Reading.Charging == true) _lowNotified.Remove(device.Key);
            }
            ApplyReadings(readings);
        }
        catch (OperationCanceledException)
        {
            if (!_exiting)
            {
                foreach (var history in _otherHistory.Values) history.Add(BatteryReading.Empty(DeviceState.NoReply));
                _history.Add(BatteryReading.Empty(DeviceState.NoReply));
                ApplyReadings(_current.Select(d => d with { Reading = BatteryReading.Empty(DeviceState.NoReply), Level = null }).ToArray());
            }
        }
        finally
        {
            LastRefreshMilliseconds = watch.Elapsed.TotalMilliseconds;
            _checking = false; _currentQuery = null;
            RefreshButton.IsEnabled = true; CancelButton.IsEnabled = false;
            if (!_exiting) CheckText.Text = query.IsCancellationRequested
                ? "更新已停止 · 可再手動更新" : $"讀取 {DateTime.Now:HH:mm:ss} · 僅手動更新";
        }
    }
    private BatteryHistory HistoryFor(string key)
    {
        if (key == "delta") return _history;
        if (!_otherHistory.TryGetValue(key, out var history))
            _otherHistory[key] = history = new BatteryHistory(Path.Combine(AppContext.BaseDirectory, "data"), false) { SaveEnabled = false };
        return history;
    }
    public void ApplyReading(BatteryReading reading) => ApplyReadings(new[] { new DeviceBattery("delta", "ROG Delta II", "2.4 GHz · USB 接收器", reading) });
    public void ApplyReadings(IReadOnlyList<DeviceBattery> readings)
    {
        _current = readings; RenderCards(readings);
        int count = readings.Count(d => d.Reading.State == DeviceState.Connected);
        LogitechSummary.Text = count > 0 ? $"已直接讀取 {count} 筆裝置電量；通用協定優先。" : "尚未直接讀到裝置電量。請確認裝置已連線並喚醒；不顯示舊配對紀錄。";
        if (_tray != null)
        {
            string text = string.Join(" · ", readings.Where(d => d.Reading.State == DeviceState.Connected).Select(d => d.Name + " " + (d.Reading.Percent is int percent ? percent + "%" : d.Level ?? "未知")));
            if (text.Length == 0) text = "尚未取得裝置電量";
            text = "PopBat · " + text; _tray.Text = text[..Math.Min(63, text.Length)];
        }
    }
    private void ApplyProgress(IReadOnlyList<DeviceBattery> readings)
    {
        var keys = readings.Select(d => d.Key).ToHashSet();
        RenderCards(MergeProgress(_current, readings), keys);
        LogitechSummary.Text = $"已收到 {readings.Count(d => d.Reading.State == DeviceState.Connected)} 筆電量，正在完成其他裝置查詢…";
    }
    internal static IReadOnlyList<DeviceBattery> MergeProgress(IReadOnlyList<DeviceBattery> previous, IReadOnlyList<DeviceBattery> readings)
    {
        // A temporary generic 'unsupported' row must not replace an existing vendor card
        // while that receiver's fallback is still being read. Reconcile removals at completion.
        var visible = readings.Where(d => d.Reading.State != DeviceState.UnsupportedInterface || d.PhysicalKey == null ||
            !previous.Any(p => p.PhysicalKey == d.PhysicalKey && p.Key != d.Key && p.Reading.State != DeviceState.UnsupportedInterface)).ToArray();
        var keys = visible.Select(d => d.Key).ToHashSet();
        var physical = visible.Where(d => d.PhysicalKey != null).Select(d => d.PhysicalKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return visible.Concat(previous.Where(d => !keys.Contains(d.Key) && (d.PhysicalKey == null || !physical.Contains(d.PhysicalKey)))).Take(64).ToArray();
    }
    private void RenderCards(IReadOnlyList<DeviceBattery> readings, IReadOnlySet<string>? fresh = null)
    {
        int position = 0;
        foreach (var reading in readings)
        {
            var data = ToCard(reading);
            if (fresh != null && !fresh.Contains(reading.Key)) data = data with { StateText = "更新中" };
            var card = _cards.FirstOrDefault(c => c.Key == reading.Key);
            if (card == null) { card = new(reading.Key, data); _cards.Insert(position, card); }
            else { card.Update(data); int previous = _cards.IndexOf(card); if (previous != position) _cards.Move(previous, position); }
            position++;
        }
        while (_cards.Count > position) _cards.RemoveAt(_cards.Count - 1);
    }
    private DeviceCard ToCard(DeviceBattery device)
    {
        var r = device.Reading; var estimate = device.AllowEstimate ? HistoryFor(device.Key).Estimate(r) : null;
        string state = r.State switch { DeviceState.Connected => "已連線", DeviceState.ReceiverMissing => "未找到接收器", DeviceState.HeadsetOffline => "耳機離線", DeviceState.NotReady => "尚未就緒", DeviceState.NoReply => "未取得回覆", DeviceState.UnsupportedInterface => "尚未支援", DeviceState.AccessDenied => "存取受限", DeviceState.UnverifiedReading => "電量未核實", _ => "讀取失敗" };
        string detail = r.State switch
        {
            DeviceState.Connected => r.Percent == null ? "裝置未提供精確百分比，顯示其電量等級或電壓。" : r.Charging == null ? "已取得電量；充電狀態未知，續航估算暫停。" : "電量直接來自裝置回覆。",
            DeviceState.ReceiverMissing => "請插入 Delta II 的 2.4 GHz USB 接收器。",
            DeviceState.UnsupportedInterface => "此介面未提供支援的電量協定。程式不會嘗試未知指令。",
            DeviceState.AccessDenied => "装置介面存取受限；程式不會提升權限或更換驅動。",
            _ => "本次沒有有效電量。離線或無回覆時會減少查詢頻率，不以舊值冒充目前電量。"
        };
        if (device.Key.StartsWith("ble-", StringComparison.Ordinal) || device.Key == "bluetooth-status")
        {
            detail = r.State == DeviceState.Connected ? "電量經藍牙 GATT 直接向裝置讀取；充電狀態未知，續航估算暫停。"
                : device.Level ?? detail;
        }
        if (device.Note != null) detail = device.Note;
        return new(device.Name, device.Connection, state, r.State == DeviceState.Connected ? r.Percent is int percent ? percent + "%" : device.Level ?? "未知" : "—", r.Percent ?? 0,
            r.Percent <= 20 ? Brushes.SandyBrown : GoodBatteryBrush, detail,
            r.State != DeviceState.Connected ? "未知" : r.Charging switch { true => "充電中", false => "未充電", _ => "未知" },
            r.State != DeviceState.Connected ? "—" : !device.AllowEstimate ? "暫不提供" : r.Percent == null ? "無精確電量可估算" : r.Charging == true ? "充電中" : r.Charging == null ? "充電狀態未知" : estimate == null ? "累積資料中" : $"約 {estimate.LowHours:0.#}–{estimate.HighHours:0.#} 小時",
            r.State == DeviceState.Connected ? r.CheckedAt.ToLocalTime().ToString("MM/dd HH:mm:ss")
                : _lastGood.TryGetValue(device.Key, out var last) ? last.CheckedAt.ToLocalTime().ToString("MM/dd HH:mm:ss") : "—");
    }
    public sealed record DeviceCard(string Name, string Connection, string StateText, string BatteryText, double BatteryValue, Brush BatteryBrush, string Detail, string ChargingText, string EstimateText, string LastSuccessText);
    public sealed class DeviceCardViewModel : INotifyPropertyChanged
    {
        public string Key { get; }
        public DeviceCard Data { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public DeviceCardViewModel(string key, DeviceCard data) { Key = key; Data = data; }
        public void Update(DeviceCard data)
        { if (Data == data) return; Data = data; PropertyChanged?.Invoke(this, new(nameof(Data))); }
    }
    private void Preferences_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool saved = _preview || _diagnostics || _preferences.Save(new(_history.SaveEnabled, NotifyCheck.IsChecked == true));
        NotifyCheck.ToolTip = saved ? null : "偏好設定無法寫入，本次選擇僅適用於目前執行期間。";
    }
    private void CancelRefresh() { _currentQuery?.Cancel(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { _history.Clear(); _otherHistory.Clear(); ApplyReadings(_current); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRefresh();
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Hide_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void HideToTray() { Hide(); WindowState = WindowState.Normal; }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_exiting && !_preview) { e.Cancel = true; HideToTray(); } }
    internal void ExitApp() { _exiting = true; _lifetime.Cancel(); _tray?.Dispose(); _tray = null; Close(); Application.Current.Shutdown(); }
}
