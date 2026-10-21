using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LeftRightAudioBalancer.Audio;

namespace LeftRightAudioBalancer;

public partial class MainWindow : Window
{
    private List<EndpointState> _devices = new();
    private readonly List<(Slider Slider, TextBlock Readout)> _earRows = new();
    private EndpointState? _current;
    private bool _loading;
    private bool _dragging;
    private bool _linked = true;
    private bool _forceChip;
    private bool _freezeEars;

    // Position lock, not always-on-top. See Pin_Click.
    private bool _locked;
    private readonly AppSettings _settings;

    private readonly ShowDesktopWatcher _watcher;

    // Transport never changes for an endpoint, so it is asked once.
    private readonly Dictionary<string, bool> _bluetooth = new();

    // Smoothed peak level per ear row, 0..1. Rises instantly, falls gradually.
    private readonly double[] _meter = new double[16];
    private readonly System.Windows.Threading.DispatcherTimer _peakTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public MainWindow()
    {
        _settings = SettingsStore.Load();

        InitializeComponent();
        _watcher = new ShowDesktopWatcher(this, _settings.AlwaysOnTop);

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Closed += (_, _) =>
        {
            _watcher.Dispose();
            SaveSettings();
        };
        SourceInitialized += OnSourceInitialized;

        _linked = _settings.Linked;
        LinkToggle.IsChecked = _linked;
        BootSwitch.IsChecked = _settings.StartOnBoot;

        _locked = _settings.Locked;
        PinButton.Tag = _locked ? "on" : "off";
        PinButton.ToolTip = _locked ? "Unlock position" : "Lock position";

        RefreshFixState();
        LoadDevices();

        // One sync loop, wired once. Bind() runs on every device switch, so a
        // handler attached there would multiply with each rebind.
        _poll.Tick += (_, _) => PollExternal();
        _poll.Start();

        _peakTimer.Tick += (_, _) => PollPeaks();
        _peakTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Keeps the widget out of Alt-Tab while staying clickable.
        int style = GetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE);
        SetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    private void SaveSettings()
    {
        _settings.DeviceId = _current?.Id;
        _settings.AlwaysOnTop = _watcher.ManualTopmost;
        _settings.Linked = _linked;
        _settings.StartOnBoot = BootSwitch.IsChecked == true;
        _settings.Locked = _locked;

        // Only remember a position the user actually chose. If the card is
        // still parked in the default corner, leave the stored value alone so a
        // resolution change can still re-place it.
        if (_placedByUser)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }

        SettingsStore.Save(_settings);
    }

    // Set once the user drags the card, so the default corner placement is not
    // mistaken for a chosen position.
    private bool _placedByUser;

    // Window position is only trustworthy once the size-to-content pass has
    // settled. Loaded can still report a provisional ActualHeight, which parked
    // the card mid-screen; ContentRendered runs after the first real layout.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceAtDesktopCorner();
        _watcher.Start();
    }

    private bool _placed;

    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (_placed) return;

        _placed = true;
        PlaceAtDesktopCorner();
    }

    private void PlaceAtDesktopCorner()
    {
        // A remembered position wins, as long as it is still on a visible
        // desktop. Unplugging a monitor can leave the card off-screen.
        if (_settings.WindowLeft is double savedLeft && _settings.WindowTop is double savedTop
            && IsOnScreen(savedLeft, savedTop))
        {
            Left = savedLeft;
            Top = savedTop;
            _placedByUser = true;
            return;
        }

        var area = SystemParameters.WorkArea;

        // ActualWidth/Height can lag SizeToContent by a frame; fall back to the
        // desired size when the measured one is still zero.
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        if (double.IsNaN(width) || double.IsNaN(height) || height <= 0) return;

        Left = area.Right - width - 24;
        Top = area.Bottom - height - 24;
    }

    // Keeps a restored position from stranding the card on a monitor that is
    // no longer attached.
    private bool IsOnScreen(double left, double top)
    {
        const double margin = 60;

        return SystemParameters.VirtualScreenLeft - margin <= left
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - margin
            && SystemParameters.VirtualScreenTop - margin <= top
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - margin;
    }

    private void LoadDevices()
    {
        string? previousId = _current?.Id ?? _settings.DeviceId;

        _devices = EndpointAudio.List();

        _loading = true;
        DeviceBox.ItemsSource = _devices.Select(d => d.Name).ToList();

        int index = previousId is null ? -1 : _devices.FindIndex(d => d.Id == previousId);
        DeviceBox.SelectedIndex = _devices.Count == 0 ? -1 : Math.Max(0, index);
        _loading = false;

        Bind();
    }

    private readonly System.Windows.Threading.DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void Bind()
    {
        _loading = true;
        EarHost.Children.Clear();
        _earRows.Clear();

        _current = DeviceBox.SelectedIndex >= 0 && DeviceBox.SelectedIndex < _devices.Count
            ? _devices[DeviceBox.SelectedIndex]
            : null;

        if (_current is null)
        {
            MasterSlider.IsEnabled = false;
            MasterSlider.Value = 0;
            MasterReadout.Text = "";
            BigReadout.Text = "\u2013";
            LinkOffset.Text = "";
            HeroHint.Visibility = Visibility.Hidden;
            UpdateDeviceHeader();
            ShowChip(ChipKind.None, null, false);
            _loading = false;
            return;
        }

        string id = _current.Id;

        // Push channel: instant refresh when Windows changes this endpoint
        // outside the widget. The timer stays as a fallback for the rest.
        var bound = EndpointAudio.Hook(id, Dispatcher);
        if (bound is not null)
        {
            _current = bound;
            EndpointAudio.EndpointChanged -= OnEndpointChanged;
            EndpointAudio.EndpointChanged += OnEndpointChanged;
        }

        MasterSlider.IsEnabled = true;

        if (_current.CanSplit)
        {
            string[] labels = _current.Channels == 2
                ? new[] { "L", "R" }
                : Enumerable.Range(1, _current.Channels).Select(i => $"C{i}").ToArray();

            for (int i = 0; i < _current.Channels; i++)
                AddEarRow(labels[i], _current.Levels[i], i);

            LinkToggle.IsEnabled = true;
            CentreButton.IsEnabled = true;
        }
        else
        {
            // Ghost pair: keeps the card the same height so switching devices
            // never makes the widget jump under the cursor.
            AddEarRow("L", _current.Levels[0], 0, enabled: false);
            AddEarRow("R", _current.Levels[0], 1, enabled: false);

            LinkToggle.IsEnabled = false;
            CentreButton.IsEnabled = false;
        }

        _loading = true;
        MasterSlider.Value = _current.Master * 100;
        SetMasterReadout(_current.Master * 100);
        _loading = false;

        UpdateOffset();
        RefreshChip();

        _loading = false;
    }

    // External changes (system volume dialog, keyboard keys, another app) must
    // show up here. OnVolumeNotification pushes instantly; the timer is a
    // fallback and also rebinds when the device list changes so a newly
    // connected device appears without hitting Refresh.
    private void PollExternal()
    {
        if (_loading || _dragging) return;
        if (_current is null) { ReloadIfChanged(); return; }

        SyncChannels();
        ReloadIfChanged();
    }

    private void ReloadIfChanged()
    {
        if (_loading || _dragging) return;

        // The default device is only followed when the selected one is gone
        // (unplugged, disconnected). A manual pick in the dropdown wins until
        // that device disappears.
        string? defaultId = EndpointAudio.DefaultId();
        if (defaultId is not null && _current?.Id != defaultId
            && !_devices.Any(d => d.Id == defaultId))
        {
            _settings.DeviceId = defaultId;
            int index = _devices.FindIndex(d => d.Id == defaultId);
            if (index >= 0)
            {
                _loading = true;
                DeviceBox.SelectedIndex = index;
                _loading = false;
                Bind();
                return;
            }
        }

        var list = EndpointAudio.List();
        bool same = list.Count == _devices.Count &&
                    !list.Zip(_devices, (a, b) => a.Id != b.Id).Contains(true);
        if (!same) LoadDevices();
    }

    private void OnEndpointChanged(string id)
    {
        if (_loading || _dragging) return;
        if (_current?.Id != id) return;

        // A device can vanish between the notification and this read
        // (Bluetooth dropout, unplug). The poll's device-list check handles
        // the switch; here we must not take the app down.
        try
        {
            SyncChannels();
        }
        catch
        {
            _current = null;
        }
    }

    private void SyncChannels()
    {
        if (_current is null) return;

        var state = EndpointAudio.Reload(_current.Id);
        if (state is null) return;

        _current = state;

        if (_freezeEars) return;

        _loading = true;
        for (int i = 0; i < _earRows.Count && i < state.Levels.Length; i++)
        {
            double value = state.Levels[i] * 100;
            _earRows[i].Slider.Value = value;
            _earRows[i].Readout.Text = Percent(value);
        }

        if (Math.Abs(MasterSlider.Value - state.Master * 100) > 0.5)
        {
            MasterSlider.Value = state.Master * 100;
            SetMasterReadout(state.Master * 100);
        }

        _loading = false;

        UpdateOffset();
        RefreshChip();
    }

    // Master is the ceiling, not an independent value: it never reads below
    // the loudest ear, in either direction.
    private void RaiseMaster()
    {
        if (_current is null || _earRows.Count == 0) return;

        double highest = _earRows.Max(row => row.Slider.Value);

        _loading = true;
        MasterSlider.Value = highest;
        SetMasterReadout(highest);
        _loading = false;

        EndpointAudio.SetMaster(_current.Id, (float)(highest / 100));
    }

    private void AddEarRow(string label, float value, int index, bool enabled = true)
    {
        var grid = new Grid { Height = 36, Margin = new Thickness(0, index > 0 ? 2 : 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

        // L / R badge: a small rounded tile instead of bare text.
        var badge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("ChipFill"),
            BorderBrush = (Brush)FindResource("ChipBorder"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("SecondaryText"),
            },
        };

        var readout = new TextBlock
        {
            Text = Percent(value * 100),
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("SecondaryText"),
        };

        Typography.SetNumeralAlignment(readout, FontNumeralAlignment.Tabular);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = value * 100,
            IsEnabled = enabled,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("WidgetSlider"),
        };

        if (enabled)
        {
            slider.ValueChanged += (_, args) => OnEarChanged(index, args);

            slider.AddHandler(Thumb.DragStartedEvent, new RoutedEventHandler((_, _) => _dragging = true));
            slider.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler((_, _) =>
            {
                _dragging = false;
                if (_current is { } device) EndpointAudio.NudgeMaster(device.Id);
            }));
        }

        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(badge);
        grid.Children.Add(slider);
        grid.Children.Add(readout);

        if (!enabled) grid.Opacity = 0.35;

        EarHost.Children.Add(grid);
        _earRows.Add((slider, readout));
    }

    private void OnEarChanged(int index, RoutedPropertyChangedEventArgs<double> args)
    {
        if (_loading || _current is null) return;

        _earRows[index].Readout.Text = Percent(args.NewValue);
        EndpointAudio.SetChannel(_current.Id, index, (float)(args.NewValue / 100));

        // Linked moves both ears by the same delta, which preserves the offset
        // rather than flattening it. Unlinked leaves the other ear alone.
        if (_linked && _earRows.Count > 1)
        {
            double delta = args.NewValue - args.OldValue;

            _loading = true;
            for (int i = 0; i < _earRows.Count; i++)
            {
                if (i == index) continue;

                double moved = Math.Clamp(_earRows[i].Slider.Value + delta, 0, 100);
                _earRows[i].Slider.Value = moved;
                _earRows[i].Readout.Text = Percent(moved);
                EndpointAudio.SetChannel(_current.Id, i, (float)(moved / 100));
            }
            _loading = false;
        }

        UpdateOffset();
        RaiseMaster();
    }

    // Signed difference between the ears, always described from the right.
    // Feeds both the Linked pill ("R -12") and the hero hint ("R is 12 quieter").
    private void UpdateOffset()
    {
        bool stereo = _earRows.Count == 2 && _current is { CanSplit: true };

        HeroHint.Visibility = stereo ? Visibility.Visible : Visibility.Hidden;

        if (!stereo)
        {
            LinkOffset.Text = "";
            BalanceText.Text = "";
            return;
        }

        double diff = _earRows[1].Slider.Value - _earRows[0].Slider.Value;
        bool even = Math.Abs(diff) < 0.5;
        string amount = Number(Math.Abs(diff));

        BalanceText.Text = even
            ? "Centred"
            : diff < 0 ? $"R is {amount} quieter" : $"L is {amount} quieter";

        if (!_linked)
        {
            LinkOffset.Text = "";
            return;
        }

        LinkOffset.Text = even
            ? "in sync"
            : diff < 0 ? $"R \u2212{amount}" : $"R +{amount}";
    }

    private void Master_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _current is null) return;

        SetMasterReadout(e.NewValue);
        EndpointAudio.SetMasterScaled(_current.Id, (float)(e.NewValue / 100));

        // Scaling master rescales the channels, so the ears must follow live.
        var state = EndpointAudio.Reload(_current.Id);
        if (state is null) return;

        _loading = true;
        for (int i = 0; i < _earRows.Count && i < state.Levels.Length; i++)
        {
            _earRows[i].Slider.Value = state.Levels[i] * 100;
            _earRows[i].Readout.Text = Percent(state.Levels[i] * 100);
        }
        _loading = false;

        UpdateOffset();
    }

    private void Centre_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || _earRows.Count < 2) return;

        double mean = _earRows.Average(row => row.Slider.Value);

        _loading = true;
        for (int i = 0; i < _earRows.Count; i++)
        {
            _earRows[i].Slider.Value = mean;
            _earRows[i].Readout.Text = Percent(mean);
            EndpointAudio.SetChannel(_current.Id, i, (float)(mean / 100));
        }
        _loading = false;

        UpdateOffset();
        RaiseMaster();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        _linked = LinkToggle.IsChecked == true;
        UpdateOffset();
    }

    // ===================== chip =====================

    private enum ChipKind { None, Info, Warn, Good }

    private void RefreshChip()
    {
        UpdateDeviceHeader();

        if (_forceChip) return;

        if (_current is null || _current.CanSplit)
        {
            ShowChip(ChipKind.None, null, false);
            return;
        }

        if (!IsBluetooth(_current.Id))
        {
            ShowChip(ChipKind.Info, "Single channel \u2014 no left/right to split", false);
            return;
        }

        if (AvrcpFix.IsApplied)
            ShowChip(ChipKind.Good, "Fix applied \u2014 reconnect the headset", false);
        else
            ShowChip(ChipKind.Warn, "Headset reports one channel", true);
    }

    private bool IsBluetooth(string id)
    {
        if (!_bluetooth.TryGetValue(id, out bool value))
        {
            value = EndpointAudio.IsBluetooth(id);
            _bluetooth[id] = value;
        }

        return value;
    }

    // Subtitle and glyph under the device name.
    private void UpdateDeviceHeader()
    {
        if (_current is null)
        {
            DeviceUi.SetIsBluetooth(DeviceBox, false);
            DeviceUi.SetSubtitle(DeviceBox, "No output devices");
            return;
        }

        bool bluetooth = IsBluetooth(_current.Id);

        string subtitle = bluetooth
            ? AvrcpFix.IsApplied ? "Bluetooth \u00B7 Local volume"
              : _current.CanSplit ? "Bluetooth \u00B7 Stereo"
              : "Bluetooth \u00B7 Reports 1 channel"
            : !_current.CanSplit ? "Output \u00B7 Single channel"
              : _current.Channels == 2 ? "Output \u00B7 Stereo"
              : $"Output \u00B7 {_current.Channels} channels";

        DeviceUi.SetIsBluetooth(DeviceBox, bluetooth);
        DeviceUi.SetSubtitle(DeviceBox, subtitle);
    }

    private void ShowChip(ChipKind kind, string? text, bool fixButton)
    {
        if (kind == ChipKind.None || string.IsNullOrEmpty(text))
        {
            Chip.Visibility = Visibility.Collapsed;
            return;
        }

        Chip.Visibility = Visibility.Visible;
        ChipText.Text = text;

        // Amber for the symptom; blue for everything informational or resolved.
        (Color fill, Color stroke, Color textColor) = kind == ChipKind.Warn
            ? (Color.FromArgb(0x1A, 0xFF, 0xB5, 0x47), Color.FromArgb(0x38, 0xFF, 0xB5, 0x47), Color.FromRgb(0xFF, 0xB5, 0x47))
            : (Color.FromArgb(0x1A, 0x5A, 0xA2, 0xFF), Color.FromArgb(0x3D, 0x5A, 0xA2, 0xFF), Color.FromRgb(0x5A, 0xA2, 0xFF));

        Chip.Background = new SolidColorBrush(fill);
        Chip.BorderBrush = new SolidColorBrush(stroke);

        var brush = new SolidColorBrush(textColor);
        ChipText.Foreground = brush;
        ChipGlyph.Stroke = brush;
        ChipBang.Stroke = brush;
        ChipCheck.Stroke = brush;
        ChipInfoRing.Stroke = brush;
        ChipInfoDot.Stroke = brush;

        // Warn: triangle. Good: check. Info: circled i.
        ChipGlyph.Visibility = ChipBang.Visibility = kind == ChipKind.Warn ? Visibility.Visible : Visibility.Collapsed;
        ChipCheck.Visibility = kind == ChipKind.Good ? Visibility.Visible : Visibility.Collapsed;
        ChipInfoRing.Visibility = ChipInfoDot.Visibility = kind == ChipKind.Info ? Visibility.Visible : Visibility.Collapsed;

        ChipAction.Visibility = fixButton ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== settings =====================

    // The pin locks the card in place. It is deliberately not always-on-top:
    // those are separate ideas, and the header only has room for one.
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _locked = !_locked;

        PinButton.Tag = _locked ? "on" : "off";
        PinButton.ToolTip = _locked ? "Unlock position" : "Lock position";
    }

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        bool open = Drawer.Visibility != Visibility.Visible;

        Drawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        GearButton.Tag = open ? "on" : "off";

        RefreshFixState();
    }

    // Diagnostic entry point for --drawer, so the screenshot tool can capture
    // the drawer without driving the mouse.
    internal void OpenDrawer()
    {
        Drawer.Visibility = Visibility.Visible;
        GearButton.Tag = "on";
        RefreshFixState();
    }

    // Diagnostic entry point for --opencombo: opens the device list without a
    // mouse, so the chevron can be captured in its open state.
    internal void OpenDeviceList()
    {
        DeviceBox.IsDropDownOpen = true;
    }

    // Diagnostic entry point for --chip: forces the warning chip so its layout
    // can be measured on a machine with no mono endpoint attached.
    internal void ShowChipForTest()
    {
        _forceChip = true;
        ShowChip(ChipKind.Warn, "Headset reports one channel", true);
    }

    // Diagnostic entry point for --select: picks a device by list index so a
    // mono endpoint can be inspected without touching settings.json.
    internal void SelectDevice(int index)
    {
        if (index < 0 || index >= _devices.Count) return;

        _loading = true;
        DeviceBox.SelectedIndex = index;
        _loading = false;

        Bind();
    }

    // Diagnostic entry point for --ear: positions an ear slider for a capture
    // without writing to the endpoint, so the knob can be checked at the ends
    // of its travel.
    internal void SetEarForTest(int index, double value)
    {
        if (index < 0 || index >= _earRows.Count) return;

        _freezeEars = true;

        _loading = true;
        _earRows[index].Slider.Value = value;
        _earRows[index].Readout.Text = Percent(value);
        _loading = false;
    }

    // Headless behaviour check for --selftest-ui. Drives the real handlers and
    // reports what the widget and the endpoint did, then restores the device.
    internal string RunUiSelfTest()
    {
        var report = new System.Text.StringBuilder();

        void Line(string text) => report.AppendLine(text);

        if (_current is null) return "no device\n";

        string id = _current.Id;
        var before = EndpointAudio.Reload(id);
        if (before is null) return "device vanished\n";

        float left = before.Levels[0];
        float right = before.Levels[1];

        Line($"device {before.Name} channels={before.Channels}");
        Line($"before  L {left:P0}  R {right:P0}  master {before.Master:P0}");

        // Linked: move left up 15 points, right must follow by the same delta.
        _linked = true;
        LinkToggle.IsChecked = true;

        double target = Math.Min(100, _earRows[0].Slider.Value + 15);
        _earRows[0].Slider.Value = target;

        var afterLink = EndpointAudio.Reload(id);
        double delta = target - left * 100;
        double expectedRight = right * 100 + delta;

        Line($"linked drag L->{target:F0}  R {afterLink!.Levels[1]:P0} (expected {expectedRight / 100:P0})  "
             + $"offset \"{LinkOffset.Text}\"  "
             + $"{(Math.Abs(afterLink.Levels[1] * 100 - expectedRight) < 2 ? "held" : "BROKEN")}");

        // Unlinked: moving left must not move right.
        _linked = false;
        LinkToggle.IsChecked = false;
        UpdateOffset();

        float rightBefore = afterLink.Levels[1];
        _earRows[0].Slider.Value = Math.Max(0, _earRows[0].Slider.Value - 20);

        var afterFree = EndpointAudio.Reload(id)!;
        Line($"unlinked drag L  R {(Math.Abs(afterFree.Levels[1] - rightBefore) < 0.02f ? "untouched" : "MOVED")}  "
             + $"offset \"{LinkOffset.Text}\"");

        // Centre: both ears equal.
        Centre_Click(this, new RoutedEventArgs());
        var afterCentre = EndpointAudio.Reload(id)!;
        double gap = Math.Abs(afterCentre.Levels[0] - afterCentre.Levels[1]);
        Line($"centre  L {afterCentre.Levels[0]:P0}  R {afterCentre.Levels[1]:P0}  {(gap < 0.02 ? "equal" : "UNEVEN")}");

        // Master at 90% keeps the balance.
        MasterSlider.Value = 90;
        var afterMaster = EndpointAudio.Reload(id)!;
        Line($"master 90%  master {afterMaster.Master:P0}  L {afterMaster.Levels[0]:P0}  R {afterMaster.Levels[1]:P0}  "
             + $"readout \"{MasterReadout.Text}\"");

        // Restore.
        EndpointAudio.SetMaster(id, before.Master);
        for (int i = 0; i < before.Channels; i++)
            EndpointAudio.SetChannel(id, i, before.Levels[i]);

        var restored = EndpointAudio.Reload(id)!;
        Line($"restored  L {restored.Levels[0]:P0}  R {restored.Levels[1]:P0}  master {restored.Master:P0}");

        _linked = true;
        LinkToggle.IsChecked = true;

        return report.ToString();
    }

    // One-click toggle: shortcut in the Startup folder is the source of truth,
    // the switch follows it.
    private void Boot_Click(object sender, RoutedEventArgs e)
    {
        bool enable = BootSwitch.IsChecked == true;
        try
        {
            SettingsStore.SetStartOnBoot(enable);
            _settings.StartOnBoot = enable;
        }
        catch
        {
            // Shortcut creation can fail on locked-down setups; leave state as-is.
        }
    }

    // ===================== Bluetooth fix =====================

    private void RefreshFixState()
    {
        bool applied = AvrcpFix.IsApplied;

        FixPill.Style = (Style)FindResource(applied ? "PillApplied" : "Pill");
        FixPill.Content = applied ? "\u2713  On" : "\u26E8  Apply";
        FixPill.IsEnabled = !applied;

        FixRevert.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChipFix_Click(object sender, RoutedEventArgs e) => RunFix(true);

    private void FixPill_Click(object sender, RoutedEventArgs e) => RunFix(true);

    private void FixRevert_Click(object sender, RoutedEventArgs e) => RunFix(false);

    private async void RunFix(bool enable)
    {
        FixStatus.Visibility = Visibility.Collapsed;
        FixPill.IsEnabled = false;
        FixPill.Content = enable ? "Applying\u2026" : "Reverting\u2026";

        bool ok = await Task.Run(() => AvrcpFix.Apply(enable));

        RefreshFixState();
        RefreshChip();

        if (ok) return;

        FixStatus.Text = "Admin permission needed. The fix writes a system setting.";
        FixStatus.Visibility = Visibility.Visible;
    }

    // ===================== window =====================

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();

            // DragMove only returns once the drag ends, so this is the drop.
            _placedByUser = true;
        }
    }

    // Diagnostics for --dump. WinExe has no console, so state has to be written
    // out rather than printed.
    internal string DescribeState()
    {
        var state = _current;

        var ears = string.Join(", ", _earRows.Select(r => r.Slider.Value.ToString("F0")));

        return $"state master={MasterSlider.Value:F1} readout=\"{MasterReadout.Text}\" "
             + $"ears=[{ears}] linked={_linked} offset=\"{LinkOffset.Text}\" "
             + $"device={state?.Master.ToString("F2") ?? "-"} chip={(Chip.Visibility == Visibility.Visible ? ChipText.Text : "-")}";
    }

    // Every endpoint with its channel count and transport, so the Bluetooth
    // check can be confirmed without a mono device plugged in.
    internal string DescribeDevices()
    {
        return string.Join("\n", _devices.Select(d =>
            $"  {d.Name}  channels={d.Channels}  transport={(EndpointAudio.IsBluetooth(d.Id) ? "bluetooth" : "wired/other")}"));
    }

    // Geometry dump: every named element's box in card coordinates, so layout
    // can be checked without looking at pixels.
    internal string DescribeGeometry()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"card {Card.ActualWidth:F0}x{Card.ActualHeight:F0}");

        void Box(string name, FrameworkElement element)
        {
            if (element.ActualWidth <= 0 && element.ActualHeight <= 0) return;

            var point = element.TransformToAncestor(Card).Transform(new Point(0, 0));

            builder.AppendLine(
                $"{name,-14} x={point.X,6:F1} y={point.Y,6:F1} w={element.ActualWidth,6:F1} h={element.ActualHeight,5:F1}");
        }

        Box("device", DeviceBox);
        Box("pin", PinButton);
        Box("gear", GearButton);
        Box("chip", Chip);
        Box("ears", EarHost);
        Box("master", MasterSlider);
        Box("vol", (FrameworkElement)MasterSlider.Parent);
        Box("link", LinkToggle);
        Box("centre", CentreButton);
        Box("drawer", Drawer);

        for (int i = 0; i < _earRows.Count; i++)
        {
            Box($"ear{i}.slider", _earRows[i].Slider);
            Box($"ear{i}.num", _earRows[i].Readout);
        }

        Box("masternum", MasterReadout);

        return builder.ToString();
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Bind();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // The master value shows twice: the small readout by the slider and the
    // big number at the top of the card.
    private void SetMasterReadout(double value)
    {
        string text = Number(value);
        MasterReadout.Text = text;
        BigReadout.Text = text;
    }

    // Live level meter: the lighter bar inside each ear's fill. Reads the
    // endpoint's real peak values; a mono endpoint has no ear rows to light.
    private void PollPeaks()
    {
        if (_earRows.Count == 0) return;

        float[]? peaks = _current is { CanSplit: true } device
            ? EndpointAudio.Peaks(device.Id)
            : null;

        for (int i = 0; i < _earRows.Count && i < _meter.Length; i++)
        {
            double target = peaks is not null && i < peaks.Length
                ? Math.Min(1, peaks[i] * 1.5)
                : 0;

            _meter[i] = Math.Max(target, _meter[i] * 0.82);
            if (_meter[i] < 0.01) _meter[i] = 0;

            DeviceUi.SetMeter(_earRows[i].Slider, _meter[i]);
        }
    }

    private static string Percent(double value) => $"{Number(value)}%";

    private static string Number(double value) => Math.Round(value).ToString("F0");
}
