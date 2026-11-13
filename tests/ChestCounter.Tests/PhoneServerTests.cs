using System.Text;
using ChestCounter;
using Xunit;

public class PhoneServerTests
{
    private const string Token = "abc123";

    private sealed class Spy : IPhoneActions
    {
        public List<string> Calls { get; } = new();
        public void Tap(int delta) => Calls.Add($"tap {delta}");
        public void Answer(string id, int? chests) => Calls.Add($"answer {id} {chests?.ToString() ?? "null"}");
        public void Forget(string id) => Calls.Add($"forget {id}");
    }

    private static (PhoneServer.Response R, Spy S) Run(string method, string target, string state = "{\"total\":\"5\"}")
    {
        var spy = new Spy();
        var r = PhoneServer.Handle(method, target, Token, () => state, spy);
        return (r, spy);
    }

    [Fact]
    public void WrongOrMissingToken_Forbidden()
    {
        Assert.Equal(403, Run("GET", "/").R.Status);
        Assert.Equal(403, Run("GET", "/?k=nope").R.Status);
        Assert.Equal(403, Run("GET", "/api/state?k=abc12").R.Status);
        var (r, s) = Run("POST", "/api/tap?k=bad&d=1");
        Assert.Equal(403, r.Status);
        Assert.Empty(s.Calls);
    }

    [Fact]
    public void Page_IsHtmlWithTokenInside()
    {
        var r = Run("GET", "/?k=abc123").R;
        Assert.Equal(200, r.Status);
        Assert.StartsWith("text/html", r.ContentType);
        Assert.Contains("abc123", Encoding.UTF8.GetString(r.Body));
    }

    [Fact]
    public void State_ReturnsPublishedJson()
    {
        var r = Run("GET", "/api/state?k=abc123").R;
        Assert.Equal(200, r.Status);
        Assert.Equal("application/json; charset=utf-8", r.ContentType);
        Assert.Equal("{\"total\":\"5\"}", Encoding.UTF8.GetString(r.Body));
        Assert.Equal("no-store", r.CacheControl);
    }

    [Fact]
    public void Commands_CallActions()
    {
        Assert.Equal(new[] { "tap 1" }, Run("POST", "/api/tap?k=abc123&d=1").S.Calls);
        Assert.Equal(new[] { "tap -1" }, Run("POST", "/api/tap?k=abc123&d=-1").S.Calls);
        Assert.Equal(new[] { "answer x1 7" }, Run("POST", "/api/answer?k=abc123&id=x1&n=7").S.Calls);
        Assert.Equal(new[] { "answer x1 null" }, Run("POST", "/api/answer?k=abc123&id=x1&n=").S.Calls);
        Assert.Equal(new[] { "forget x1" }, Run("POST", "/api/forget?k=abc123&id=x1").S.Calls);
    }

    [Fact]
    public void Commands_RejectBadValues()
    {
        Assert.Equal(400, Run("POST", "/api/tap?k=abc123&d=5").R.Status);
        Assert.Equal(400, Run("POST", "/api/answer?k=abc123&id=x1&n=abc").R.Status);
        Assert.Equal(400, Run("POST", "/api/answer?k=abc123&n=3").R.Status);
        Assert.Equal(405, Run("GET", "/api/tap?k=abc123&d=1").R.Status); // pas de commande par simple lien
    }

    [Fact]
    public void Unknown_NotFound()
    {
        Assert.Equal(404, Run("GET", "/nimporte?k=abc123").R.Status);
    }

    [Fact]
    public void Manifest_ForHomeScreen()
    {
        var r = Run("GET", "/manifest.json?k=abc123").R;
        Assert.Equal(200, r.Status);
        Assert.Contains("ChestCounter+", Encoding.UTF8.GetString(r.Body));
        Assert.Contains("abc123", Encoding.UTF8.GetString(r.Body)); // start_url garde la clé
    }

    [Fact]
    public async Task RealSocket_ServesStateAndCommands()
    {
        var spy = new Spy();
        var port = 18000 + Random.Shared.Next(1000);
        Assert.True(PhoneServer.Start(port, Token, spy));
        try
        {
            PhoneServer.Publish(new BarView("42", BarState.Estimate, "≈", Array.Empty<BarLine>(), Array.Empty<ChanceLine>(), null));
            using var http = new HttpClient();
            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/api/state?k={Token}");
            Assert.Contains("\"total\":\"42\"", json);
            var r = await http.PostAsync($"http://127.0.0.1:{port}/api/tap?k={Token}&d=1", null);
            Assert.True(r.IsSuccessStatusCode);
            Assert.Equal(new[] { "tap 1" }, spy.Calls);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.GetAsync($"http://127.0.0.1:{port}/")).StatusCode);
        }
        finally { PhoneServer.Stop(); }
    }
}
