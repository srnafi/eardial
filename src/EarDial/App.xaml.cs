using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EarDial;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        window.Show();

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

    private static string Describe(Window window)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"window {window.Left:F0},{window.Top:F0} {window.ActualWidth:F0}x{window.ActualHeight:F0} "
            + $"topmost={window.Topmost} visible={window.IsVisible}");

        Walk(window, builder, 0);
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
        }

        int children = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < children; i++)
            Walk(VisualTreeHelper.GetChild(node, i), builder, depth + 1);
    }
}