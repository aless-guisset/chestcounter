namespace ChestCounter;

/// <summary>Version, GitHub repository and names shared by the tool's processes.</summary>
public static class AppInfo
{
    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";

    public const string Repo = "LED-N/Fortnite-ChestCounter";

    /// <summary>Notre version (PC + console), installable à côté de celle de LED-N : autre nom, autre dossier, autres verrous.</summary>
    public const string DisplayName = "ChestCounter+";
    public const string DataFolderName = "ChestCounterPlus";
    public const string ExeName = "ChestCounterPlus.exe";

    /// <summary>
    /// Copie modifiée (PC + console) : les mises à jour de LED-N remplaceraient l'exe et effaceraient le mode console,
    /// donc elles sont coupées tant qu'on n'a pas notre propre dépôt de versions.
    /// </summary>
    public const bool UpdatesEnabled = false;
    public static string RepoUrl => "https://github.com/" + Repo;
    public static string IssuesUrl => RepoUrl + "/issues";

    /// <summary>Path of ChestCounter.exe when running as the published exe (null under "dotnet ChestCounter.dll").</summary>
    public static string? ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            return p != null && Path.GetFileName(p).Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    // Names of the per-user kernel objects that let the processes find each other.
    /// <summary>One instance per data folder (the normal folder, or a test folder given with --data).</summary>
    public static string InstanceMutexFor(string dataDir)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant()));
        return @"Local\ChestCounterPlus.Instance." + Convert.ToHexString(hash, 0, 8);
    }
    public const string StandbyMutex = @"Local\ChestCounterPlus.Standby";
    public const string StandbyStopEvent = @"Local\ChestCounterPlus.StandbyStop";
}
