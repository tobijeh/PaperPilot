using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperPilot.Core;
using PaperPilot.UI;

namespace PaperPilot.Diagnostics;

/// <summary>
/// Used by CI only (--smoke-test): opens every Settings page and shows the bubble in each state,
/// saving a picture of each into the log folder, so problems show up before a release.
/// </summary>
internal static class SmokeTest
{
    public static async Task RunAsync(App app, AppController controller)
    {
        try
        {
            app.ShowSettings();
            var window = app.SettingsWindowForTests!;
            foreach (var page in Enum.GetValues<SettingsWindow.PageId>())
            {
                window.ShowPage(page);
                await Task.Delay(900);
                Save(window, $"smoke-settings-{page}.png");
                Log.Info($"Smoke: {page} page rendered.");
            }

            var bubble = controller.Bubble;
            var rng = new Random(1);
            foreach (var (state, word) in new[]
                     {
                         (PilotState.Holding, "Listening"), (PilotState.Following, "Following"),
                         (PilotState.Paused, "Paused"), (PilotState.Problem, "Scroll manually"),
                     })
            {
                bubble.SetState(state, word, IntPtr.Zero);
                for (int i = 0; i < 20; i++)
                {
                    bubble.PushLevel(Math.Max(0, Math.Sin(i * 0.6)) * (0.5 + rng.NextDouble() * 0.5));
                    await Task.Delay(33);
                }
                Save(bubble, $"smoke-bubble-{state}.png");
                Log.Info($"Smoke: bubble {state} rendered.");
            }
            Log.Info("Smoke: done.");
        }
        catch (Exception ex)
        {
            Log.Error("Smoke test failed", ex);
        }
    }

    private static void Save(Window window, string name)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth <= 0) return;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            var fill = window.Background is SolidColorBrush { Color.A: > 0 } b ? b : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            dc.DrawRectangle(fill, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Log.LogFolder);
        using var fs = File.Create(Path.Combine(Log.LogFolder, name));
        enc.Save(fs);
    }
}
