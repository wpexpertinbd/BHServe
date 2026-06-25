namespace BHServe.Core;

/// <summary>A managed service definition (Windows registry analog of the bash <c>services()</c> table).</summary>
public sealed record ServiceDef(string Key, ServiceRole Role);

/// <summary>
/// The static catalog of services BHServe can manage on Windows, plus the
/// "enabled" (auto-start) set persisted at <c>config\enabled</c>.
/// </summary>
public static class Services
{
    /// <summary>PHP minor versions we support as managed php-cgi pools.</summary>
    public static readonly string[] PhpVersions = { "8.5", "8.4", "8.3", "8.2", "8.1", "7.4" };

    public static IReadOnlyList<ServiceDef> All { get; } = Build();

    private static List<ServiceDef> Build()
    {
        var list = new List<ServiceDef> { new("php", ServiceRole.Php) };
        foreach (var v in PhpVersions) list.Add(new($"php@{v}", ServiceRole.Php));
        list.Add(new("nginx",     ServiceRole.Web));
        list.Add(new("apache",    ServiceRole.Web));
        list.Add(new("mysql",     ServiceRole.Db));
        list.Add(new("mariadb",   ServiceRole.Db));
        list.Add(new("postgresql",ServiceRole.Db));
        list.Add(new("redis",     ServiceRole.Cache));
        list.Add(new("memcached", ServiceRole.Cache));
        list.Add(new("mkcert",    ServiceRole.Tool));
        list.Add(new("mailpit",   ServiceRole.Mail));
        list.Add(new("fnm",       ServiceRole.Node));
        return list;
    }

    public static bool Exists(string key) => All.Any(s => s.Key == key);
    public static ServiceRole RoleOf(string key) => All.FirstOrDefault(s => s.Key == key)?.Role ?? ServiceRole.Other;

    /// <summary>A short version/label for a service row (best-effort, no process spawn).</summary>
    public static string ShortVersion(string key, Config cfg) => key switch
    {
        "nginx"     => "nginx 1.27",
        "apache"    => "httpd 2.4",
        "mysql"     => "MySQL 9.7",
        "mariadb"   => "MariaDB 12.3",
        "postgresql"=> "PostgreSQL 16",
        "redis"     => "Redis",
        "memcached" => "Memcached",
        "mailpit"   => "Mailpit",
        "mkcert"    => "mkcert",
        "fnm"       => "fnm",
        _ when RoleOf(key) == ServiceRole.Php => "PHP " + PhpVersion(key, cfg),
        _ => "",
    };

    /// <summary>Normalize a --php value ("8.4" | "php@8.4" | "default" | "") to a registry key (mirrors bash php_key).</summary>
    public static string PhpKey(string? v, Config cfg)
    {
        v = (v ?? "").Trim();
        if (v is "" or "default") v = cfg.DefaultPhp;
        if (v == "php") return "php";
        return v.StartsWith("php@") ? v : $"php@{v}";
    }

    /// <summary>Socket/pool label for a php key: php@8.4 → 8.4, php → default (mirrors bash php_label).</summary>
    public static string PhpLabel(string key) => key == "php" ? "default" : key["php@".Length..];

    /// <summary>The concrete php version string (e.g. "8.4") a key resolves to.</summary>
    public static string PhpVersion(string key, Config cfg) =>
        key == "php" ? cfg.DefaultPhp : key["php@".Length..];

    public static bool Installed(string key, Config cfg) => key switch
    {
        "nginx"     => Tools.NginxExe() is not null,
        "apache"    => Tools.HttpdExe() is not null,
        "mysql"     => Tools.MysqlInstalled,
        "mariadb"   => Tools.MariadbInstalled,
        "postgresql"=> Tools.PostgresExe() is not null,
        "redis"     => Tools.RedisServerExe() is not null,
        "memcached" => Tools.MemcachedExe() is not null,
        "mkcert"    => Tools.MkcertExe() is not null,
        "mailpit"   => Tools.MailpitExe() is not null,
        "fnm"       => Tools.FnmExe() is not null,
        _ when RoleOf(key) == ServiceRole.Php => Tools.PhpCgiExe(PhpVersion(key, cfg)) is not null,
        _ => false,
    };

    // ── enabled (auto-start) set ────────────────────────────────────────────────
    private static string EnabledFile => Path.Combine(Paths.Config, "enabled");

    private static bool DefaultEnabled(string key, Config cfg) => key switch
    {
        "nginx" or "mysql" or "mariadb" => true,
        _ => key == PhpKey("default", cfg),
    };

    public static bool Enabled(string key, Config cfg)
    {
        if (File.Exists(EnabledFile))
            return File.ReadAllLines(EnabledFile).Any(l => l.Trim() == key);
        return DefaultEnabled(key, cfg);
    }

    private static void Materialize(Config cfg)
    {
        if (File.Exists(EnabledFile)) return;
        Directory.CreateDirectory(Paths.Config);
        File.WriteAllLines(EnabledFile, All.Select(s => s.Key).Where(k => DefaultEnabled(k, cfg)));
    }

    public static void Enable(string key, Config cfg)
    {
        Materialize(cfg);
        var lines = File.ReadAllLines(EnabledFile).ToList();
        if (!lines.Contains(key)) { lines.Add(key); File.WriteAllLines(EnabledFile, lines); }
    }

    public static void Disable(string key, Config cfg)
    {
        Materialize(cfg);
        var lines = File.ReadAllLines(EnabledFile).Where(l => l.Trim() != key).ToList();
        File.WriteAllLines(EnabledFile, lines);
    }
}
