using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>
/// Le gain d'XP que Fortnite affiche à droite du centre de l'écran : « +900 XP » avec en dessous d'où il vient
/// (« RARE CHEST », « RARE AMMO BOX », « ELIMINATION », « SLURP BARREL »…). Vu en partie via Chiaki le 13/11 :
/// zone ≈ x 60–75 %, y 28–34 % de l'image du jeu. On lit ce texte pour compter coffres et boîtes en direct.
/// </summary>
public static class XpPopup
{
    public sealed record Event(int Xp, string? Label);

    /// <summary>Zone lue, en fraction de l'image du jeu (large autour de la position vue, l'interface peut bouger).</summary>
    public const double ZoneX0 = 0.52, ZoneX1 = 0.90, ZoneY0 = 0.16, ZoneY1 = 0.50;

    private static readonly Regex XpRe = new(@"^\s*\+\s*([\d.,\s]{1,7})\s*XP\b", RegexOptions.IgnoreCase);

    /// <summary>Les « +N XP » de la zone, chacun avec la ligne juste en dessous comme étiquette (si elle est lue).</summary>
    public static List<Event> Parse(IEnumerable<ScreenOcr.Line> lines)
    {
        var all = lines.ToList();
        var events = new List<Event>();
        foreach (var l in all)
        {
            var m = XpRe.Match(l.Text);
            if (!m.Success || !int.TryParse(Regex.Replace(m.Groups[1].Value, @"[^\d]", ""), out var xp) || xp <= 0 || xp > 20000) continue;
            var label = all
                .Where(o => o != l && !XpRe.IsMatch(o.Text) && o.Y > l.Y && o.Y - (l.Y + l.H) < 2.5 * l.H
                    && o.X < l.X + l.W + l.W && o.X + o.W > l.X - l.W)
                .OrderBy(o => o.Y)
                .FirstOrDefault();
            events.Add(new Event(xp, label?.Text.Trim()));
        }
        return events;
    }

    // L'OCR se trompe sur la petite étiquette : « RARE CREST », « AMMO gox »… On accepte les erreurs courantes.
    private static readonly Regex ChestRe = new(@"C\s?[HR]\s?E\s?S|CHES|COFFRE|C0FFRE", RegexOptions.IgnoreCase);
    private static readonly Regex AmmoRe = new(@"AMM[O0]|MUNITION", RegexOptions.IgnoreCase);

    /// <summary>chest, rare-chest, ammo, elimination, other ou unknown (étiquette pas lue).</summary>
    public static string Kind(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "unknown";
        var t = label.ToUpperInvariant();
        if (AmmoRe.IsMatch(t)) return "ammo";
        if (ChestRe.IsMatch(t)) return t.Contains("RARE") ? "rare-chest" : "chest";
        if (t.Contains("ELIMIN")) return "elimination";
        return "other";
    }

    private static readonly string[] LobbyWords = { "PLAY", "SHOP", "LOCKER", "PASSES", "QUESTS", "COMPETE", "CAREER",
        "JOUER", "BOUTIQUE", "CASIER", "QUÊTES", "QUETES", "COMPÉTITION", "CARRIÈRE" };

    /// <summary>Le menu du haut du lobby (au moins 3 de ses mots) : on n'est pas en partie.</summary>
    public static bool IsLobby(IEnumerable<ScreenOcr.Line> lines)
    {
        var words = lines.SelectMany(l => l.Text.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
        return LobbyWords.Count(words.Contains) >= 3;
    }

    /// <summary>Le haut de l'image du jeu (menu du lobby), sans agrandir.</summary>
    public static ScreenOcr.Frame Top(ScreenOcr.Frame f)
    {
        var (gx, gy, gw, gh) = GameArea(f);
        if (gw < f.Width / 3 || gh < f.Height / 3) (gx, gy, gw, gh) = (0, 0, f.Width, f.Height);
        var h = Math.Max(2, Math.Min(gh / 8, f.Height - gy));
        var o = new byte[gw * h * 4];
        for (var y = 0; y < h; y++) Buffer.BlockCopy(f.Bgra, ((gy + y) * f.Width + gx) * 4, o, y * gw * 4, gw * 4);
        return new ScreenOcr.Frame(o, gw, h);
    }

    /// <summary>Étiquette seule (sans « +N XP » lu) qui dit quand même le type : coffre ou munitions.</summary>
    public static bool IsUsefulLabel(string text) => Kind(text) is "chest" or "rare-chest" or "ammo";

    /// <summary>
    /// Un gain reste affiché ~2 s et se lit sur plusieurs images, parfois en morceaux (le « +XP » sur une, l'étiquette
    /// sur l'autre). Tout ce qui se ressemble à moins de 4 s fait un seul gain, rendu par Flush quand il a disparu.
    /// </summary>
    public sealed class Tracker
    {
        private sealed class Pending
        {
            public int Xp;
            public string? Label;
            public DateTime First, Last;
        }

        private readonly List<Pending> _open = new();
        private static readonly TimeSpan Same = TimeSpan.FromSeconds(4), Gone = TimeSpan.FromSeconds(1.5);

        public void Add(Event e, DateTime at)
        {
            var kind = Kind(e.Label);
            var match = _open.FirstOrDefault(p => at - p.Last < Same && at - p.First < Same && (
                (e.Xp > 0 && p.Xp == e.Xp) ||
                (e.Xp == 0 && (Kind(p.Label) == kind || p.Label == null || Kind(p.Label) is "unknown" or "other")) ||
                (e.Xp > 0 && p.Xp == 0 && (kind is "unknown" or "other" || kind == Kind(p.Label)))));
            if (match == null)
            {
                _open.Add(new Pending { Xp = e.Xp, Label = e.Label, First = at, Last = at });
                return;
            }
            match.Last = at;
            if (match.Xp == 0) match.Xp = e.Xp;
            if (Better(e.Label, match.Label)) match.Label = e.Label;
        }

        /// <summary>Le type connu passe avant « autre », qui passe avant rien.</summary>
        private static bool Better(string? candidate, string? current)
        {
            static int Rank(string? l) => Kind(l) switch { "unknown" => 0, "other" => 1, _ => 2 };
            return Rank(candidate) > Rank(current);
        }

        /// <summary>Les gains qui ne sont plus affichés : (XP, étiquette, heure d'apparition).</summary>
        public List<(int Xp, string? Label, DateTime At)> Flush(DateTime now, bool all = false)
        {
            var done = _open.Where(p => all || now - p.Last > Gone).ToList();
            _open.RemoveAll(done.Contains);
            return done.Select(p => (p.Xp, p.Label, p.First)).ToList();
        }
    }

    /// <summary>L'image du jeu dans la fenêtre, sans les bandes noires (Chiaki, OBS…) : (x, y, largeur, hauteur).</summary>
    public static (int X, int Y, int W, int H) GameArea(ScreenOcr.Frame f)
    {
        // Une ligne « allumée » = au moins 30 % de pixels pas noirs : la barre de titre (gris foncé, un peu de texte) et
        // les bandes noires ne comptent pas.
        bool RowLit(int y)
        {
            int n = 0, lit = 0;
            for (var x = 0; x < f.Width; x += 4, n++) if (Lit(f, x, y)) lit++;
            return lit * 10 >= n * 3;
        }
        bool ColLit(int x, int y0, int y1)
        {
            int n = 0, lit = 0;
            for (var y = y0; y < y1; y += 4, n++) if (Lit(f, x, y)) lit++;
            return lit * 10 >= n * 3;
        }
        int top = 0, bottom = f.Height - 1;
        while (top < bottom && !RowLit(top)) top++;
        while (bottom > top && !RowLit(bottom)) bottom--;
        int left = 0, right = f.Width - 1;
        while (left < right && !ColLit(left, top, bottom)) left++;
        while (right > left && !ColLit(right, top, bottom)) right--;
        return (left, top, right - left + 1, bottom - top + 1);
    }

    private static bool Lit(ScreenOcr.Frame f, int x, int y)
    {
        var i = (y * f.Width + x) * 4;
        return f.Bgra[i] > 48 || f.Bgra[i + 1] > 48 || f.Bgra[i + 2] > 48;
    }

    /// <summary>
    /// La zone du gain d'XP, agrandie (×scale, lissée) et passée en noir sur blanc : le texte du gain est blanc ou rose
    /// très clair avec un contour sombre, donc « clair » = texte. L'OCR lit bien mieux la petite étiquette ainsi.
    /// </summary>
    public static ScreenOcr.Frame Zone(ScreenOcr.Frame f, int scale = 4, int? threshold = 160)
    {
        var (gx, gy, gw, gh) = GameArea(f);
        if (gw < f.Width / 3 || gh < f.Height / 3) (gx, gy, gw, gh) = (0, 0, f.Width, f.Height); // écran noir : toute la fenêtre
        int x0 = gx + (int)(gw * ZoneX0), x1 = gx + (int)(gw * ZoneX1), y0 = gy + (int)(gh * ZoneY0), y1 = gy + (int)(gh * ZoneY1);
        int w = Math.Max(2, Math.Min(x1, f.Width) - x0), h = Math.Max(2, Math.Min(y1, f.Height) - y0);
        if (x0 + w > f.Width || y0 + h > f.Height) return new ScreenOcr.Frame(new byte[2 * 2 * 4], 2, 2);
        var lum = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = ((y0 + y) * f.Width + x0 + x) * 4;
                // min des canaux : un rose très clair (245,209,255) reste clair, un violet saturé (120,60,185) non.
                lum[y * w + x] = Math.Min(f.Bgra[i], Math.Min(f.Bgra[i + 1], f.Bgra[i + 2]));
            }
        int ow = w * scale, oh = h * scale;
        var o = new byte[ow * oh * 4];
        for (var y = 0; y < oh; y++)
        {
            var fy = Math.Min(h - 1.001f, Math.Max(0, (y + 0.5f) / scale - 0.5f));
            var iy = (int)fy;
            var ty = fy - iy;
            for (var x = 0; x < ow; x++)
            {
                var fx = Math.Min(w - 1.001f, Math.Max(0, (x + 0.5f) / scale - 0.5f));
                var ix = (int)fx;
                var tx = fx - ix;
                var v = lum[iy * w + ix] * (1 - tx) * (1 - ty) + lum[iy * w + ix + 1] * tx * (1 - ty)
                    + lum[(iy + 1) * w + ix] * (1 - tx) * ty + lum[(iy + 1) * w + ix + 1] * tx * ty;
                // Sans seuil : niveaux de gris inversés (texte clair → foncé), lissés.
                var c = threshold is int t ? (v >= t ? (byte)0 : (byte)255) : (byte)(255 - Math.Clamp((int)v, 0, 255));
                var di = (y * ow + x) * 4;
                o[di] = o[di + 1] = o[di + 2] = c;
                o[di + 3] = 255;
            }
        }
        return new ScreenOcr.Frame(o, ow, oh);
    }
}
