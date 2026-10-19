using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EarDial.Audio;

namespace EarDial;

public partial class MainWindow : Window
{
    private List<EndpointState> _devices = new();
    private readonly List<(Slider Slider, TextBlock Readout)> _channelRows = new();
    private EndpointState? _current;
    private bool _loading;
    private bool _dragging;
    private readonly AppSettings _settings;

    private readonly ShowDesktopWatcher _watcher;

    public MainWindow()
    {
        _settings = SettingsStore.Load();

        InitializeComponent();
        _watcher = new ShowDesktopWatcher(this, _settings.AlwaysOnTop);

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _watcher.Dispose();
            SaveSettings();
        };
        SourceInitialized += OnSourceInitialized;
        LoadDevices();

        // One sync loop, wired once. Bind() runs on every device switch, so a
        // handler attached there would multiply with each rebind.
        _poll.Tick += (_, _) => PollExternal();
        _poll.Start();
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

        SettingsStore.Save(_settings);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;

        Left = area.Right - ActualWidth - 24;
        Top = area.Bottom - ActualHeight - 24;

        _watcher.Start();
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

    private (Slider Slider, TextBlock Readout)? _masterRow;
    private readonly System.Windows.Threading.DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void Bind()
    {
        _loading = true;
        SliderHost.Children.Clear();
        _channelRows.Clear();

        _current = DeviceBox.SelectedIndex >= 0 && DeviceBox.SelectedIndex < _devices.Count
            ? _devices[DeviceBox.SelectedIndex]
            : null;

        if (_current is null)
        {
            HintText.Text = "No output devices found.";
            HintText.Visibility = Visibility.Visible;
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

        if (_current.CanSplit)
        {
            HintText.Visibility = Visibility.Collapsed;

            string[] labels = _current.Channels == 2
                ? new[] { "Left", "Right" }
                : Enumerable.Range(1, _current.Channels).Select(i => $"Ch {i}").ToArray();

            for (int i = 0; i < _current.Channels; i++)
            {
                int channel = i;
                _channelRows.Add(AddRow(
                    labels[i],
                    _current.Levels[i],
                    value =>
                    {
                        EndpointAudio.SetChannel(id, channel, value);
                        RaiseMaster(id);
                    },
                    () => EndpointAudio.NudgeMaster(id)));
            }
        }
        else
        {
            HintText.Text = "This device has one channel, so there is no left or right to split.";
            HintText.Visibility = Visibility.Visible;
        }

        _masterRow = AddRow(
            "Master",
            _current.Master,
            value =>
            {
                EndpointAudio.SetMasterScaled(id, value);
                SyncChannels();
            },
            () => { });

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

        _loading = true;
        for (int i = 0; i < _channelRows.Count && i < state.Channels; i++)
        {
            double value = state.Levels[i] * 100;
            _channelRows[i].Slider.Value = value;
            _channelRows[i].Readout.Text = (value / 100).ToString("P0");
        }

        if (_masterRow is { } row && Math.Abs(row.Slider.Value - state.Master * 100) > 0.5)
        {
            row.Slider.Value = state.Master * 100;
            row.Readout.Text = state.Master.ToString("P0");
        }

        _loading = false;
    }

    // Keeps the master equal to the loudest channel, in both directions, so the
    // master never reads below a channel it is playing.
    private void RaiseMaster(string id)
    {
        if (_masterRow is not { } master || _channelRows.Count == 0) return;

        float highest = _channelRows.Max(row => (float)row.Slider.Value / 100);

        _loading = true;
        master.Slider.Value = highest * 100;
        master.Readout.Text = highest.ToString("P0");
        _loading = false;

        EndpointAudio.SetMaster(id, highest);
    }

    private (Slider Slider, TextBlock Readout) AddRow(string label, float value, Action<float> apply, Action commit)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        var name = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("SecondaryText"),
        };

        var readout = new TextBlock
        {
            Text = value.ToString("P0"),
            FontSize = 11,
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
            IsEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("WidgetSlider"),
        };

        slider.ValueChanged += (_, args) =>
        {
            if (_loading) return;
            readout.Text = (args.NewValue / 100).ToString("P0");
            apply((float)(args.NewValue / 100));
        };

        slider.AddHandler(Thumb.DragStartedEvent, new RoutedEventHandler((_, _) => _dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler((_, _) =>
        {
            _dragging = false;
            commit();
        }));

        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        SliderHost.Children.Add(grid);
        return (slider, readout);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Bind();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _watcher.ManualTopmost = !_watcher.ManualTopmost;
        TopmostMenu.IsChecked = _watcher.ManualTopmost;
        _watcher.Apply();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}