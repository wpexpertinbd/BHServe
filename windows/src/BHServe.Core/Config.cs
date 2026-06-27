using System.Text.Json;
using System.Text.Json.Serialization;

namespace BHServe.Core;

/// <summary>
/// Typed view of <c>config\bhserve.json</c> — the Windows analog of the mac
/// engine's <c>jget</c> reads. Load is fail-soft (missing/garbage → defaults) so
/// the CLI never dies on a config problem.
/// </summary>
public sealed class Config
{
    [JsonPropertyName("tld")]          public string Tld { get; set; } = "test";
    [JsonPropertyName("http_port")]    public int HttpPort { get; set; } = 80;
    [JsonPropertyName("https_port")]   public int HttpsPort { get; set; } = 443;
    [JsonPropertyName("default_php")]  public string DefaultPhp { get; set; } = "8.4";
    [JsonPropertyName("default_web")]  public string DefaultWeb { get; set; } = "nginx";
    [JsonPropertyName("sites_root")]   public string SitesRoot { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BHServe", "www");
    [JsonPropertyName("autostart")]    public bool Autostart { get; set; } = false;
    [JsonPropertyName("minimize_to_tray")] public bool MinimizeToTray { get; set; } = true;
    [JsonPropertyName("dashboard_page_size")] public int DashboardPageSize { get; set; } = 10;
    [JsonPropertyName("sites_page_size")]     public int SitesPageSize { get; set; } = 15;
    [JsonPropertyName("databases_page_size")] public int DatabasesPageSize { get; set; } = 15;
    [JsonPropertyName("apps_page_size")]      public int AppsPageSize { get; set; } = 15;
    [JsonPropertyName("auto_update")]              public bool AutoUpdate { get; set; } = true;
    [JsonPropertyName("start_services_on_launch")] public bool StartServicesOnLaunch { get; set; } = false;
    [JsonPropertyName("root_password")]            public string RootPassword { get; set; } = "";   // "" = passwordless root

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Read config (fail-soft → defaults if absent/invalid). Expands %ENV% in sites_root.</summary>
    public static Config Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigJson))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.ConfigJson), Opts);
                if (c is not null)
                {
                    c.SitesRoot = Environment.ExpandEnvironmentVariables(c.SitesRoot);
                    return c;
                }
            }
        }
        catch { /* fall through to defaults */ }
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Config);
        File.WriteAllText(Paths.ConfigJson, JsonSerializer.Serialize(this, Opts));
    }
}
