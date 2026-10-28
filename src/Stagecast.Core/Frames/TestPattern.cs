namespace Stagecast.Core.Frames;

/// <summary>
/// The picture an output shows when no tab is live, or when the operator asks for it: colour bars, a grey ramp, a checker, a circle for
/// the anamorphic squeeze, a border for cropping, and a moving part (timecode, sweeping bar, BEEP box) for freeze, frame rate and sound
/// sync. Everything is sized from the render size. The background is drawn once per size; every frame only copies it and draws the
/// moving part. The text uses a small built-in pixel font so no font or window system is needed.
/// </summary>
public sealed class TestPattern
{
    /// <summary>The sweeping bar crosses the frame in this many seconds.</summary>
    public const int SweepSeconds = 2;

    /// <summary>A beep (and the box that flashes with it) starts every second and lasts this long.</summary>
    public const int BeepMilliseconds = 100;

    private static readonly uint[] BarColours = [0xFFFFFF, 0xFFFF00, 0x00FFFF, 0x00FF00, 0xFF00FF, 0xFF0000, 0x0000FF];

    private static readonly long Epoch = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Time on the one clock that the picture and the sound of the pattern share, so the beep box and the beep start together.</summary>
    public static TimeSpan Now() => System.Diagnostics.Stopwatch.GetElapsedTime(Epoch);

    private readonly FramePool _pool = new();
    private byte[]? _background;
    private int _width, _height, _fps;

    /// <summary>True during the first <see cref="BeepMilliseconds"/> of each second; the sound and the box use the same rule.</summary>
    public static bool BeepOn(TimeSpan elapsed) => elapsed.Ticks % TimeSpan.TicksPerSecond < TimeSpan.FromMilliseconds(BeepMilliseconds).Ticks;

    /// <summary>Renders the pattern as it looks <paramref name="elapsed"/> after it started. The caller releases the frame.</summary>
    public Frame Render(int width, int height, int fps, TimeSpan elapsed)
    {
        if (_background is null || width != _width || height != _height || fps != _fps)
        {
            _background = DrawBackground(width, height, fps);
            (_width, _height, _fps) = (width, height, fps);
        }

        Frame frame = _pool.Rent(width, height, width * 4);
        Buffer.BlockCopy(_background, 0, frame.Pixels, 0, _background.Length);
        DrawMoving(frame.Pixels, width, height, fps, elapsed);
        frame.CapturedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        return frame;
    }

    // ---- geometry shared by the background and the moving part ----

    private static int StripTop(int height) => (int)(height * 0.84);

    private static byte[] DrawBackground(int w, int h, int fps)
    {
        var px = new byte[w * h * 4];
        int barsEnd = (int)(h * 0.42), rampEnd = (int)(h * 0.54), checkerEnd = StripTop(h);

        for (int i = 0; i < BarColours.Length; i++)
            Fill(px, w, h, i * w / 7, 0, (i + 1) * w / 7, barsEnd, BarColours[i]);
        for (int i = 0; i < 11; i++)
        {
            uint v = (uint)(i * 255 / 10);
            Fill(px, w, h, i * w / 11, barsEnd, (i + 1) * w / 11, rampEnd, (v << 16) | (v << 8) | v);
        }

        int cell = Math.Max(2, (int)Math.Round(15.0 * h / 540));
        for (int y = rampEnd; y < checkerEnd; y += cell)
            for (int x = 0; x < w; x += cell)
                Fill(px, w, h, x, y, Math.Min(x + cell, w), Math.Min(y + cell, checkerEnd), ((x / cell + (y - rampEnd) / cell) & 1) == 0 ? 0x101010u : 0x707070u);
        Fill(px, w, h, 0, checkerEnd, w, h, 0x000000);

        // Faint grid and the two diagonals.
        for (int c = 1; c < 10; c++) Blend(px, w, h, c * w / 10, 0, c * w / 10 + 1, h, 0xFFFFFF, 90);
        for (int r = 1; r < 6; r++) Blend(px, w, h, 0, r * h / 6, w, r * h / 6 + 1, 0xFFFFFF, 90);
        Line(px, w, h, 0, 0, w - 1, h - 1, 0xFFFFFF, 90);
        Line(px, w, h, w - 1, 0, 0, h - 1, 0xFFFFFF, 90);

        // The circle must look round after the output has undone any squeeze; the crosshair marks the centre.
        int cx = w / 2, cy = h / 2, radius = (int)(h * 0.34), thick = Math.Max(2, h / 270);
        Ring(px, w, h, cx, cy, radius + thick, radius + thick + 1, 0x000000);
        Ring(px, w, h, cx, cy, radius, radius + thick, 0xFFFFFF);
        Ring(px, w, h, cx, cy, radius - 1, radius, 0x000000);
        Fill(px, w, h, cx - radius, cy, cx + radius, cy + 1, 0xFFFFFF);
        Fill(px, w, h, cx, cy - radius, cx + 1, cy + radius, 0xFFFFFF);

        // The plate with the numbers.
        int scale = Math.Max(1, h / 135);
        string[] lines = [$"{w} X {h}", $"STAGECAST TEST PATTERN - {fps} FPS", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RASIO {(double)w / h:0.00}:1")];
        int[] sizes = [scale * 2, scale, scale];
        int plateW = 0, plateH = scale * 4;
        for (int i = 0; i < lines.Length; i++)
        {
            plateW = Math.Max(plateW, TextWidth(lines[i], sizes[i]));
            plateH += 7 * sizes[i] + scale * 2;
        }

        plateW += scale * 8;
        int py = cy - plateH / 2;
        Fill(px, w, h, cx - plateW / 2, py, cx + plateW / 2, py + plateH, 0x000000);
        int ty = py + scale * 3;
        for (int i = 0; i < lines.Length; i++)
        {
            Text(px, w, h, cx - TextWidth(lines[i], sizes[i]) / 2, ty, sizes[i], lines[i], 0xFFFFFF);
            ty += 7 * sizes[i] + scale * 2;
        }

        // Corner marks and the one-pixel frame at the very edge.
        int mark = h / 18, edge = Math.Max(1, h / 540);
        foreach (var (x, y, dx, dy) in new[] { (0, 0, 1, 1), (w - 1, 0, -1, 1), (0, h - 1, 1, -1), (w - 1, h - 1, -1, -1) })
        {
            Fill(px, w, h, Math.Min(x, x + dx * mark), Math.Min(y, y + dy * edge * 3), Math.Max(x, x + dx * mark) + 1, Math.Max(y, y + dy * edge * 3) + 1, 0xFFFF00);
            Fill(px, w, h, Math.Min(x, x + dx * edge * 3), Math.Min(y, y + dy * mark), Math.Max(x, x + dx * edge * 3) + 1, Math.Max(y, y + dy * mark) + 1, 0xFFFF00);
        }

        Fill(px, w, h, 0, 0, w, edge, 0xFFFFFF);
        Fill(px, w, h, 0, h - edge, w, h, 0xFFFFFF);
        Fill(px, w, h, 0, 0, edge, h, 0xFFFFFF);
        Fill(px, w, h, w - edge, 0, w, h, 0xFFFFFF);
        return px;
    }

    private static void DrawMoving(byte[] px, int w, int h, int fps, TimeSpan elapsed)
    {
        int top = StripTop(h), strip = h - top, scale = Math.Max(1, h / 135);
        int totalFrames = (int)(elapsed.TotalSeconds * fps);
        int ff = totalFrames % fps, seconds = (int)elapsed.TotalSeconds;
        string code = $"{seconds / 3600 % 100:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{ff:00}";
        int margin = scale * 4;
        Fill(px, w, h, margin - scale, top + scale, margin + TextWidth("00:00:00:00", scale * 2) + scale, top + scale + 7 * scale * 2 + scale * 2, 0x000000);
        Text(px, w, h, margin, top + scale * 2, scale * 2, code, 0xFFFFFF);

        // The sweep: a white bar that crosses the frame every SweepSeconds.
        double phase = elapsed.TotalSeconds % SweepSeconds / SweepSeconds;
        int barW = Math.Max(4, w / 40), barX = (int)(phase * (w - barW));
        Fill(px, w, h, barX, top + strip - strip / 4, barX + barW, h - Math.Max(1, h / 540), 0xFFFFFF);

        // The beep box.
        bool beep = BeepOn(elapsed);
        string label = $"BEEP {seconds % 100:00}";
        int boxW = TextWidth(label, scale * 2) + scale * 6, boxH = 7 * scale * 2 + scale * 4;
        int boxX = w - boxW - margin, boxY = top + scale;
        Fill(px, w, h, boxX, boxY, boxX + boxW, boxY + boxH, beep ? 0xFFFFFFu : 0x202020u);
        Text(px, w, h, boxX + scale * 3, boxY + scale * 2, scale * 2, label, beep ? 0x000000u : 0xFFFFFFu);
    }

    // ---- drawing ----

    private static void Put(byte[] px, int w, int x, int y, uint rgb)
    {
        int o = (y * w + x) * 4;
        px[o] = (byte)rgb;
        px[o + 1] = (byte)(rgb >> 8);
        px[o + 2] = (byte)(rgb >> 16);
        px[o + 3] = 255;
    }

    private static void Fill(byte[] px, int w, int h, int x0, int y0, int x1, int y1, uint rgb)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(w, x1); y1 = Math.Min(h, y1);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) Put(px, w, x, y, rgb);
    }

    private static void Blend(byte[] px, int w, int h, int x0, int y0, int x1, int y1, uint rgb, int alpha)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(w, x1); y1 = Math.Min(h, y1);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * w + x) * 4;
                px[o] = (byte)((px[o] * (255 - alpha) + (byte)rgb * alpha) / 255);
                px[o + 1] = (byte)((px[o + 1] * (255 - alpha) + (byte)(rgb >> 8) * alpha) / 255);
                px[o + 2] = (byte)((px[o + 2] * (255 - alpha) + (byte)(rgb >> 16) * alpha) / 255);
            }
    }

    private static void Line(byte[] px, int w, int h, int x0, int y0, int x1, int y1, uint rgb, int alpha)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
        {
            int x = x0 + (x1 - x0) * i / Math.Max(1, steps), y = y0 + (y1 - y0) * i / Math.Max(1, steps);
            Blend(px, w, h, x, y, x + 1, y + 1, rgb, alpha);
        }
    }

    private static void Ring(byte[] px, int w, int h, int cx, int cy, int inner, int outer, uint rgb)
    {
        long in2 = (long)inner * inner, out2 = (long)outer * outer;
        for (int y = Math.Max(0, cy - outer); y < Math.Min(h, cy + outer + 1); y++)
            for (int x = Math.Max(0, cx - outer); x < Math.Min(w, cx + outer + 1); x++)
            {
                long d = (long)(x - cx) * (x - cx) + (long)(y - cy) * (y - cy);
                if (d >= in2 && d < out2) Put(px, w, x, y, rgb);
            }
    }

    private static int TextWidth(string text, int scale) => text.Length * 6 * scale - scale;

    private static void Text(byte[] px, int w, int h, int x, int y, int scale, string text, uint rgb)
    {
        foreach (char c in text)
        {
            if (Glyphs.TryGetValue(c, out string[]? rows))
                for (int r = 0; r < 7; r++)
                    for (int col = 0; col < 5; col++)
                        if (rows[r][col] == '#') Fill(px, w, h, x + col * scale, y + r * scale, x + (col + 1) * scale, y + (r + 1) * scale, rgb);
            x += 6 * scale;
        }
    }

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = ["####.", "....#", "....#", ".###.", "....#", "....#", "####."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ['6'] = ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
        ['C'] = [".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."],
        ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
        ['F'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#...."],
        ['G'] = [".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###."],
        ['I'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
        ['N'] = ["#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#"],
        ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['P'] = ["####.", "#...#", "#...#", "####.", "#....", "#....", "#...."],
        ['R'] = ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
        ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['X'] = ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
        [':'] = [".....", "..#..", "..#..", ".....", "..#..", "..#..", "....."],
        ['.'] = [".....", ".....", ".....", ".....", ".....", ".....", "..#.."],
        ['-'] = [".....", ".....", ".....", ".###.", ".....", ".....", "....."],
        [' '] = [".....", ".....", ".....", ".....", ".....", ".....", "....."],
    };
}
