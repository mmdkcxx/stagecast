using System.IO;
using System.Text;
using System.Windows;

namespace Stagecast.Pattern;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Self test for the build scripts: --selftest <folder> renders a few short videos and writes selftest.log there.
        int at = Array.IndexOf(e.Args, "--selftest");
        if (at >= 0 && at + 1 < e.Args.Length)
        {
            Shutdown(SelfTest.Run(e.Args[at + 1]));
            return;
        }

        var window = new MainWindow();
        window.Show();

        // --shot <png>: save a picture of the window and quit (used to look at the layout without a screen).
        int shot = Array.IndexOf(e.Args, "--shot");
        if (shot >= 0 && shot + 1 < e.Args.Length)
        {
            string path = e.Args[shot + 1];
            window.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var f = File.Create(path)) encoder.Save(f);
                Shutdown(0);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
