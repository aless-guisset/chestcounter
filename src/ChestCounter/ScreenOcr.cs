using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ChestCounter;

/// <summary>
/// Lecture de l'écran (OCR intégré à Windows) : capture une fenêtre (Fortnite sur PC, ou la fenêtre d'OBS / du
/// navigateur qui montre la console ou le stream) et lit le texte affiché. Ne touche pas au jeu : c'est une copie
/// de l'image, comme un logiciel de stream.
/// Étape 1 (--ocr-test) : relever ce qui s'affiche quand on ouvre un coffre (texte, position) pour régler la détection.
/// </summary>
public static class ScreenOcr
{
    public sealed record Line(string Text, double X, double Y, double W, double H);

    /// <summary>Lignes qui ressemblent à un gain d'XP ou à un coffre (« +600 XP », « Coffre fouillé »…).</summary>
    public static readonly Regex Interesting = new(@"\bXP\b|coffre|chest|cofre|fouill|search|registr", RegexOptions.IgnoreCase);

    /// <summary>
    /// --ocr-test [minutes] [fenêtre] : toutes les secondes, lit la fenêtre (Fortnite par défaut, sinon la première
    /// fenêtre dont le titre contient le texte donné, ex. « OBS » ou « Twitch ») et note dans ocr\ocr.txt les lignes
    /// intéressantes avec l'heure et leur position (en % de la fenêtre). Garde une capture PNG des 40 premières.
    /// </summary>
    public static int Test(string dataDir, int minutes, string? windowTitle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Console.WriteLine("Windows 10 (2004) ou plus récent requis.");
            return 1;
        }
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // vraies coordonnées d'écran
        var folder = Path.Combine(dataDir, "ocr");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "ocr.txt");
        File.WriteAllText(path, $"== démarrage {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==\n");
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
        {
            File.AppendAllText(path, "OCR Windows indisponible (aucune langue OCR installée).\n");
            return 1;
        }
        File.AppendAllText(path, $"== lecture d'écran ({engine.RecognizerLanguage.DisplayName}) pendant {minutes} min, fenêtre « {windowTitle ?? "Fortnite"} », début {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==\n");
        void Log(string line)
        {
            var l = $"{DateTime.Now:HH:mm:ss.f}  {line}";
            File.AppendAllText(path, l + "\n");
            try { Console.WriteLine(l); } catch { }
        }

        var tracker = new XpPopup.Tracker();
        var counts = new Dictionary<string, int>();
        string Totals() => $"coffres {counts.GetValueOrDefault("chest")} · coffres rares {counts.GetValueOrDefault("rare-chest")} · munitions {counts.GetValueOrDefault("ammo")} · éliminations {counts.GetValueOrDefault("elimination")} · autres {counts.GetValueOrDefault("other") + counts.GetValueOrDefault("unknown")}";
        void Report(int xp, string? label, DateTime at)
        {
            var kind = XpPopup.Kind(label);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            var what = kind switch { "chest" => "COFFRE", "rare-chest" => "COFFRE RARE", "ammo" => "munitions", "elimination" => "élimination", "other" => "autre", _ => "?" };
            var line = $"{at:HH:mm:ss.f}  +{xp} XP · {label ?? "(étiquette illisible)"}  →  {what}   [{Totals()}]";
            File.AppendAllText(path, line + "\n");
            try { Console.WriteLine(line); } catch { }
        }
        var end = DateTime.UtcNow.AddMinutes(minutes);
        var shots = 0;
        var lastNoWindow = DateTime.MinValue;
        while (DateTime.UtcNow < end)
        {
            var started = Stopwatch.StartNew();
            try
            {
                var hwnd = FindWindow(windowTitle);
                if (hwnd == IntPtr.Zero)
                {
                    if (DateTime.UtcNow - lastNoWindow > TimeSpan.FromMinutes(1)) Log($"fenêtre « {windowTitle ?? "Fortnite"} » introuvable (en attente)");
                    lastNoWindow = DateTime.UtcNow;
                    Thread.Sleep(2000);
                    continue;
                }
                if (hwnd != _lastWindow)
                {
                    _lastWindow = hwnd;
                    var sb = new StringBuilder(512);
                    GetWindowText(hwnd, sb, sb.Capacity);
                    GetWindowRect(hwnd, out var wr);
                    Log($"fenêtre lue : « {sb} » ({wr.Right - wr.Left}×{wr.Bottom - wr.Top})");
                }
                if (Capture(hwnd) is { } frame)
                {
                    if (!_firstSaved)
                    {
                        _firstSaved = true; // pour vérifier que c'est bien l'image du jeu qui est lue
                        SavePng(frame, Path.Combine(folder, "premiere-image.png"));
                    }
                    // Zone du gain d'XP, agrandie ×3 puis ×4 : l'une lit souvent ce que l'autre rate.
                    var seen = false;
                    foreach (var scale in new[] { 3, 4 })
                    {
                        var zone = XpPopup.Zone(frame, scale, null);
                        var lines = Recognize(engine, zone);
                        var events = XpPopup.Parse(lines);
                        foreach (var e in events) tracker.Add(e, DateTime.Now);
                        foreach (var l in lines.Where(l => XpPopup.IsUsefulLabel(l.Text) && !events.Any(e => e.Label == l.Text.Trim())))
                            tracker.Add(new XpPopup.Event(0, l.Text.Trim()), DateTime.Now);
                        if (events.Count > 0 && !seen && shots < 80)
                        {
                            seen = true;
                            shots++;
                            SavePng(zone, Path.Combine(folder, $"zone-{DateTime.Now:HHmmss-f}.png"));
                        }
                    }
                }
                foreach (var (xp, label, at) in tracker.Flush(DateTime.Now)) Report(xp, label, at);
            }
            catch (Exception e)
            {
                Log("erreur " + e.Message);
                Thread.Sleep(2000);
            }
            var rest = 500 - (int)started.ElapsedMilliseconds;
            if (rest > 0) Thread.Sleep(rest);
        }
        foreach (var (xp, label, at) in tracker.Flush(DateTime.Now, all: true)) Report(xp, label, at);
        Log($"fin de la lecture · {Totals()}");
        return 0;
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static IntPtr _lastWindow;
    private static bool _firstSaved;

    public sealed record Frame(byte[] Bgra, int Width, int Height);

    /// <summary>Fenêtre de Fortnite (processus du jeu), ou la première fenêtre visible dont le titre contient le texte.</summary>
    public static IntPtr FindWindow(string? title)
    {
        if (title == null)
        {
            foreach (var p in Process.GetProcessesByName("FortniteClient-Win64-Shipping"))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
            }
            return IntPtr.Zero;
        }
        // Fenêtres du programme lui-même (« chiaki » → chiaki-ng.exe) ou dont le titre contient le texte ; la plus grande
        // (Chiaki ouvre une petite fenêtre de menu et une grande pour le jeu).
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (p.ProcessName.Contains(title, StringComparison.OrdinalIgnoreCase)) pids.Add((uint)p.Id); } catch { }
            }
        }
        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            if (!pids.Contains(pid) && !sb.ToString().Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
            if (!GetWindowRect(h, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (pids.Contains(pid)) area *= 4; // le programme passe avant un onglet du navigateur qui en parle
            if (area > bestArea) (best, bestArea) = (h, area);
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>
    /// Image de la fenêtre. D'abord PrintWindow (PW_RENDERFULLCONTENT) : le contenu de la fenêtre même si une autre est
    /// devant (Chiaki, OBS, navigateur). Si l'image est noire (certains jeux en DirectX), copie de l'écran à cet endroit.
    /// </summary>
    public static Frame? Capture(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r) || IsIconic(hwnd)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 200 || h < 150) return null;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            var printed = PrintWindow(hwnd, mem, 2);
            if (printed && Read(mem, bmp, old, w, h) is { } f && !IsBlack(f)) return f;
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000)) return null;
            return Read(mem, bmp, old, w, h);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool IsBlack(Frame f)
    {
        for (var i = 0; i < f.Bgra.Length; i += 4 * 997)
        {
            if (f.Bgra[i] > 12 || f.Bgra[i + 1] > 12 || f.Bgra[i + 2] > 12) return false;
        }
        return true;
    }

    private static Frame? Read(IntPtr mem, IntPtr bmp, IntPtr old, int w, int h)
    {
        SelectObject(mem, old);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        if (GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0) == 0) return null;
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new Frame(pixels, w, h);
    }

    public static List<Line> Recognize(OcrEngine engine, Frame f)
    {
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(f.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, f.Width, f.Height, BitmapAlphaMode.Premultiplied);
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        var list = new List<Line>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x0 = line.Words.Min(w => w.BoundingRect.X), y0 = line.Words.Min(w => w.BoundingRect.Y);
            double x1 = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y1 = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            list.Add(new Line(line.Text, 100 * x0 / f.Width, 100 * y0 / f.Height, 100 * (x1 - x0) / f.Width, 100 * (y1 - y0) / f.Height));
        }
        return list;
    }

    /// <summary>--ocr-file &lt;png&gt;… : lit la zone du gain d'XP sur des captures (réglage sans jouer).</summary>
    public static int TestFiles(IEnumerable<string> files)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) return 1;
        foreach (var file in files)
        {
            var f = LoadPng(file);
            var tr = new XpPopup.Tracker();
            var t0 = DateTime.Now;
            foreach (var scale in new[] { 3, 4 })
            {
                var lines = Recognize(engine, XpPopup.Zone(f, scale, null));
                var events = XpPopup.Parse(lines);
                foreach (var e in events) tr.Add(e, t0);
                foreach (var l in lines.Where(l => XpPopup.IsUsefulLabel(l.Text) && !events.Any(e => e.Label == l.Text.Trim())))
                    tr.Add(new XpPopup.Event(0, l.Text.Trim()), t0);
            }
            var found = tr.Flush(t0, all: true);
            Console.WriteLine($"{Path.GetFileName(file)} : " + (found.Count == 0 ? "rien" : string.Join(" | ", found.Select(e => $"+{e.Xp} XP · {e.Label} → {XpPopup.Kind(e.Label)}"))));
        }
        return 0;
    }

    public static Frame LoadPng(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var w = new Windows.Storage.Streams.DataWriter(mem.GetOutputStreamAt(0)))
        {
            w.WriteBytes(bytes);
            w.StoreAsync().AsTask().GetAwaiter().GetResult();
            w.FlushAsync().AsTask().GetAwaiter().GetResult();
            w.DetachStream();
        }
        var decoder = BitmapDecoder.CreateAsync(mem).AsTask().GetAwaiter().GetResult();
        using var bmp = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        var buf = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyToBuffer(buf.AsBuffer());
        return new Frame(buf, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static void SavePng(Frame f, string path)
    {
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(f.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, f.Width, f.Height, BitmapAlphaMode.Premultiplied);
            using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem).AsTask().GetAwaiter().GetResult();
            encoder.SetSoftwareBitmap(bitmap);
            encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
            var bytes = new byte[mem.Size];
            using var reader = new Windows.Storage.Streams.DataReader(mem.GetInputStreamAt(0));
            reader.LoadAsync((uint)mem.Size).AsTask().GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception e)
        {
            ErrorLog.Write("capture PNG", e);
        }
    }

    // ---- Win32 -----------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
