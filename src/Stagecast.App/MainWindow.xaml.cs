using System.Windows;
using Stagecast.Core.Configuration;

namespace Stagecast.App;

/// <summary>The shell. A1 only shows what was loaded; the real screens are built in the following phases.</summary>
public partial class MainWindow : Window
{
    public MainWindow(string version, AppPaths paths, ConfigLoadResult config)
    {
        InitializeComponent();
        var render = config.Config.Render;
        VersionText.Text = $"v{version}";
        ResolutionText.Text = $"{render.Width} × {render.Height}";
        FpsText.Text = $"{render.Fps} fps";
        ConfigText.Text = config.Status switch
        {
            ConfigLoadStatus.Loaded => "Pengaturan dibaca.",
            ConfigLoadStatus.CreatedDefault => "Belum ada pengaturan: memakai bawaan.",
            ConfigLoadStatus.Repaired => $"Sebagian pengaturan tidak valid dan diganti bawaan: {string.Join("; ", config.Notes)}",
            _ => $"Berkas pengaturan rusak, disimpan sebagai salinan dan diganti bawaan. {string.Join("; ", config.Notes)}",
        };
        PathText.Text = paths.ConfigFile;
    }
}
