using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EarDial.Audio;
using NAudio.CoreAudioApi;

namespace EarDial;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int test = Array.IndexOf(e.Args, "--selftest");
        if (test >= 0 && test + 1 < e.Args.Length)
        {
            File.WriteAllText(e.Args[test + 1], SelfTest());
            Shutdown();
            return;
        }

        var window = new MainWindow();
        window.Show();

        int shot = Array.IndexOf(e.Args, "--shot");
        if (shot >= 0 && shot + 1 < e.Args.Length)
        {
            string shotPath = e.Args[shot + 1];
            var shotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            shotTimer.Tick += (_, _) =>
            {
                shotTimer.Stop();
                Capture(window, shotPath);
                Shutdown();
            };
            shotTimer.Start();
            return;
        }

        int index = Array.IndexOf(e.Args, "--dump");
        if (index < 0 || index + 1 >= e.Args.Length) return;

        string path = e.Args[index + 1];

        // Smoke test: let layout settle, write the visual tree to a file, quit.
        // WinExe has no console, so the dump has to land in a file.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            File.WriteAllText(path, Describe(window));
            Shutdown();
        };
        timer.Start();
    }

    // Renders the window to a PNG at 2x. Screen capture is unreliable under display
    // scaling, so the UI is checked from its own render target.
    private static void Capture(Window window, string path)
    {
        const double scale = 2;

        double width = window.ActualWidth;
        double height = window.ActualHeight;

        var bitmap = new RenderTargetBitmap(
            (int)(width * scale),
            (int)(height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), null, bounds);
            context.DrawRectangle(new VisualBrush(window), null, bounds);
        }

        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Describe(Window window)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"window {window.Left:F0},{window.Top:F0} {window.ActualWidth:F0}x{window.ActualHeight:F0} "
            + $"topmost={window.Topmost} visible={window.IsVisible}");

        Walk(window, builder, 0);
        return builder.ToString();
    }

    // Drops the master to 90% and reports the channel levels before and after,
    // then restores both.
    private static string SelfTest()
    {
        var builder = new StringBuilder();

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string id = device.ID;

        var before = EndpointAudio.Reload(id);
        if (before is null || !before.CanSplit)
        {
            return $"device {device.FriendlyName} has no per-channel volume\n";
        }

        builder.AppendLine($"device {before.Name}");
        builder.AppendLine($"before  master {before.Master:P0}  L {before.Levels[0]:P0}  R {before.Levels[1]:P0}");

        EndpointAudio.SetMasterScaled(id, 0.90f);

        var after = EndpointAudio.Reload(id);
        if (after is not null)
        {
            builder.AppendLine($"master 90%  master {after.Master:P0}  L {after.Levels[0]:P0}  R {after.Levels[1]:P0}");

            // The whole point of SetMasterScaled: dragging master must not move
            // the left/right relationship.
            float ratioBefore = before.Levels[1] / before.Levels[0];
            float ratioAfter = after.Levels[1] / after.Levels[0];
            bool ratioHeld = Math.Abs(ratioAfter - ratioBefore) < 0.01f;
            builder.AppendLine($"balance L/R {ratioBefore:F2} -> {ratioAfter:F2}  {(ratioHeld ? "held" : "BROKEN")}");
        }

        EndpointAudio.SetMaster(id, before.Master);
        for (int i = 0; i < before.Channels; i++)
            EndpointAudio.SetChannel(id, i, before.Levels[i]);

        var restored = EndpointAudio.Reload(id);
        if (restored is not null)
        {
            builder.AppendLine($"restored  master {restored.Master:P0}  L {restored.Levels[0]:P0}  R {restored.Levels[1]:P0}");
        }

        return builder.ToString();
    }

    private static void Walk(DependencyObject node, StringBuilder builder, int depth)
    {
        string pad = new(' ', depth * 2);

        switch (node)
        {
            case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                builder.AppendLine($"{pad}text \"{text.Text}\"");
                break;
            case Slider slider:
                builder.AppendLine($"{pad}slider {slider.Value:F0} enabled={slider.IsEnabled}");
                break;
            case ComboBox combo:
                builder.AppendLine(
                    $"{pad}combo selected={combo.SelectedIndex} items={combo.Items.Count} text=\"{combo.Text}\"");
                break;
            case CheckBox check:
                builder.AppendLine($"{pad}check \"{check.Content}\" = {check.IsChecked}");
                break;
            case ToggleButton toggle:
                builder.AppendLine($"{pad}toggle \"{toggle.Content}\" = {toggle.IsChecked}");
                break;
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
            Walk(VisualTreeHelper.GetChild(node, i), builder, depth + 1);
    }
}