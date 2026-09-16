#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.App.Views.Pages;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Debug builds only (<c>RIGSHIFT_PREVIEW_PAGE=&lt;folder&gt;</c>): renders the games page in its states straight from the
/// visual tree, so it works in a disconnected RDP session where <c>PrintWindow</c> returns black. The app exits afterwards.
/// </summary>
internal static class PagePreview
{
    public static async Task WriteGamesAsync(IServiceProvider services, string directory)
    {
        // A disconnected RDP session has no GPU surface; software rendering still fills a RenderTargetBitmap.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Directory.CreateDirectory(directory);
        MainWindow main = services.GetRequiredService<MainWindow>();
        GamesViewModel games = services.GetRequiredService<GamesViewModel>();
        main.Width = 1280;
        main.Height = 780;
        main.ShowPage(typeof(GamesPage));
        await SettleAsync(main);
        for (int attempt = 0; attempt < 20 && games.Editor is null; attempt++)
        {
            await Task.Delay(250);
            await SettleAsync(main);
        }

        string[] tabs = ["game", "profile", "tools", "windows", "end"];
        for (int i = 0; i < tabs.Length; i++)
        {
            games.SelectedTabIndex = i;
            await SettleAsync(main);
            Render(main, directory, "games-" + tabs[i]);
        }

        if (games.Editor is { } editor)
        {
            editor.StopApps = !editor.StopApps;
            await SettleAsync(main);
            Render(main, directory, "games-dirty");
            editor.Name = string.Empty;
            await SettleAsync(main);
            Render(main, directory, "games-name-problem");
            games.DiscardCommand.Execute(null);
            await SettleAsync(main);
            games.SelectedTabIndex = 0;
            await SettleAsync(main);
            Render(main, directory, "games-after-discard");
        }

        Log.Information("Page preview written to {Directory} ({Width}x{Height})", directory, main.ActualWidth, main.ActualHeight);
    }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Render(Window window, string directory, string name)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }
}
#endif
