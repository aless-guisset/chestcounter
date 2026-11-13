using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Media.Ocr;

namespace ChestCounter;

/// <summary>
/// Lecture de l'écran en continu (fil à part) : la fenêtre de Fortnite sur PC, ou celle qui montre la console (Chiaki,
/// OBS…). Chaque gain d'XP lu (« +600 XP · CHEST ») devient un événement : coffres et boîtes de munitions comptés en
/// direct. Le menu du lobby dit si on est en partie. Tout est noté dans journal\AAAA-MM-JJ.txt (journal de partie).
/// Ne touche pas au jeu : c'est une copie de l'image, comme un logiciel de stream.
/// </summary>
public static class LiveScreen
{
    public sealed record Seen(DateTime At, int Xp, string? Label, string Kind);

    /// <summary>Gains lus, à vider par la boucle principale.</summary>
    public static readonly ConcurrentQueue<Seen> Events = new();

    /// <summary>Fenêtre trouvée et lue en ce moment.</summary>
    public static bool Active => DateTime.UtcNow - _lastFrame < TimeSpan.FromSeconds(5);

    /// <summary>Menu du lobby visible (pas en partie). Null tant qu'on ne sait pas.</summary>
    public static bool? InLobby { get; private set; }

    public static string? WindowTitle { get; private set; }

    /// <summary>Mode lu sur la carte du lobby (dernier vu) et mode de la partie en cours (figé en quittant le lobby).</summary>
    public static string? LobbyMode { get; private set; }
    public static string? MatchMode { get; private set; }

    /// <summary>Réglage des modes comptés en direct : "br" (défaut), "br+reload", "all".</summary>
    public static volatile string? ModesSetting;

    /// <summary>Le mode est-il compté ? Mode pas lu (null) : oui, mieux vaut compter que rater une partie.</summary>
    public static bool Allowed(string? setting, string? mode) => (setting ?? "br") switch
    {
        "all" => true,
        "br+reload" => mode is null or "br" or "reload",
        _ => mode is null or "br",
    };

    /// <summary>En partie, dans un mode compté, avec l'écran lu : le direct est actif.</summary>
    public static bool Counting => Active && InLobby == false && Allowed(ModesSetting, MatchMode);

    public static string ModeName(string? mode) => mode switch
    {
        "br" => "Battle Royale",
        "reload" => L.T("Recharge", "Reload", "Recarga"),
        "other" => L.T("autre mode", "other mode", "otro modo"),
        _ => L.T("mode pas lu", "mode not read", "modo no leído"),
    };

    public static string DescribeModes(string? setting) => (setting ?? "br") switch
    {
        "all" => L.T("tous les modes", "all modes", "todos los modos"),
        "br+reload" => L.T("Battle Royale + Recharge", "Battle Royale + Reload", "Battle Royale + Recarga"),
        _ => L.T("Battle Royale seulement", "Battle Royale only", "solo Battle Royale"),
    };

    public static string NextModes(string? setting) => (setting ?? "br") switch { "br" => "br+reload", "br+reload" => "all", _ => "br" };

    /// <summary>Réglage → fenêtre à lire : null = rien, "" = Fortnite (PC), sinon un mot du titre / du programme.</summary>
    public static string? Target(string? setting, bool console) => (setting ?? "auto").ToLowerInvariant() switch
    {
        "off" => null,
        "auto" => console ? "chiaki|remoteplay" : "",
        "fortnite" => "",
        var s => s,
    };

    public static string Describe(string? setting, bool console) => Target(setting, console) switch
    {
        null => L.T("désactivée", "off", "desactivada"),
        "" => L.T("fenêtre de Fortnite (PC)", "Fortnite window (PC)", "ventana de Fortnite (PC)"),
        "chiaki|remoteplay" => L.T("Chiaki ou PS Remote Play", "Chiaki or PS Remote Play", "Chiaki o PS Remote Play"),
        "chiaki" => L.T("Chiaki (PS5 à distance)", "Chiaki (PS5 remote play)", "Chiaki (PS5 a distancia)"),
        "remoteplay" => L.T("PS Remote Play (appli Sony)", "PS Remote Play (Sony app)", "PS Remote Play (app de Sony)"),
        "obs" => L.T("OBS (carte de capture)", "OBS (capture card)", "OBS (capturadora)"),
        var t => t,
    } + ((setting ?? "auto") == "auto" ? L.T(" · auto", " · auto", " · auto") : "");

    /// <summary>Réglage suivant (Entrée dans les Réglages).</summary>
    public static string Next(string? setting) => (setting ?? "auto") switch
    {
        "auto" => "fortnite",
        "fortnite" => "chiaki",
        "chiaki" => "remoteplay",
        "remoteplay" => "obs",
        "obs" => "off",
        _ => "auto",
    };

    private static Thread? _thread;
    private static volatile bool _stop;
    private static DateTime _lastFrame = DateTime.MinValue;
    private static string _journalFolder = "";

    /// <summary>Démarre le fil (une fois). target() est relu à chaque tour : changer le réglage suffit.</summary>
    public static void Start(Func<string?> target, string dataFolder)
    {
        if (_thread != null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        _journalFolder = Path.Combine(dataFolder, "journal");
        _stop = false;
        _thread = new Thread(() => Loop(target)) { IsBackground = true, Name = "LiveScreen", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public static void Stop() => _stop = true;

    private static void Loop(Func<string?> target)
    {
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { } // vraies tailles de fenêtre, sans toucher au reste de l'appli
        OcrEngine? engine;
        try { engine = OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch (Exception e) { ErrorLog.Write("lecture d'écran", e); return; }
        if (engine == null) return;

        var tracker = new XpPopup.Tracker();
        var combo = new XpPopup.Combo();
        var hwnd = IntPtr.Zero;
        string? current = null;
        var nextFind = DateTime.MinValue;
        var nextLobby = DateTime.MinValue;
        while (!_stop)
        {
            var started = Environment.TickCount64;
            try
            {
                var t = target();
                if (t != current) (current, hwnd, nextFind) = (t, IntPtr.Zero, DateTime.MinValue);
                if (current == null)
                {
                    InLobby = null;
                    Thread.Sleep(2000);
                    continue;
                }
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                {
                    hwnd = IntPtr.Zero;
                    if (DateTime.UtcNow < nextFind) { Thread.Sleep(1000); continue; }
                    hwnd = ScreenOcr.FindWindow(current.Length == 0 ? null : current);
                    nextFind = DateTime.UtcNow.AddSeconds(5);
                    if (hwnd == IntPtr.Zero) { InLobby = null; continue; }
                    WindowTitle = ScreenOcr.Title(hwnd);
                }
                if (ScreenOcr.Capture(hwnd) is not { } frame) { Thread.Sleep(1000); continue; }
                _lastFrame = DateTime.UtcNow;

                if (DateTime.UtcNow >= nextLobby)
                {
                    nextLobby = DateTime.UtcNow.AddSeconds(4);
                    var lobby = XpPopup.IsLobby(ScreenOcr.Recognize(engine, XpPopup.Top(frame)));
                    if (lobby && XpPopup.ModeOf(ScreenOcr.Recognize(engine, XpPopup.Left(frame))) is { } mode) LobbyMode = mode;
                    if (InLobby == true && !lobby) MatchMode = LobbyMode; // on quitte le lobby : le mode de la partie
                    if (InLobby != null && lobby != InLobby)
                        Journal(lobby ? "── " + L.T("retour au lobby", "back to the lobby", "vuelta al lobby")
                            : $"── {L.T("partie commencée", "match started", "partida empezada")} ({ModeName(MatchMode)}{(Allowed(ModesSetting, MatchMode) ? "" : L.T(", pas comptée", ", not counted", ", no contada"))})");
                    InLobby = lobby;
                }
                if (InLobby != true && Allowed(ModesSetting, MatchMode))
                {
                    // ×3 suffit en grand (1080p) ; en petit, ×4 lit souvent ce que ×3 rate.
                    foreach (var scale in frame.Width >= 1500 ? new[] { 3 } : new[] { 3, 4 })
                    {
                        var lines = ScreenOcr.Recognize(engine, XpPopup.Zone(frame, scale, null));
                        var events = XpPopup.Parse(lines);
                        foreach (var e in events) tracker.Add(e, DateTime.Now);
                        foreach (var l in lines.Where(l => XpPopup.IsUsefulLabel(l.Text) && !events.Any(e => e.Label == l.Text.Trim())))
                            tracker.Add(new XpPopup.Event(0, l.Text.Trim()), DateTime.Now);
                    }
                }
                foreach (var (xp, label, at) in tracker.Flush(DateTime.Now).OrderBy(f => f.At))
                {
                    var kind = XpPopup.Kind(label);
                    var credit = IsChest(kind) || kind == "ammo" ? combo.Credit(kind, label, xp, at) : 1;
                    for (var i = 0; i < credit; i++) Events.Enqueue(new Seen(at, i == 0 ? xp : 0, label, kind));
                    Journal($"{at:HH:mm:ss}  +{xp} XP · {label ?? "?"}  →  {KindName(kind)}"
                        + (credit == 0 ? L.T(" (même pile, déjà compté)", " (same stack, already counted)", " (misma pila, ya contado)")
                        : credit > 1 ? $" ×{credit}" : ""), at);
                }
            }
            catch (Exception e)
            {
                ErrorLog.Write("lecture d'écran", e);
                Thread.Sleep(2000);
            }
            var rest = 500 - (int)(Environment.TickCount64 - started);
            if (rest > 0) Thread.Sleep(rest);
        }
        _thread = null;
    }

    // ---- PC : la partie en cours lue à l'écran (le replay reste illisible jusqu'à la fin) ----------------------------

    public static int PcChests { get; private set; }
    public static int PcAmmo { get; private set; }
    /// <summary>L'écran a été lu pendant la partie en cours : PcChests est un vrai chiffre (même 0).</summary>
    public static bool PcRead { get; private set; }
    private static bool _pcWasLive;

    /// <summary>Boucle principale (PC) : vide les gains lus. Nouvelle partie (replay qui commence) → remise à zéro.</summary>
    public static bool DrainForPc(bool matchInProgress)
    {
        var changed = false;
        if (matchInProgress && !_pcWasLive)
        {
            (PcChests, PcAmmo, PcRead) = (0, 0, false);
            changed = true;
        }
        _pcWasLive = matchInProgress;
        if (matchInProgress && Active && InLobby != true && Allowed(ModesSetting, MatchMode) && !PcRead)
        {
            PcRead = true;
            changed = true;
        }
        while (Events.TryDequeue(out var e))
        {
            if (!matchInProgress) continue;
            if (IsChest(e.Kind)) PcChests++;
            else if (e.Kind == "ammo") PcAmmo++;
            else continue;
            PcRead = true;
            changed = true;
        }
        return changed;
    }

    public static string KindName(string kind) => kind switch
    {
        "chest" => L.T("coffre", "chest", "cofre"),
        "rare-chest" => L.T("coffre rare", "rare chest", "cofre raro"),
        "ammo" => L.T("munitions", "ammo", "munición"),
        "elimination" => L.T("élimination", "elimination", "eliminación"),
        "other" => L.T("autre", "other", "otro"),
        _ => "?",
    };

    public static bool IsChest(string kind) => kind is "chest" or "rare-chest";

    public static string JournalPath(DateTime day) => Path.Combine(_journalFolder, $"{day:yyyy-MM-dd}.txt");

    private static void Journal(string line, DateTime? at = null)
    {
        try
        {
            Directory.CreateDirectory(_journalFolder);
            var when = at ?? DateTime.Now;
            File.AppendAllText(JournalPath(when.Date), (line.StartsWith("──") ? $"{when:HH:mm:ss}  {line}" : line) + Environment.NewLine);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
}
