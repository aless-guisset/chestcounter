using System.Text.Json.Nodes;
using ChestCounter;
using Xunit;

public class ImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Import_CopiesAndLowersAnalyzerVersion()
    {
        var from = Path.Combine(_root, "ChestCounter");
        var to = Path.Combine(_root, "ChestCounterPlus");
        Directory.CreateDirectory(Path.Combine(from, "ps5"));
        Directory.CreateDirectory(Path.Combine(from, "webview"));
        File.WriteAllText(Path.Combine(from, "config.json"), "{\"Language\":\"fr\"}");
        var data = "{\"AnalyzerVersion\":16,\"Matches\":[{\"SessionId\":\"a\",\"AnalyzerVersion\":16,\"ChestsOpened\":5},{\"SessionId\":\"b\",\"AnalyzerVersion\":12,\"ChestsOpened\":3}]}";
        File.WriteAllText(Path.Combine(from, "data.json"), data);
        File.WriteAllText(Path.Combine(from, "ps5", "auth.json"), "{}");
        File.WriteAllText(Path.Combine(from, "webview", "x"), "x");

        Assert.True(Store.ImportFromLedN(from, to));

        Assert.True(File.Exists(Path.Combine(to, "config.json")));
        Assert.True(File.Exists(Path.Combine(to, "ps5", "auth.json")));
        Assert.False(Directory.Exists(Path.Combine(to, "webview")));
        var copied = JsonNode.Parse(File.ReadAllText(Path.Combine(to, "data.json")))!;
        Assert.Equal(0, (int)copied["AnalyzerVersion"]!);
        Assert.Equal(0, (int)copied["Matches"]![0]!["AnalyzerVersion"]!);
        Assert.Equal(12, (int)copied["Matches"]![1]!["AnalyzerVersion"]!);
        Assert.Equal(5, (int)copied["Matches"]![0]!["ChestsOpened"]!);
        Assert.Equal(data, File.ReadAllText(Path.Combine(from, "data.json")));

        File.WriteAllText(Path.Combine(to, "config.json"), "{\"Language\":\"en\"}");
        Assert.False(Store.ImportFromLedN(from, to));
        Assert.Equal("{\"Language\":\"en\"}", File.ReadAllText(Path.Combine(to, "config.json")));
    }

    [Fact]
    public void Import_NothingToImport()
    {
        Assert.False(Store.ImportFromLedN(Path.Combine(_root, "absent"), Path.Combine(_root, "dest")));
    }
}
