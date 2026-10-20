using System.Windows;

namespace LeftRightAudioBalancer;

// Attached properties the control templates read. They let the device selector
// show a subtitle and a Bluetooth/speaker glyph, and let each ear slider draw
// its live level meter, without any extra controls in MainWindow.xaml.
public static class DeviceUi
{
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.RegisterAttached("Subtitle", typeof(string), typeof(DeviceUi),
            new PropertyMetadata(""));

    public static string GetSubtitle(DependencyObject d) => (string)d.GetValue(SubtitleProperty);
    public static void SetSubtitle(DependencyObject d, string value) => d.SetValue(SubtitleProperty, value);

    public static readonly DependencyProperty IsBluetoothProperty =
        DependencyProperty.RegisterAttached("IsBluetooth", typeof(bool), typeof(DeviceUi),
            new PropertyMetadata(false));

    public static bool GetIsBluetooth(DependencyObject d) => (bool)d.GetValue(IsBluetoothProperty);
    public static void SetIsBluetooth(DependencyObject d, bool value) => d.SetValue(IsBluetoothProperty, value);

    // 0..1: how much of the slider fill the live peak meter covers.
    public static readonly DependencyProperty MeterProperty =
        DependencyProperty.RegisterAttached("Meter", typeof(double), typeof(DeviceUi),
            new PropertyMetadata(0.0));

    public static double GetMeter(DependencyObject d) => (double)d.GetValue(MeterProperty);
    public static void SetMeter(DependencyObject d, double value) => d.SetValue(MeterProperty, value);
}
