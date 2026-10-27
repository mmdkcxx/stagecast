using System.Diagnostics;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

// D0 spike: opens each PDF with Windows.Data.Pdf and renders pages scale-to-fit at three canvas sizes.
// Usage: Stagecast.PdfSpike <outDir> <file.pdf>...
// Writes a few PNGs per file to <outDir> for a visual check and prints timings and memory.
args = args.Where(a => a != "--").ToArray();
string outDir = args[0];
Directory.CreateDirectory(outDir);
(int W, int H)[] targets = { (2520, 1080), (1920, 1080), (3840, 2160) };

foreach (string path in args.Skip(1))
{
    Console.WriteLine($"=== {Path.GetFileName(path)} ({new FileInfo(path).Length / 1048576.0:F1} MB)");
    long memBefore = Process.GetCurrentProcess().WorkingSet64;
    var sw = Stopwatch.StartNew();
    // Same as the planned app: open from a temporary copy so the original stays untouched.
    string temp = Path.Combine(Path.GetTempPath(), "pdfspike-" + Guid.NewGuid().ToString("N") + ".pdf");
    File.Copy(path, temp);
    double copyMs = sw.Elapsed.TotalMilliseconds;
    PdfDocument doc;
    try
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(temp);
        doc = await PdfDocument.LoadFromFileAsync(file);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"OPEN FAILED: {ex.GetType().Name}: {ex.Message}");
        continue;
    }
    Console.WriteLine($"copy {copyMs:F0} ms, open {sw.Elapsed.TotalMilliseconds - copyMs:F0} ms, pages {doc.PageCount}, password {doc.IsPasswordProtected}");

    // Page sizes and rotation summary.
    var sizes = new Dictionary<string, int>();
    for (uint i = 0; i < doc.PageCount; i++)
    {
        using PdfPage p = doc.GetPage(i);
        string key = $"{p.Size.Width:F0}x{p.Size.Height:F0} rot{p.Rotation}";
        sizes[key] = sizes.GetValueOrDefault(key) + 1;
    }
    Console.WriteLine("page sizes: " + string.Join(", ", sizes.Select(kv => $"{kv.Key} x{kv.Value}")));

    // Sample: first 8 pages plus up to 12 spread over the rest.
    var sample = Enumerable.Range(0, (int)Math.Min(8, doc.PageCount)).ToList();
    if (doc.PageCount > 8)
        for (int k = 1; k <= 12; k++) sample.Add((int)((doc.PageCount - 1) * k / 12));
    sample = sample.Distinct().ToList();

    string stem = new string(Path.GetFileNameWithoutExtension(path).Where(char.IsLetterOrDigit).ToArray());
    foreach (var (tw, th) in targets)
    {
        foreach (string format in new[] { "png", "bmp" })
        {
            var times = new List<double>();
            long bytes = 0;
            foreach (int index in sample)
            {
                using PdfPage page = doc.GetPage((uint)index);
                double scale = Math.Min(tw / page.Size.Width, th / page.Size.Height);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Round(page.Size.Width * scale),
                    DestinationHeight = (uint)Math.Round(page.Size.Height * scale),
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                };
                if (format == "bmp") options.BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId;
                using var stream = new InMemoryRandomAccessStream();
                var t = Stopwatch.StartNew();
                await page.RenderToStreamAsync(stream, options);
                times.Add(t.Elapsed.TotalMilliseconds);
                bytes += (long)stream.Size;
                // Save a few for the visual check (first page, a middle page, the last page).
                if (format == "png" && (index == sample[0] || index == sample[Math.Min(4, sample.Count - 1)] || index == sample[^1]))
                {
                    stream.Seek(0);
                    using var fs = File.Create(Path.Combine(outDir, $"{stem}_{tw}x{th}_p{index + 1}.png"));
                    await stream.AsStreamForRead().CopyToAsync(fs);
                }
            }
            // The first render in a run includes warm-up; report it separately.
            Console.WriteLine($"{tw}x{th} {format}: first {times[0]:F0} ms, avg(rest) {times.Skip(1).Average():F0} ms, worst {times.Max():F0} ms, avg size {bytes / times.Count / 1024.0:F0} KB ({times.Count} pages)");
        }
    }

    // Memory: render every page (capped at 150) at 2520x1080 as bmp and drop each result, to see steady-state growth.
    int all = (int)Math.Min(doc.PageCount, 150);
    var all_sw = Stopwatch.StartNew();
    for (int index = 0; index < all; index++)
    {
        using PdfPage page = doc.GetPage((uint)index);
        double scale = Math.Min(2520 / page.Size.Width, 1080 / page.Size.Height);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Round(page.Size.Width * scale),
            DestinationHeight = (uint)Math.Round(page.Size.Height * scale),
            BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId,
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
    }
    GC.Collect(); GC.WaitForPendingFinalizers();
    long memAfter = Process.GetCurrentProcess().WorkingSet64;
    Console.WriteLine($"all {all} pages 2520x1080 bmp: {all_sw.Elapsed.TotalSeconds:F1} s ({all_sw.Elapsed.TotalMilliseconds / all:F0} ms/page), working set {memBefore / 1048576} -> {memAfter / 1048576} MB");
    try { File.Delete(temp); } catch { }
}
