using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChestCounter;

/// <summary>Ce que le téléphone peut faire (mode console) : appliqué par la boucle principale, pas par le serveur.</summary>
public interface IPhoneActions
{
    void Tap(int delta);
    void Answer(string id, int? chests);
    void Forget(string id);
}

/// <summary>
/// Page pour le téléphone, servie par le PC sur le réseau local (même Wi-Fi) : la barre des coffres, « +1 coffre »
/// pendant la partie et la réponse « combien de coffres ? » en fin de partie. Une clé secrète (dans le lien et le
/// QR code) protège la page : sans elle, 403. Rien ne sort du réseau local ; la connexion Epic n'est jamais servie.
/// </summary>
public static class PhoneServer
{
    public sealed record Response(int Status, string ContentType, byte[] Body, string? CacheControl = null);

    private static TcpListener? _listener;
    private static volatile string _state = "{}";
    private static string _token = "";
    private static IPhoneActions? _actions;

    public static bool IsRunning => _listener != null;
    public static int Port { get; private set; }

    /// <summary>Clé aléatoire pour le lien du téléphone (gardée dans config.json).</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Adresse du PC sur le réseau local (Wi-Fi ou câble avec une passerelle), ou null.</summary>
    public static string? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(p => p.UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString();
        }
        catch { return null; }
    }

    public static string? Url => IsRunning && LanAddress() is { } ip ? $"http://{ip}:{Port}/?k={_token}" : null;

    /// <summary>Démarre le serveur (Windows demande la première fois d'autoriser le réseau privé). Faux si le port est pris.</summary>
    public static bool Start(int port, string token, IPhoneActions? actions)
    {
        if (IsRunning) return true;
        _token = token;
        _actions = actions;
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            _listener = l;
            Port = port;
        }
        catch (Exception e)
        {
            ErrorLog.Write("serveur téléphone", e);
            return false;
        }
        new Thread(AcceptLoop) { IsBackground = true, Name = "PhoneServer" }.Start();
        return true;
    }

    public static void Stop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    /// <summary>Ce que la page affiche (appelé par la boucle principale à chaque mise à jour de la barre).</summary>
    public static void Publish(BarView view, object? console = null)
    {
        if (!IsRunning) return;
        _state = JsonSerializer.Serialize(new
        {
            total = view.Total,
            state = view.State.ToString(),
            badge = view.Badge,
            summary = view.Summary.Select(l => new { label = l.Label, value = l.Value, detail = l.Detail }),
            chances = view.Chances.Select(c => new { label = c.Label, percent = c.Percent, fill = c.Fill, odds = c.Odds }),
            chancesEmpty = view.ChancesEmpty,
            console,
            ui = new
            {
                tap = L.T("+1 coffre", "+1 chest", "+1 cofre"),
                undo = L.T("−1", "−1", "−1"),
                current = L.T("Partie en cours", "Current match", "Partida actual"),
                tapHelp = L.T("Appuie à chaque coffre ouvert : le chiffre est gardé à la fin de la partie.", "Tap for every chest you open: the count is kept when the match ends.", "Pulsa en cada cofre abierto: la cifra se guarda al final de la partida."),
                ask = L.T("Combien de coffres ?", "How many chests?", "¿Cuántos cofres?"),
                ok = L.T("OK", "OK", "OK"),
                matches = L.T("Parties", "Matches", "Partidas"),
                endedAt = L.T("finie à", "ended at", "terminó a las"),
                notMatch = L.T("pas une partie", "not a match", "no es partida"),
                chancesTitle = L.T("Coffres · chances", "Chests · chances", "Cofres · probabilidades"),
                offline = L.T("PC injoignable (même Wi-Fi ? compteur ouvert ?)", "PC unreachable (same Wi-Fi? counter open?)", "PC inaccesible (¿mismo Wi-Fi? ¿contador abierto?)"),
            },
        });
    }

    private static void AcceptLoop()
    {
        while (_listener is { } l)
        {
            TcpClient client;
            try { client = l.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private static void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var request = reader.ReadLine();
                if (string.IsNullOrEmpty(request)) return;
                while (!string.IsNullOrEmpty(reader.ReadLine())) { } // en-têtes ignorés (pas de corps utile : tout est dans l'URL)
                var parts = request.Split(' ');
                if (parts.Length < 2) return;
                var r = Handle(parts[0], parts[1], _token, () => _state, _actions);
                var head = $"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\nContent-Type: {r.ContentType}\r\nContent-Length: {r.Body.Length}\r\n"
                    + (r.CacheControl != null ? $"Cache-Control: {r.CacheControl}\r\n" : "") + "Connection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(head));
                stream.Write(r.Body);
            }
            catch { }
        }
    }

    private static string Reason(int status) => status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 405 => "Method Not Allowed", _ => "Not Found" };

    /// <summary>Une requête → une réponse (sans réseau : testable).</summary>
    public static Response Handle(string method, string target, string token, Func<string> state, IPhoneActions? actions)
    {
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = ParseQuery(q < 0 ? "" : target[(q + 1)..]);
        var key = query.GetValueOrDefault("k") ?? "";
        if (token.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(token)))
            return Text(403, "Lien incomplet : rescanne le QR code affiché par ChestCounter+.");

        switch (path)
        {
            case "/":
                return new Response(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page().Replace("__KEY__", token)), "no-store");
            case "/manifest.json":
                var manifest = JsonSerializer.Serialize(new
                {
                    name = "ChestCounter+",
                    short_name = "Coffres",
                    start_url = "/?k=" + token,
                    display = "standalone",
                    background_color = "#14110d",
                    theme_color = "#14110d",
                }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                return new Response(200, "application/manifest+json", Encoding.UTF8.GetBytes(manifest));
            case "/api/state":
                return new Response(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(state()), "no-store");
            case "/api/tap" or "/api/answer" or "/api/forget":
                if (method != "POST") return Text(405, "POST");
                if (actions == null) return Text(400, "mode PC");
                return Command(path, query, actions);
        }
        return Text(404, "?");
    }

    private static Response Command(string path, Dictionary<string, string> q, IPhoneActions a)
    {
        var id = q.GetValueOrDefault("id");
        switch (path)
        {
            case "/api/tap":
                if (q.GetValueOrDefault("d") is not ("1" or "-1")) return Text(400, "d");
                a.Tap(int.Parse(q["d"]));
                break;
            case "/api/answer":
                if (string.IsNullOrEmpty(id)) return Text(400, "id");
                var n = q.GetValueOrDefault("n") ?? "";
                if (n.Length == 0) a.Answer(id, null);
                else if (int.TryParse(n, out var chests) && chests is >= 0 and <= 200) a.Answer(id, chests);
                else return Text(400, "n");
                break;
            default:
                if (string.IsNullOrEmpty(id)) return Text(400, "id");
                a.Forget(id);
                break;
        }
        return Text(200, "ok");
    }

    private static Response Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), "no-store");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = Uri.UnescapeDataString(i < 0 ? pair : pair[..i]);
            d[k] = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
        }
        return d;
    }

    /// <summary>Page à ouvrir sur le PC : le QR code du lien (à scanner avec l'appareil photo du téléphone) et le lien en clair.</summary>
    public static void WriteQrPage(string path, string url)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        var svg = new QRCoder.SvgQRCode(data).GetGraphic(8);
        var h = System.Net.WebUtility.HtmlEncode(url);
        var html = $$"""
            <!doctype html><html lang="fr"><head><meta charset="utf-8"><title>ChestCounter+ · téléphone</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>body{margin:0;background:#14110d;color:#f3ead9;font:16px/1.5 system-ui,"Segoe UI",sans-serif;display:grid;place-items:center;min-height:100vh}
            main{max-width:560px;padding:24px;text-align:center}.qr{background:#fff;padding:16px;border-radius:16px;display:inline-block}.qr svg{width:300px;height:300px;display:block}
            h1{color:#e9b44c;font-size:24px}ol{text-align:left;color:#d8cbb6}code{display:block;margin-top:16px;color:#a8998a;word-break:break-all;font-size:13px}</style></head>
            <body><main><h1>{{L.T("Ouvre le compteur sur ton téléphone", "Open the counter on your phone", "Abre el contador en tu teléfono")}}</h1>
            <div class="qr">{{svg}}</div>
            <ol><li>{{L.T("Téléphone sur le même Wi-Fi que ce PC.", "Phone on the same Wi-Fi as this PC.", "Teléfono en la misma Wi-Fi que este PC.")}}</li>
            <li>{{L.T("Scanne le QR code avec l'appareil photo.", "Scan the QR code with the camera.", "Escanea el código QR con la cámara.")}}</li>
            <li>{{L.T("Pour l'avoir comme une appli : menu du navigateur → « Ajouter à l'écran d'accueil ».", "To keep it like an app: browser menu → \"Add to Home screen\".", "Para tenerlo como app: menú del navegador → «Añadir a pantalla de inicio».")}}</li>
            <li>{{L.T("La page marche tant que ChestCounter+ est ouvert sur le PC.", "The page works while ChestCounter+ is open on the PC.", "La página funciona mientras ChestCounter+ esté abierto en el PC.")}}</li></ol>
            <code>{{h}}</code></main></body></html>
            """;
        File.WriteAllText(path, html);
    }

    private static string? _page;

    private static string Page()
    {
        if (_page != null) return _page;
        using var s = typeof(PhoneServer).Assembly.GetManifestResourceStream("phone.html")
            ?? throw new InvalidOperationException("phone.html manquant dans l'exe");
        using var r = new StreamReader(s);
        return _page = r.ReadToEnd();
    }
}
