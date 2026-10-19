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
    private readonly AppSettings _settings;

    internal string PinStatus => $"topmost={Topmost}";

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
            MuteBox.IsEnabled = false;
            _loading = false;
            return;
        }

        string id = _current.Id;
        MuteBox.IsEnabled = true;
        MuteBox.IsChecked = _current.Muted;
        MuteState.Text = _current.Muted ? "On" : "Off";

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
                    true,
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
            true,
            value =>
            {
                EndpointAudio.SetMasterScaled(id, value);
                SyncChannels();
            },
            () => { });

        _loading = false;
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

    // Keeps the master at or above the loudest channel so a raised channel is
    // audible instead of being capped by the master level.
    private void RaiseMaster(string id)
    {
        if (_masterRow is not { } master || _channelRows.Count == 0) return;

        float highest = _channelRows.Max(row => (float)row.Slider.Value / 100);
        float current = (float)master.Slider.Value / 100;
        if (highest <= current) return;

        EndpointAudio.SetMaster(id, highest);

        _loading = true;
        master.Slider.Value = highest * 100;
        master.Readout.Text = highest.ToString("P0");
        _loading = false;
    }

    private (Slider Slider, TextBlock Readout) AddRow(string label, float value, bool enabled, Action<float> apply, Action commit)
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
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("WidgetSlider"),
        };

        slider.ValueChanged += (_, args) =>
        {
            if (_loading) return;
            readout.Text = (args.NewValue / 100).ToString("P0");
            apply((float)(args.NewValue / 100));
        };

        slider.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler((_, _) => commit()));

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

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        bool muted = MuteBox.IsChecked == true;
        EndpointAudio.SetMute(_current.Id, muted);
        MuteState.Text = muted ? "On" : "Off";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _watcher.ManualTopmost = !_watcher.ManualTopmost;
        TopmostMenu.IsChecked = _watcher.ManualTopmost;
        _watcher.ApplyManual();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}