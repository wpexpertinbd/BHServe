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
