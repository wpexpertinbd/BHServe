using System.Text.RegularExpressions;

namespace BHServe.Core;

/// <summary>
/// The brains — the C# analog of the mac <c>engine/bhserve</c> script. The WinUI
/// app calls these methods in-process; <c>bhserve.exe</c> exposes the same surface
/// on the command line.
/// </summary>
public sealed class Engine
{
    /// <summary>Stdout sink (the CLI sets this to Console.WriteLine; the GUI can capture it).</summary>
    public Action<string> Out { get; init; } = Console.WriteLine;
    public Action<string> Err { get; init; } = Console.Error.WriteLine;

    private void Ok(string m)   => Out($"  ✓ {m}");
    private void No(string m)   => Err($"  ✗ {m}");
    private void Warn(string m) => Out($"  ! {m}");
    private void Info(string m) => Out($"    {m}");
    private void Hdr(string m)  => Out($"\n{m}");

    private static void NeedInit()
    {
        if (!Directory.Exists(Paths.Config))
            throw new BhException("not initialized — run: bhserve init");
    }

    // ── init ────────────────────────────────────────────────────────────────────
    public void Init()
    {
        Hdr($"Initializing BHServe at {Paths.Home}");
        Paths.EnsureSkeleton();
        foreach (var d in new[] { "client_body", "proxy", "fastcgi", "uwsgi", "scgi" })
            Directory.CreateDirectory(Path.Combine(Paths.Tmp, d));
        Directory.CreateDirectory(Path.Combine(Paths.Home, "nginx", "sites"));

        var cfg = Config.Load();
        if (!File.Exists(Paths.ConfigJson)) { cfg.Save(); Ok($"wrote default config: {Paths.ConfigJson}"); }
        else Ok($"config already exists: {Paths.ConfigJson}");

        NginxConfig.RenderMain(cfg);
        Ok($"directories ready under {Paths.Home}");
    }

    // ── install / lifecycle ─────────────────────────────────────────────────────
    public void Install(string tool)
    {
        NeedInit();
        if (string.IsNullOrEmpty(tool))
            throw new BhException("usage: bhserve install <all|nginx|apache|php@8.4|mariadb|redis|memcached|mkcert|mailpit|fnm|cloudflared>");
        var cfg = Config.Load();

        if (tool == "all")
        {
            // The core stack — everything BHServe needs to serve a PHP site, self-contained.
            foreach (var t in new[] { "nginx", $"php@{cfg.DefaultPhp}", "mariadb", "mkcert" }) Install(t);
            return;
        }

        try
        {
            string? exe = tool switch
            {
                "nginx"      => Get("nginx",   cfg, () => Downloader.InstallNginx()),
                "apache" or "httpd" => GetApache(cfg),
                "mysql"      => Get("mysql",   cfg, () => Downloader.InstallDb()),
                "mariadb"    => Get("mariadb", cfg, () => Downloader.InstallMariadb()),
                "postgresql" or "postgres" => Get("postgresql", cfg, () => Downloader.InstallPostgres()),
                "redis"      => Get("redis",     cfg, () => Downloader.InstallRedis()),
                "memcached"  => Get("memcached", cfg, () => Downloader.InstallMemcached()),
                "mkcert"     => Get("mkcert",    cfg, () => Downloader.InstallMkcert()),
                "mailpit"    => Get("mailpit",   cfg, () => Downloader.InstallMailpit()),
                "fnm" or "node" => Get("fnm",    cfg, () => Downloader.InstallFnm()),
                "python" or "python3" => Get("python", cfg, () => Downloader.InstallPython()),
                "cloudflared" => !_force && Tools.CloudflaredExe() is not null ? Done("cloudflared") : Run("cloudflared", () => Downloader.InstallCloudflared()),
                _ when tool.StartsWith("php") => InstallPhp(tool, cfg),
                _ => throw new BhException($"unknown tool: {tool}"),
            };
            if (exe is not null) Ok($"{tool} installed: {exe}");
        }
        catch (BhException) { throw; }
        catch (Exception ex) { No($"install {tool} failed: {ex.Message}"); }
    }

    // ── site requirements ────────────────────────────────────────────────────────
    // Most users skip the readme and try to add a site with NOTHING installed (no nginx/PHP/DB),
    // which produces a dead site. The GUI uses these to install + start the core stack first.

    /// <summary>The service keys a site of this type needs to actually serve.</summary>
    private List<string> RequiredServices(string type, string php, string server)
    {
        var cfg = Config.Load();
        if (type == "node") return new List<string> { "nginx", "fnm" };
        if (type == "python") return new List<string> { "nginx", "python" };
        // An Apache site is a BACKEND behind nginx: Apache listens only on 127.0.0.1:8080, while nginx
        // owns :80/:443 (+ TLS + *.test) and proxies to it. So "Apache" needs BOTH nginx AND apache —
        // otherwise an Apache-only install has nothing serving :80 and the site is dead.
        var req = server == "apache" ? new List<string> { "nginx", "apache" } : new List<string> { "nginx" };
        if (type is "php" or "wordpress") req.Add(Services.PhpKey(php, cfg));
        if (type == "wordpress")
            req.Add(Services.Installed("mysql", cfg) && !Services.Installed("mariadb", cfg) ? "mysql" : "mariadb");
        return req;
    }

    private static string ServiceLabel(string key, Config cfg) => key switch
    {
        "nginx"   => "nginx (web server)",
        "apache"  => "Apache (web server)",
        "mariadb" => "MariaDB (database)",
        "mysql"   => "MySQL (database)",
        "fnm"     => "Node.js (fnm)",
        "python"  => "Python (interpreter)",
        "mkcert"  => "mkcert (HTTPS certificates)",
        _ when key.StartsWith("php") => $"PHP {Services.PhpVersion(key, cfg)}",
        _ => key,
    };

    /// <summary>The core stack BHServe needs before any site works (used by the first-run setup prompt):
    /// web server, the default PHP, a database, and mkcert for HTTPS. Returns the ones not installed.</summary>
    public List<(string key, string label)> MissingCore()
    {
        var cfg = Config.Load();
        var core = new[] { "nginx", Services.PhpKey("default", cfg), "mariadb", "mkcert" };
        return core.Where(k => !Services.Installed(k, cfg))
                   .Select(k => (k, ServiceLabel(k, cfg)))
                   .ToList();
    }

    /// <summary>Required services for this site type that aren't installed yet (key + friendly label).
    /// The GUI shows these and blocks "Add site" until they're installed.</summary>
    public List<(string key, string label)> MissingForSite(string type, string php, string server)
    {
        var cfg = Config.Load();
        return RequiredServices(type, php, server)
            .Where(k => !Services.Installed(k, cfg))
            .Select(k => (k, ServiceLabel(k, cfg)))
            .ToList();
    }

    /// <summary>Install any missing required services for this site and start the ones it needs, so a
    /// freshly-added site actually serves. Best-effort + idempotent; writes progress via Out.</summary>
    public void EnsureSiteServices(string type, string php, string server)
    {
        var cfg = Config.Load();
        var required = RequiredServices(type, php, server);
        foreach (var key in required)
            if (!Services.Installed(key, cfg)) Install(key);
        foreach (var key in required)
        {
            try { if (Services.Installed(key, cfg)) Start(key); } catch { /* fnm isn't startable; start failures surface elsewhere */ }
        }
    }

    // Set during Update so the "already installed" guards are bypassed and the latest is re-fetched.
    private bool _force;

    private string? Get(string key, Config cfg, Func<Task<string>> install)
    {
        if (!_force && Services.Installed(key, cfg)) { Ok($"{key} already installed"); return null; }
        Hdr($"{(_force ? "Updating" : "Installing")} {key}");
        return install().GetAwaiter().GetResult();
    }

    private string? Done(string key) { Ok($"{key} already installed"); return null; }
    private string? Run(string key, Func<Task<string>> install) { Hdr($"Installing {key}"); return install().GetAwaiter().GetResult(); }

    private string? GetApache(Config cfg)
    {
        if (!_force && Services.Installed("apache", cfg)) { Ok("apache already installed"); return null; }
        Hdr("Installing Apache (Apache Lounge build)");
        try { return Downloader.InstallApache().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            throw new BhException(
                $"Apache download failed ({ex.Message}). Apache Lounge URLs change per build — " +
                $"grab httpd-2.4.x-win64-VS17.zip from apachelounge.com and extract into {Path.Combine(Paths.Bin, "apache")}.");
        }
    }

    private string? InstallPhp(string tool, Config cfg)
    {
        var verArg = tool == "php" ? "default" : tool[(tool.IndexOf('@') + 1)..];
        var ver = Services.PhpVersion(Services.PhpKey(verArg, cfg), cfg);
        if (!_force && Tools.PhpCgiExe(ver) is not null) { Ok($"php {ver} already installed"); return null; }
        Hdr($"Installing PHP {ver} (NTS x64 from windows.php.net)");
        var exe = Downloader.InstallPhp(ver).GetAwaiter().GetResult();
        // Auto-enable ionCube so encoded apps (WHMCS, etc.) work out of the box on every PHP version.
        // Best-effort: an ionCube loader-download hiccup must never fail the PHP install itself.
        try { Php.Ioncube(ver, Out); }
        catch (Exception ex) { Warn($"ionCube not auto-enabled for php {ver}: {ex.Message} (run later: bhserve php ioncube {ver})"); }
        return exe;
    }

    /// <summary>Re-fetch the LATEST build of a tool, replacing the binaries in place. Stops the
    /// service first (to release file locks) but never touches the data dir or config — so a DB
    /// engine update keeps every existing database. _force makes Install skip its "already
    /// installed" guards and pull the current latest (e.g. MariaDB 12 → 13 when it ships).</summary>
    public void Update(string tool)
    {
        NeedInit();
        if (string.IsNullOrEmpty(tool)) throw new BhException("usage: bhserve update <tool>");
        try { Stop(tool); } catch { }
        System.Threading.Thread.Sleep(500);   // let the process release its file locks
        _force = true;
        try { Install(tool); } finally { _force = false; }

        // DB engines: the regular upgrade flow — stop → swap binaries → restart → run the upgrade
        // command. No dump: the data dir is never touched; mariadb-upgrade just reconciles the
        // system tables (MySQL self-upgrades on start).
        if (tool is "mariadb" or "mysql")
        {
            var (ok, msg) = DbServer.Start(tool);
            if (!ok) { Warn($"new {tool} binary failed to start: {msg}"); return; }
            Ok(msg);
            var up = DbServer.RunUpgrade(tool);
            if (up.Length > 0) Ok(up);
        }
    }

    public void Uninstall(string tool)
    {
        NeedInit();
        if (string.IsNullOrEmpty(tool)) throw new BhException("usage: bhserve uninstall <tool>");
        var cfg = Config.Load();
        try { Stop(tool); } catch { }
        System.Threading.Thread.Sleep(400);   // let the process release file locks

        var dir = tool switch
        {
            "nginx"      => Path.Combine(Paths.Bin, "nginx"),
            "apache" or "httpd"  => Path.Combine(Paths.Bin, "apache"),
            "mysql"      => Path.Combine(Paths.Bin, "mysql"),
            "mariadb"    => Path.Combine(Paths.Bin, "mariadb"),
            "postgresql" or "postgres" => Path.Combine(Paths.Bin, "postgresql"),
            "redis"      => Path.Combine(Paths.Bin, "redis"),
            "memcached"  => Path.Combine(Paths.Bin, "memcached"),
            "mkcert"     => Path.Combine(Paths.Bin, "mkcert"),
            "mailpit"    => Path.Combine(Paths.Bin, "mailpit"),
            "fnm" or "node" => Path.Combine(Paths.Bin, "fnm"),
            "python" or "python3" => Path.Combine(Paths.Bin, "python"),
            "cloudflared"   => Path.Combine(Paths.Bin, "cloudflared"),
            _ when tool.StartsWith("php") =>
                Path.Combine(Paths.Bin, "php", Services.PhpVersion(Services.PhpKey(tool == "php" ? "default" : tool[(tool.IndexOf('@') + 1)..], cfg), cfg)),
            _ => throw new BhException($"unknown tool: {tool}"),
        };
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); Ok($"{tool} uninstalled"); } catch (Exception ex) { No($"uninstall {tool}: {ex.Message}"); } }
        else Warn($"{tool} not installed");
    }

    // ── start/stop ──────────────────────────────────────────────────────────────
    /// <summary>"all" → start nginx + every PHP version an enabled site uses (the 502 fix).</summary>
    public void Start(string svc)
    {
        NeedInit();
        var cfg = Config.Load();
        if (svc is "all" or "")
        {
            // Start only ACTIVE (auto-start ★) + installed services — never an installed-but-inactive one.
            // php-cgi is on-demand for sites, so it always starts for the versions sites use.
            foreach (var v in SitePhpVersions(cfg))
                if (PhpCgi.Start(v)) Ok($"php-cgi {v} on :{PhpCgi.PortFor(v)}");
                else Warn($"php {v} not installed — bhserve install php@{v}");

            if ((Services.Enabled("mariadb", cfg) || Services.Enabled("mysql", cfg)) && Tools.MysqldExe() is not null && !DbServer.Running())
            {
                var engine = Services.Enabled("mysql", cfg) && !Services.Enabled("mariadb", cfg) ? "mysql" : "mariadb";
                var (dok, dmsg) = DbServer.Start(engine); if (dok) Ok(dmsg); else Warn(dmsg);
            }
            if (Services.Enabled("postgresql", cfg) && Tools.PostgresExe() is not null && !PgServer.Running())
            { var (pok, pmsg) = PgServer.Start(); if (pok) Ok(pmsg); else Warn(pmsg); }
            if (Services.Enabled("redis", cfg) && Tools.RedisServerExe() is not null && Redis.Start()) Ok($"redis on :{Redis.Port}");
            if (Services.Enabled("memcached", cfg) && Tools.MemcachedExe() is not null && Memcached.Start()) Ok($"memcached on :{Memcached.Port}");
            if (Services.Enabled("mailpit", cfg) && Tools.MailpitExe() is not null && MailpitServer.Start()) Ok($"mailpit on UI :{MailpitServer.UiPort}");
            if (Services.Enabled("apache", cfg) && Tools.HttpdExe() is not null) { var (aok, amsg) = Apache.Start(); if (aok) Ok(amsg); else Warn(amsg); }
            if (Services.Enabled("nginx", cfg) && Tools.NginxExe() is not null) { var (ok, msg) = Nginx.Start(cfg); if (ok) Ok(msg); else Warn(msg); }
            return;
        }
        // python (and fnm/node) are TOOLS, not daemons — "active once installed", nothing to start.
        if (svc is "python" or "python3" or "fnm" or "node") { Ok($"{svc} ready (a tool — no daemon to start)"); return; }
        // Single-service start: THROW on failure (don't just print) so the GUI/notice bar reflects
        // the real result instead of a false "started", and the CLI exits non-zero.
        if (svc == "nginx") { var (ok, msg) = Nginx.Start(cfg); if (ok) Ok(msg); else throw new BhException(msg); return; }
        if (svc is "mariadb" or "mysql") { var (ok, msg) = DbServer.Start(svc); if (ok) Ok(msg); else throw new BhException(msg); return; }
        if (svc is "postgresql" or "postgres") { var (ok, msg) = PgServer.Start(); if (ok) Ok(msg); else throw new BhException(msg); return; }
        if (svc == "apache")  { var (ok, msg) = Apache.Start(); if (ok) Ok(msg); else throw new BhException(msg); return; }
        if (svc == "redis")     { if (Redis.Start()) Ok($"redis on :{Redis.Port}"); else throw new BhException("redis not installed — bhserve install redis"); return; }
        if (svc == "memcached") { if (Memcached.Start()) Ok($"memcached on :{Memcached.Port}"); else throw new BhException("memcached not installed — bhserve install memcached"); return; }
        if (svc == "mailpit") { if (MailpitServer.Start()) Ok($"mailpit on UI :{MailpitServer.UiPort} / SMTP :{MailpitServer.SmtpPort}"); else throw new BhException("mailpit not installed — bhserve mailpit"); return; }
        if (svc.StartsWith("php"))
        {
            var v = PhpVersionOf(svc, cfg);
            if (PhpCgi.Start(v)) Ok($"php-cgi {v} on :{PhpCgi.PortFor(v)}"); else throw new BhException($"php {v} not installed");
            return;
        }
        throw new BhException($"don't know how to start '{svc}'");
    }

    public void Stop(string svc)
    {
        NeedInit();
        var cfg = Config.Load();
        if (svc is "all" or "")
        {
            Nginx.Stop(); Ok("nginx stopped");
            foreach (var v in SitePhpVersions(cfg)) { PhpCgi.Stop(v); Ok($"php-cgi {v} stopped"); }
            if (DbServer.Running()) { DbServer.Stop(); Ok("database stopped"); }
            if (PgServer.Running()) { PgServer.Stop(); Ok("PostgreSQL stopped"); }
            if (Redis.Running()) { Redis.Stop(); Ok("redis stopped"); }
            if (Memcached.Running()) { Memcached.Stop(); Ok("memcached stopped"); }
            if (MailpitServer.Running()) { MailpitServer.Stop(); Ok("mailpit stopped"); }
            if (Apache.Running()) { Apache.Stop(); Ok("apache stopped"); }
            return;
        }
        if (svc is "python" or "python3" or "fnm" or "node") { Ok($"{svc} is a tool — nothing to stop"); return; }
        if (svc == "nginx") { Nginx.Stop(); Ok("nginx stopped"); return; }
        if (svc is "mariadb" or "mysql") { DbServer.Stop(); Ok("database stopped"); return; }
        if (svc is "postgresql" or "postgres") { PgServer.Stop(); Ok("PostgreSQL stopped"); return; }
        if (svc == "apache")  { Apache.Stop(); Ok("apache stopped"); return; }
        if (svc == "redis")     { Redis.Stop(); Ok("redis stopped"); return; }
        if (svc == "memcached") { Memcached.Stop(); Ok("memcached stopped"); return; }
        if (svc == "mailpit") { MailpitServer.Stop(); Ok("mailpit stopped"); return; }
        if (svc.StartsWith("php")) { var v = PhpVersionOf(svc, cfg); PhpCgi.Stop(v); Ok($"php-cgi {v} stopped"); return; }
        throw new BhException($"don't know how to stop '{svc}'");
    }

    public void Restart(string svc) { Stop(svc); Start(svc); }
    public void Enable(string svc)  { NeedInit(); Services.Enable(svc, Config.Load()); Ok($"{svc} will auto-start"); }
    public void Disable(string svc) { NeedInit(); Services.Disable(svc, Config.Load()); Ok($"{svc} won't auto-start"); }

    // ── sites ─────────────────────────────────────────────────────────────────────

    /// <summary>A friendly, generic landing page written as a site's index.php when it has no own
    /// index yet (non-WordPress sites). Deliberately does NOT name the web server — an "Apache" site is
    /// fronted by nginx, so SERVER_SOFTWARE reads "nginx" and confused users into thinking nginx was
    /// installed instead of Apache. Static HTML in a .php file (proves PHP serves). Single-quoted HTML
    /// attrs so the C# verbatim string needs no escaping.</summary>
    private const string DefaultIndexPhp = @"<?php // BHServe default page - replace this index.php with your own site. ?>
<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Welcome to Your New Site!</title>
<style>
  body { font-family:Arial, sans-serif; margin:0; padding:0; display:flex; justify-content:center; align-items:center; min-height:100vh; background-color:#f8f8f8; }
  .container { max-width:600px; width:90%; padding:32px 20px; background:#fff; border-radius:10px; box-shadow:0 4px 8px rgba(0,0,0,.1); text-align:center; }
  h1 { color:#20a53a; margin:0 0 6px; }
  h3 { color:#555; font-weight:normal; }
  ul { list-style-type:none; padding:0; }
  ul li { margin:10px 0; font-size:16px; color:#444; }
  code { background:#eef7ee; color:#20a53a; padding:1px 6px; border-radius:4px; }
</style>
</head>
<body>
  <div class='container'>
    <h1>🎉 Congratulations!<br>Your website is live now!</h1>
    <h3>This default page has been automatically generated.</h3>
    <ul>
      <li>Your site's <code>index.php</code> file is located in the root directory.</li>
      <li>Feel free to modify, delete, or replace this page to customize your site!</li>
    </ul>
  </div>
</body>
</html>
";

    public void SiteAdd(string name, string php = "", string? root = null, string server = "", string type = "others")
    {
        NeedInit();
        if (string.IsNullOrWhiteSpace(name)) throw new BhException("usage: bhserve site add <name> [--php 8.4] [--root path]");
        // Allow dots for multi-label local domains (e.g. my.biswashost -> my.biswashost.test),
        // which Laragon supports. Must start and end with an alphanumeric.
        if (!Regex.IsMatch(name, "^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$"))
            throw new BhException($"invalid site name '{name}' (lowercase letters, digits, hyphens, dots)");

        var cfg = Config.Load();
        var domain = $"{name}.{cfg.Tld}";
        root ??= Path.Combine(cfg.SitesRoot, name);
        if (string.IsNullOrEmpty(server)) server = cfg.DefaultWeb;
        if (server is not ("nginx" or "apache")) throw new BhException("--server must be nginx or apache");
        if (server == "apache" && !Apache.Available) throw new BhException("apache backend needs httpd — install Apache from the Services page first");
        var phpKey = Services.PhpKey(php, cfg);
        var version = Services.PhpVersion(phpKey, cfg);

        Directory.CreateDirectory(root);
        if (!File.Exists(Path.Combine(root, "index.php")) && !File.Exists(Path.Combine(root, "index.html")))
            File.WriteAllText(Path.Combine(root, "index.php"), DefaultIndexPhp);

        if (PhpCgi.Start(version)) Ok($"php-cgi {version} on :{PhpCgi.PortFor(version)}");
        else Warn($"php {version} not installed — bhserve install php@{version} (site will 502 until then)");

        if (server == "apache")
        {
            Apache.RenderVhost(name, domain, root, phpKey, cfg);
            var (aok, amsg) = Apache.Start(); if (aok) Ok(amsg); else Warn(amsg);
            NginxConfig.RenderApacheFront(name, domain, root, phpKey, Apache.Port, cfg);
        }
        else NginxConfig.RenderPhpVhost(name, domain, root, phpKey, cfg);
        Ok($"site vhost: {Path.Combine(Paths.NginxSites, name + ".conf")}  (server={server})");

        // Serve it first (so the site works immediately), then map the hostname.
        if (Nginx.Running()) Nginx.Reload(cfg);
        else { var (ok, msg) = Nginx.Start(cfg); if (ok) Ok(msg); else Warn(msg); }

        Provision(name, type, root);
        EnsureHosts(domain);

        Hdr($"Site '{name}' added");
        Info($"url    : http://{domain}");
        Info($"root   : {root}");
        Info($"php    : {phpKey}   server: {server}   type: {type}");
    }

    /// <summary>Per-type setup: WordPress (DB + files + wp-config) or php (DB only).</summary>
    private void Provision(string name, string type, string root)
    {
        if (type is not ("php" or "wordpress")) return;
        // This site type needs a database. Make sure the server is installed + running,
        // installing it on demand (a fresh BHServe has no services yet).
        if (Tools.MysqldExe() is null)
        {
            Hdr("Installing MySQL (required for this site type)");
            try { Downloader.InstallDb().GetAwaiter().GetResult(); }
            catch (Exception ex) { Warn("MySQL install failed: " + ex.Message); }
        }
        if (!DbServer.Running())
        {
            var (ok, msg) = DbServer.Start();
            if (!ok) { Warn($"database server unavailable ({msg}) — install/start MySQL from the Services page, then create the DB"); return; }
        }
        var db = Regex.Replace(name, "[^A-Za-z0-9_]", "_");
        try { Database.Create(db); Ok($"database '{db}' ready  (root · no password · localhost)"); }
        catch (Exception ex) { Warn($"could not create database '{db}': {ex.Message}"); }

        if (type == "wordpress" && !File.Exists(Path.Combine(root, "wp-load.php")))
        {
            Hdr("Downloading WordPress (latest)");
            try { Downloader.InstallWordPress(root, db).GetAwaiter().GetResult(); Ok("WordPress installed — open the site to finish setup (title + admin user)"); }
            catch (Exception ex) { Warn("WordPress download failed: " + ex.Message); }
        }
    }

    /// <summary>Resolve where a site's files live and its database name (for the GUI's delete prompt).
    /// Root comes from the vhost if present, else the default sites-root/name.</summary>
    public (string root, string db) SiteTargets(string name)
    {
        var cfg = Config.Load();
        string? root = null;
        foreach (var c in new[] { Path.Combine(Paths.NginxSites, $"{name}.conf"), Path.Combine(Paths.NginxSites, $"{name}.conf.disabled") })
            if (File.Exists(c)) { try { root = ParseVhost(c).root; } catch { } if (!string.IsNullOrEmpty(root)) break; }
        if (string.IsNullOrEmpty(root)) root = Path.Combine(cfg.SitesRoot, name);
        return (Path.GetFullPath(root), Regex.Replace(name, "[^A-Za-z0-9_]", "_"));
    }

    /// <summary>Guard against deleting anything but a real per-site folder (never a drive root, the
    /// data dir, the sites-root itself, the user profile, Windows, or Program Files).</summary>
    private static bool SafeToDelete(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
        if (full.Length < 8) return false;
        if (string.Equals(full, Path.GetPathRoot(full)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
        var cfg = Config.Load();
        foreach (var bad in new[] { Paths.Home, cfg.SitesRoot,
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(bad) &&
                string.Equals(full, Path.GetFullPath(bad).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    public void SiteRemove(string name, bool purgeFiles = false, bool dropDb = false)
    {
        NeedInit();
        if (!Regex.IsMatch(name, "^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$")) throw new BhException($"invalid site name '{name}'");
        var cfg = Config.Load();
        var (root, db) = SiteTargets(name);   // resolve BEFORE we delete the vhost

        foreach (var f in new[] { $"{name}.conf", $"{name}.conf.disabled" })
            try { File.Delete(Path.Combine(Paths.NginxSites, f)); } catch { }
        Apache.RemoveVhost(name);
        Ok($"removed vhost for {name}");
        var rmDomain = $"{name}.{cfg.Tld}";
        if (!Hosts.Remove(rmDomain) && Hosts.Has(rmDomain)) Elevation.Run("hosts-remove", rmDomain);
        if (Nginx.Running()) Nginx.Reload(cfg);
        Apache.Reload();

        if (dropDb)
        {
            try
            {
                if (DbServer.Running() && Database.List().Contains(db)) { Database.Drop(db); Ok($"dropped database '{db}'"); }
                else Info($"database '{db}' not present — nothing to drop");
            }
            catch (Exception ex) { Warn($"could not drop database '{db}': {ex.Message}"); }
        }

        if (purgeFiles)
        {
            if (!SafeToDelete(root)) Warn($"refused to delete an unsafe path: {root} (remove it manually)");
            else if (!Directory.Exists(root)) Info($"no files to delete at {root}");
            else { try { Directory.Delete(root, true); Ok($"deleted site files: {root}"); } catch (Exception ex) { Warn($"could not delete files: {ex.Message}"); } }
        }
        else Info("(site files left on disk; remove manually if desired)");
    }

    public void SitePhp(string name, string version)
    {
        NeedInit();
        var cfg = Config.Load();
        var conf = Path.Combine(Paths.NginxSites, $"{name}.conf");
        if (!File.Exists(conf)) throw new BhException($"no such site: {name}");
        var (domain, root, _) = ParseVhost(conf);
        var phpKey = Services.PhpKey(version, cfg);
        RenderSite(name, domain, root, phpKey, VhostServer(conf), cfg);
        if (Nginx.Running()) Nginx.Reload(cfg);
        Ok($"{name} now on {phpKey}");
    }

    /// <summary>Enable (serve) or disable a site by toggling its vhost on/off (rename ↔ .disabled).</summary>
    public void SiteEnable(string name, bool enable)
    {
        NeedInit();
        var on  = Path.Combine(Paths.NginxSites, $"{name}.conf");
        var off = Path.Combine(Paths.NginxSites, $"{name}.conf.disabled");
        if (enable)
        {
            if (File.Exists(off)) { File.Move(off, on, true); Ok($"enabled {name}"); }
            else Warn($"{name} already enabled / missing");
        }
        else
        {
            if (File.Exists(on)) { File.Move(on, off, true); Ok($"disabled {name}"); }
            else Warn($"{name} already disabled / missing");
        }
        if (Nginx.Running()) Nginx.Reload(Config.Load());
    }

    /// <summary>Change a site's document root and re-render its vhost.</summary>
    public void SiteRoot(string name, string newRoot)
    {
        NeedInit();
        var cfg = Config.Load();
        var conf = Path.Combine(Paths.NginxSites, $"{name}.conf");
        if (!File.Exists(conf)) throw new BhException($"no such site: {name}");
        var (domain, _, phpKey) = ParseVhost(conf);
        Directory.CreateDirectory(newRoot);
        RenderSite(name, domain, newRoot, phpKey, VhostServer(conf), cfg);
        if (Nginx.Running()) Nginx.Reload(cfg);
        Ok($"{name} root → {newRoot}");
    }

    /// <summary>Switch a site between the nginx and apache backends.</summary>
    public void SiteServer(string name, string server)
    {
        NeedInit();
        if (server is not ("nginx" or "apache")) throw new BhException("usage: bhserve site server <name> <nginx|apache>");
        var cfg = Config.Load();
        var conf = Path.Combine(Paths.NginxSites, $"{name}.conf");
        if (!File.Exists(conf)) throw new BhException($"no such site: {name}");
        if (server == "apache" && !Apache.Available) throw new BhException("apache backend needs httpd — install Apache");
        var (domain, root, phpKey) = ParseVhost(conf);
        if (server == "nginx") Apache.RemoveVhost(name);   // leaving apache → drop its vhost
        RenderSite(name, domain, root, phpKey, server, cfg);
        if (Nginx.Running()) Nginx.Reload(cfg);
        Apache.Reload();
        Ok($"{name} now served by {server}");
    }

    /// <summary>Render a site's vhost(s) for the chosen backend + ensure its php-cgi is up.</summary>
    private void RenderSite(string name, string domain, string root, string phpKey, string server, Config cfg)
    {
        PhpCgi.Start(Services.PhpVersion(phpKey, cfg));
        if (server == "apache")
        {
            Apache.RenderVhost(name, domain, root, phpKey, cfg);
            Apache.Start();
            NginxConfig.RenderApacheFront(name, domain, root, phpKey, Apache.Port, cfg);
        }
        else NginxConfig.RenderPhpVhost(name, domain, root, phpKey, cfg);
    }

    private static string VhostServer(string conf) =>
        File.ReadAllText(conf).Contains("server=apache") ? "apache" : "nginx";

    private static bool AnyApacheSite() =>
        Directory.Exists(Paths.NginxSites) &&
        Directory.EnumerateFiles(Paths.NginxSites, "*.conf").Any(f => File.ReadAllText(f).Contains("server=apache"));

    public void Secure(string domain)
    {
        NeedInit();
        var mkc = Tools.MkcertExe() ?? throw new BhException("mkcert not installed — run: bhserve install mkcert");
        Directory.CreateDirectory(Paths.Certs);
        Hdr($"Provisioning trusted cert for {domain}");

        EnsureMkcertCa(mkc);

        var (_, outp) = RunCapture(mkc,
            $"-cert-file \"{domain}.pem\" -key-file \"{domain}-key.pem\" {domain}", Paths.Certs);
        if (!File.Exists(Path.Combine(Paths.Certs, $"{domain}.pem")))
            throw new BhException("mkcert failed:\n" + outp);
        Ok($"cert: {Path.Combine(Paths.Certs, domain + ".pem")}");

        RerenderVhostAndRestart(domain);
        Ok($"secured: https://{domain}");
    }

    /// <summary>Remove HTTPS from a site: delete its cert + key and re-render the vhost
    /// (which drops the ssl listen block since the cert files are gone). The site keeps
    /// serving over http. Use Resecure for a clean fresh certificate instead.</summary>
    public void Unsecure(string domain)
    {
        NeedInit();
        Hdr($"Removing HTTPS from {domain}");
        var cert = Path.Combine(Paths.Certs, $"{domain}.pem");
        var key  = Path.Combine(Paths.Certs, $"{domain}-key.pem");
        var had = File.Exists(cert);
        try { File.Delete(cert); } catch { }
        try { File.Delete(key); } catch { }
        Ok(had ? "certificate removed" : "no certificate found (already http-only)");
        RerenderVhostAndRestart(domain);
        Ok($"unsecured: http://{domain}");
    }

    /// <summary>Reinstall HTTPS: drop the old cert + key and issue a completely fresh one
    /// (new key + new serial). This is the GUI remedy when a site's certificate shows as
    /// untrusted — e.g. after an AV/OS trust hiccup — without touching anything else.</summary>
    public void Resecure(string domain)
    {
        NeedInit();
        Hdr($"Reinstalling HTTPS for {domain}");
        try { File.Delete(Path.Combine(Paths.Certs, $"{domain}.pem")); } catch { }
        try { File.Delete(Path.Combine(Paths.Certs, $"{domain}-key.pem")); } catch { }
        Secure(domain);
    }

    /// <summary>Re-render a site's vhost (proxy-aware) after a cert change and fully RESTART
    /// nginx. Restart, NOT reload: old workers keep serving the PREVIOUS certs on kept-alive
    /// connections after a reload, which left stale certs live for hours — an HTTPS-scanning
    /// AV then kept flagging them as untrusted. Stop+start guarantees fresh certs are served.</summary>
    private void RerenderVhostAndRestart(string domain)
    {
        // Site name = domain minus the ".<tld>" suffix — NOT Split('.')[0], which broke
        // multi-label sites (api.amarmedi.test → "api" → api.conf not found → re-render
        // silently skipped, so those sites never got their vhost refreshed on secure).
        var tldSuffix = "." + Config.Load().Tld;
        var name = domain.EndsWith(tldSuffix, StringComparison.OrdinalIgnoreCase)
            ? domain[..^tldSuffix.Length]
            : domain.Split('.')[0];
        var conf = Path.Combine(Paths.NginxSites, $"{name}.conf");
        if (!File.Exists(conf)) return;
        var cfg = Config.Load();
        // A proxy vhost (Mailpit, or any ported service) has a proxy_pass and no PHP root —
        // re-render it with the proxy renderer; RenderPhpVhost would produce a broken vhost.
        var pm = Regex.Match(File.ReadAllText(conf), @"proxy_pass http://127\.0\.0\.1:(\d+)");
        if (pm.Success)
        {
            NginxConfig.RenderProxyVhost(name, domain, int.Parse(pm.Groups[1].Value), cfg);
            Ok("re-rendered proxy vhost");
        }
        else
        {
            var (dom, root, phpKey) = ParseVhost(conf);
            NginxConfig.RenderPhpVhost(name, dom, root, phpKey, cfg);
            Ok("re-rendered vhost");
        }
        if (Nginx.Running()) { Nginx.Stop(); System.Threading.Thread.Sleep(300); Nginx.Start(cfg); }
    }

    // ── status / api ────────────────────────────────────────────────────────────
    public Snapshot Api()
    {
        var cfg = Config.Load();
        var services = Services.All.Select(s =>
        {
            var installed = Services.Installed(s.Key, cfg);
            var running = s.Role switch
            {
                ServiceRole.Web => (s.Key == "nginx" && Nginx.Running()) || (s.Key == "apache" && Apache.Running()),
                ServiceRole.Php => PhpCgi.Running(Services.PhpVersion(s.Key, cfg)),
                ServiceRole.Db    => s.Key switch
                {
                    "mysql"      => DbServer.ActiveEngine() == "mysql",
                    "mariadb"    => DbServer.ActiveEngine() == "mariadb",
                    "postgresql" => PgServer.Running(),
                    _ => false,
                },
                ServiceRole.Mail  => s.Key == "mailpit" && MailpitServer.Running(),
                ServiceRole.Cache => (s.Key == "redis" && Redis.Running()) || (s.Key == "memcached" && Memcached.Running()),
                _ => false,
            };
            return new Service(s.Key, s.Role, installed, running, "", Services.Enabled(s.Key, cfg));
        }).ToList();
        return new Snapshot(services, ListSites(cfg));
    }

    public void Status()
    {
        var snap = Api();
        Hdr("Services");
        foreach (var s in snap.Services.Where(s => s.Installed || s.Running || s.AutoStart))
            Out($"  {(s.Running ? "●" : "○")} {s.Key,-12} {(s.Installed ? "installed" : "—"),-10} {(s.Running ? "running" : "stopped"),-8} {(s.AutoStart ? "auto" : "")}");
        Hdr("Sites");
        if (snap.Sites.Count == 0) Info("no sites yet — bhserve site add <name>");
        foreach (var st in snap.Sites)
            Out($"  ✓ {st.Name,-18} {st.Domain}  [{st.Php}]  {(st.Secure ? "https" : "http")}");
    }

    // ── elevation-backed helpers ────────────────────────────────────────────────
    /// <summary>Ensure 127.0.0.1 → domain in the hosts file (direct if admin, else one UAC prompt).</summary>
    private void EnsureHosts(string domain)
    {
        if (Hosts.Has(domain)) { Ok($"hosts: {domain} (already mapped)"); return; }
        // Escape hatch for CI/automation where no one can click the UAC prompt.
        if (Environment.GetEnvironmentVariable("BHSERVE_SKIP_HOSTS") is { Length: > 0 })
        {
            Warn($"hosts skipped (BHSERVE_SKIP_HOSTS) — add manually: 127.0.0.1 {domain}");
            return;
        }
        if (Hosts.Add(domain)) { Ok($"hosts: {domain} → 127.0.0.1"); return; }
        Info("requesting admin to update the hosts file (UAC)…");
        Elevation.Run("hosts-add", domain);
        // Judge success by READING THE HOSTS FILE BACK, not the elevated child's exit code: the exit
        // code of a runas child launched from a non-elevated process is unreliable (it can throw/misreport
        // on some Windows builds → a false "not updated" even when the write succeeded). Re-check briefly
        // in case the helper's handle returned before the write flushed.
        var added = false;
        for (var i = 0; i < 6 && !(added = Hosts.Has(domain)); i++) System.Threading.Thread.Sleep(300);
        if (added)
            Ok($"hosts: {domain} → 127.0.0.1 (elevated)");
        else
            Warn($"hosts not updated — add manually: 127.0.0.1 {domain}  " +
                 "(some antivirus/security tools block edits to the hosts file — allow hosts changes there, or add the line by hand)");
    }

    /// <summary>Ensure mkcert's local CA exists + is ACTUALLY TRUSTED (first run needs admin/UAC).
    /// rootCA.pem merely existing on disk does NOT mean the CA is in the trust stores — mkcert can
    /// generate it without installing, and HTTPS-scanning AVs additionally require it in the
    /// MACHINE store. Verify the store by thumbprint before skipping the install.</summary>
    private void EnsureMkcertCa(string mkc)
    {
        var (_, caroot) = RunCapture(mkc, "-CAROOT", null);
        caroot = caroot.Trim();
        var rootPem = string.IsNullOrEmpty(caroot) ? null : Path.Combine(caroot, "rootCA.pem");
        if (rootPem is not null && File.Exists(rootPem) && CaTrusted(rootPem)) return;

        Info("installing mkcert local CA (one-time, needs admin)…");
        if (Elevation.Run("mkcert-install")) Ok("mkcert CA installed (browsers + AV scanners will trust BHServe certs)");
        else Warn("mkcert CA not installed in trust store — certs work but show untrusted (curl -k is fine)");
    }

    /// <summary>True when the given CA pem is present in BOTH the user and machine Root stores.
    /// The machine store matters because HTTPS-scanning security software (ESET etc.) validates
    /// against it; user-store-only trust shows ERR_CERT_AUTHORITY_INVALID behind such scanners.</summary>
    private static bool CaTrusted(string rootPem)
    {
        try
        {
            using var ca = new System.Security.Cryptography.X509Certificates.X509Certificate2(rootPem);
            foreach (var loc in new[] { System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
                                        System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine })
            {
                using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                    System.Security.Cryptography.X509Certificates.StoreName.Root, loc);
                store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                var hit = store.Certificates.Find(
                    System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, ca.Thumbprint, false);
                if (hit.Count == 0) return false;
            }
            return true;
        }
        catch { return false; }   // can't verify → run the installer (idempotent)
    }

    private static (int code, string output) RunCapture(string exe, string args, string? cwd)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = cwd ?? Path.GetDirectoryName(exe)!,
        };
        var p = System.Diagnostics.Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────
    private static string PhpVersionOf(string svc, Config cfg) =>
        Services.PhpVersion(Services.PhpKey(svc == "php" ? "default" : svc[(svc.IndexOf('@') + 1)..], cfg), cfg);

    /// <summary>Distinct PHP versions referenced by the existing site vhosts (+ the default).</summary>
    private static IEnumerable<string> SitePhpVersions(Config cfg)
    {
        var set = new HashSet<string> { cfg.DefaultPhp };
        if (Directory.Exists(Paths.NginxSites))
            foreach (var f in Directory.EnumerateFiles(Paths.NginxSites, "*.conf"))
            {
                var m = Regex.Match(File.ReadAllText(f), @"php=php@?([0-9.]+)");
                if (m.Success) set.Add(m.Groups[1].Value);
            }
        return set;
    }

    private static (string domain, string root, string phpKey) ParseVhost(string conf)
    {
        var text = File.ReadAllText(conf);
        var domain = Regex.Match(text, @"server_name\s+([^;]+);").Groups[1].Value.Trim();
        var root   = Regex.Match(text, @"(?m)^\s*root\s+([^;]+);").Groups[1].Value.Trim();
        var phpKey = Regex.Match(text, @"php=(\S+)").Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(phpKey)) phpKey = "php";
        return (domain, root, phpKey);
    }

    private static IReadOnlyList<Site> ListSites(Config cfg)
    {
        var list = new List<Site>();
        if (!Directory.Exists(Paths.NginxSites)) return list;
        // enabled (*.conf) and disabled (*.conf.disabled) vhosts
        foreach (var f in Directory.EnumerateFiles(Paths.NginxSites, "*.conf*"))
        {
            var fname = Path.GetFileName(f);
            if (!fname.EndsWith(".conf") && !fname.EndsWith(".conf.disabled")) continue;
            var enabled = fname.EndsWith(".conf");
            var name = enabled ? fname[..^".conf".Length] : fname[..^".conf.disabled".Length];
            var text = File.ReadAllText(f);
            var domain = Regex.Match(text, @"server_name\s+([^;]+);").Groups[1].Value.Trim();
            var php    = Regex.Match(text, @"php=(\S+)").Groups[1].Value.Trim();
            var root   = Regex.Match(text, @"(?m)^\s*root\s+([^;]+);").Groups[1].Value.Trim();
            var secure = text.Contains("ssl_certificate ");
            list.Add(new Site(name, domain, php, root, secure, enabled,
                              text.Contains("server=apache") ? "apache" : "nginx"));
        }
        return list;
    }

    // ── not-yet-on-Windows (phase 4/5) ─────────────────────────────────────────────
    public string PhpIniPath(string version) => Php.IniPath(version);
    public void PhpIniReload(string version)
    {
        if (Php.IniReload(version)) Ok($"php-cgi {version} reloaded");
        else Info($"php {version} not running — changes apply on next start");
    }

    public void PhpIoncube(string version)
    {
        NeedInit();
        Hdr($"Enabling ionCube for PHP {version}");
        Php.Ioncube(version, Ok);
    }

    public void PhpStatus()
    {
        NeedInit();
        Hdr("PHP versions");
        foreach (var p in Php.Status())
            Out($"  ✓ {p.Version,-8} {(p.Running ? "running" : "stopped"),-8} ionCube: {p.Ioncube}");
    }

    // ── logs ────────────────────────────────────────────────────────────────────
    public void Logs(string name = "", int lines = 200)
    {
        NeedInit();
        if (name is "--list" or "list")
        {
            Hdr("Logs");
            if (Directory.Exists(Paths.Logs))
                foreach (var f in Directory.EnumerateFiles(Paths.Logs, "*.log")) Ok(Path.GetFileName(f));
            return;
        }
        if (name == "") name = "nginx-error.log";
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) throw new BhException("invalid log name");
        var path = Path.Combine(Paths.Logs, name);
        if (!File.Exists(path)) { Info($"(no log yet: {name})"); return; }
        foreach (var line in File.ReadLines(path).TakeLast(lines)) Out(line);
    }

    public IReadOnlyList<string> LogFiles() =>
        Directory.Exists(Paths.Logs)
            ? Directory.EnumerateFiles(Paths.Logs, "*.log").Select(Path.GetFileName).Where(n => n is not null).Cast<string>().OrderBy(n => n).ToList()
            : Array.Empty<string>();

    public string LogText(string name, int lines = 400)
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return "";
        var path = Path.Combine(Paths.Logs, name);
        return File.Exists(path) ? string.Join("\n", File.ReadLines(path).TakeLast(lines)) : "(empty)";
    }

    // ── doctor ──────────────────────────────────────────────────────────────────
    public void Doctor()
    {
        var cfg = Config.Load();
        Hdr("Tools");
        Tool("nginx",   Tools.NginxExe());
        Tool($"php {cfg.DefaultPhp}", Tools.PhpCgiExe(cfg.DefaultPhp));
        Tool("mysqld",  Tools.MysqldExe());
        Tool("mkcert",  Tools.MkcertExe());
        Tool("mailpit", Tools.MailpitExe());
        Tool("fnm",     Tools.FnmExe());

        Hdr("Ports");
        Port("HTTP ", cfg.HttpPort);
        Port("HTTPS", cfg.HttpsPort);
        Port("MySQL", DbServer.Port);

        Hdr("Environment");
        Info($"data dir : {Paths.Home}");
        Info($"hosts editable now (admin): {Hosts.IsElevated()}  (else BHServe prompts via UAC)");
        Info($"initialized: {Directory.Exists(Paths.Config)}");

        Hdr("Database host (Windows localhost stall)");
        var fixedHosts = SiteDbHostFix.Run(cfg.SitesRoot);
        if (fixedHosts.Count == 0) Ok("all site configs already use 127.0.0.1 (no slow localhost lookups)");
        else foreach (var f in fixedHosts) Ok($"fixed localhost -> 127.0.0.1 : {f}");
    }

    private void Tool(string label, string? path)
    {
        if (path is not null) Ok($"{label,-12} {path}");
        else Warn($"{label,-12} not found (install via bhserve install / the GUI)");
    }

    private void Port(string label, int port)
    {
        var open = PortInUse(port);
        if (open) Ok($"{label} :{port}  in use (a server is listening)");
        else Info($"{label} :{port}  free");
    }

    private static bool PortInUse(int port)
    {
        try { using var c = new System.Net.Sockets.TcpClient(); return c.ConnectAsync("127.0.0.1", port).Wait(400) && c.Connected; }
        catch { return false; }
    }

    // ── config ──────────────────────────────────────────────────────────────────
    public void ConfigShow() { NeedInit(); Out(File.ReadAllText(Paths.ConfigJson)); }

    public void ConfigSet(string key, string val)
    {
        NeedInit();
        var cfg = Config.Load();
        switch (key)
        {
            case "tld":
                if (!Regex.IsMatch(val, "^[a-z][a-z0-9-]*$")) throw new BhException("tld must be lowercase letters/digits/hyphen");
                cfg.Tld = val; break;
            case "http_port":  cfg.HttpPort  = ParsePort(val, key); break;
            case "https_port": cfg.HttpsPort = ParsePort(val, key); break;
            case "default_php": cfg.DefaultPhp = Services.PhpVersion(Services.PhpKey(val, cfg), cfg); break;
            case "default_web":
                if (val is not ("nginx" or "apache")) throw new BhException("default_web must be nginx|apache");
                cfg.DefaultWeb = val; break;
            case "sites_root":
                if (string.IsNullOrWhiteSpace(val)) throw new BhException("sites_root cannot be empty");
                cfg.SitesRoot = val; break;
            case "autostart":
                if (val is not ("true" or "false")) throw new BhException("autostart must be true|false");
                cfg.Autostart = val == "true"; break;
            default: throw new BhException($"unknown/uneditable key: {key}");
        }
        cfg.Save();
        Ok($"set {key} = {val}");
        if (key is "http_port" or "https_port" or "tld")
        {
            RegenVhosts(cfg);
            NginxConfig.RenderMain(cfg);
            Warn("restart nginx to apply: bhserve restart nginx");
            if (key == "tld") Warn($"TLD changed — re-issue HTTPS per site: bhserve secure <name>.{val}");
        }
    }

    private static int ParsePort(string v, string key) =>
        int.TryParse(v, out var p) && p is > 0 and < 65536 ? p : throw new BhException($"{key} must be a port number");

    /// <summary>Re-render every site vhost (e.g. after a TLD/port change), proxy sites included.</summary>
    private static void RegenVhosts(Config cfg)
    {
        if (!Directory.Exists(Paths.NginxSites)) return;
        foreach (var f in Directory.EnumerateFiles(Paths.NginxSites, "*.conf"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var text = File.ReadAllText(f);
            var domain = $"{name}.{cfg.Tld}";
            var pm = Regex.Match(text, @"proxy_pass http://127\.0\.0\.1:(\d+)");
            if (pm.Success) { NginxConfig.RenderProxyVhost(name, domain, int.Parse(pm.Groups[1].Value), cfg); continue; }
            var (_, root, phpKey) = ParseVhost(f);
            if (root.Length > 0) NginxConfig.RenderPhpVhost(name, domain, root, phpKey, cfg);
        }
    }
    public void Db(string sub, params string[] args)
    {
        NeedInit();
        var name = args.Length > 0 ? args[0] : "";
        switch (sub)
        {
            case "" or "list":
                var dbs = Database.List();
                Hdr("Databases (MySQL/MariaDB)");
                if (dbs.Count == 0) Info("none — start the server (bhserve start mariadb) then create one");
                foreach (var d in dbs) Ok(d);
                break;
            case "create":
            {
                if (name == "") throw new BhException("usage: bhserve db create <name> [password]");
                var pw = args.Length > 1 ? args[1] : "";
                Database.Create(name, pw);
                if (pw.Length > 0) Ok($"database '{name}' ready  (user: {name} · password: {pw} · 127.0.0.1:{DbServer.Port})");
                else Ok($"database '{name}' ready  (root · no password · 127.0.0.1:{DbServer.Port})");
                break;
            }
            case "passwd":
                if (name == "" || args.Length < 2) throw new BhException("usage: bhserve db passwd <name> <password>");
                Database.SetPassword(name, args[1]);
                Ok($"set password for user '{name}'");
                break;
            case "drop":
                if (name == "") throw new BhException("usage: bhserve db drop <name>");
                Database.Drop(name); Ok($"dropped database '{name}'");
                break;
            case "rootpw":
                Database.SetRootPassword(name);   // name = the new password ("" clears it)
                Ok(name.Length > 0 ? "root password set" : "root password cleared");
                break;
            default: throw new BhException("usage: bhserve db {list|create|drop|passwd|rootpw} [name] [password]");
        }
    }

    /// <summary>True if root@localhost currently has a password set.</summary>
    public bool DbHasRootPassword() { NeedInit(); return Database.HasRootPassword; }

    /// <summary>PostgreSQL database ops (mirrors Db, but for the postgres server).</summary>
    public void Pg(string sub, params string[] args)
    {
        NeedInit();
        var name = args.Length > 0 ? args[0] : "";
        switch (sub)
        {
            case "" or "list":
                var dbs = PgDatabase.List();
                Hdr("Databases (PostgreSQL)");
                if (dbs.Count == 0) Info("none — start the server (bhserve start postgresql) then create one");
                foreach (var d in dbs) Ok(d);
                break;
            case "create":
                if (name == "") throw new BhException("usage: bhserve pg create <name>");
                PgDatabase.Create(name); Ok($"PostgreSQL database '{name}' ready (postgres · 127.0.0.1:{PgServer.Port})");
                break;
            case "drop":
                if (name == "") throw new BhException("usage: bhserve pg drop <name>");
                PgDatabase.Drop(name); Ok($"dropped PostgreSQL database '{name}'");
                break;
            default: throw new BhException("usage: bhserve pg {list|create|drop} [name]");
        }
    }

    public IReadOnlyList<string> PgList() { NeedInit(); return PgDatabase.List(); }
    public bool PgRunning() => PgServer.Running();

    /// <summary>Node version management via fnm (the mac engine's `node` verb).</summary>
    public void Node(string sub, params string[] args)
    {
        NeedInit();
        var fnm = Tools.FnmExe();
        if (fnm is null)
        {
            Hdr("Installing fnm (Node version manager)");
            fnm = Downloader.InstallFnm().GetAwaiter().GetResult();
            Ok($"fnm: {fnm}");
        }
        var nodeDir = Path.Combine(Paths.Home, "node");
        Directory.CreateDirectory(nodeDir);
        var v = args.Length > 0 ? args[0] : "";
        var fnmArgs = sub switch
        {
            "" or "list" or "ls"      => "list",
            "install" or "i"          => $"install {v}",
            "use" or "default"        => $"default {v}",
            "uninstall" or "rm" or "uni" => $"uninstall {v}",
            _ => throw new BhException("usage: bhserve node {list|install|use|uninstall} [version]"),
        };
        if (sub is "install" or "i" or "use" or "default" or "uninstall" or "rm" or "uni" && v == "")
            throw new BhException($"usage: bhserve node {sub} <version>");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fnm, Arguments = fnmArgs,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = nodeDir,
        };
        psi.Environment["FNM_DIR"] = nodeDir;
        var p = System.Diagnostics.Process.Start(psi)!;
        var outp = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).TrimEnd();
        p.WaitForExit();
        if (outp.Length > 0) Out(outp);
        if (p.ExitCode != 0) throw new BhException($"fnm {fnmArgs} failed");
        if (sub is "install" or "i") Ok($"node {v} installed — run with: fnm use {v} (FNM_DIR={nodeDir})");
    }

    /// <summary>Installed Node versions (via fnm) + which is the default — for the GUI list.</summary>
    public IReadOnlyList<(string version, bool isDefault)> NodeVersions()
    {
        NeedInit();
        var fnm = Tools.FnmExe();
        if (fnm is null) return Array.Empty<(string, bool)>();
        var nodeDir = Path.Combine(Paths.Home, "node");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fnm, Arguments = "list", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = nodeDir,
        };
        psi.Environment["FNM_DIR"] = nodeDir;
        string outp;
        try { var p = System.Diagnostics.Process.Start(psi)!; outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); }
        catch { return Array.Empty<(string, bool)>(); }
        var list = new List<(string, bool)>();
        foreach (var line in outp.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(line, @"v(\d+\.\d+\.\d+)");
            if (m.Success) list.Add((m.Groups[1].Value, line.Contains("default")));
        }
        return list;
    }

    // ── Node-app sites (supervised frontend/backend behind an nginx reverse proxy) ──
    public void NodeSiteAdd(string name, string feDir, string feCmd, int fePort,
                            string? beDir, string? beCmd, int bePort, string apiPath)
    {
        NeedInit();
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$")) throw new BhException($"invalid name '{name}'");
        if (string.IsNullOrWhiteSpace(feDir) || string.IsNullOrWhiteSpace(feCmd) || fePort <= 0)
            throw new BhException("frontend dir, cmd and port are required");
        var cfg = Config.Load();
        var domain = $"{name}.{cfg.Tld}";
        // sanitize the api path (guard against a shell mangling "/api" into a drive path)
        var api = Regex.Replace(apiPath ?? "", "[^a-zA-Z0-9/_-]", "").Trim('/');
        if (api.Length == 0 || apiPath!.Contains(':') || apiPath.Contains(' ')) api = "api";
        var nc = new NodeSiteConfig
        {
            Name = name, ApiPath = "/" + api,
            Frontend = new NodeProc { Dir = feDir, Cmd = feCmd, Port = fePort },
            Backend = (!string.IsNullOrWhiteSpace(beDir) && !string.IsNullOrWhiteSpace(beCmd) && bePort > 0)
                ? new NodeProc { Dir = beDir!, Cmd = beCmd!, Port = bePort } : null,
        };
        NodeSite.Save(nc);
        NodeSite.RenderVhost(nc, domain, cfg);
        Ok($"node-app vhost: {Path.Combine(Paths.NginxSites, name + ".conf")}");
        var (ok, msg) = NodeSite.Start(name); if (ok) Ok(msg); else Warn(msg);
        if (Nginx.Running()) Nginx.Reload(cfg); else { var (nok, nmsg) = Nginx.Start(cfg); if (!nok) Warn(nmsg); }
        EnsureHosts(domain);
        Hdr($"Node-app '{name}' added");
        Info($"url    : http://{domain}");
        Info($"frontend: {feCmd}  (dir {feDir}, :{fePort})");
        if (nc.Backend is not null) Info($"backend : {beCmd}  (dir {beDir}, :{bePort})  · {nc.ApiPath} → backend");
    }

    public void NodeSiteStart(string name)   { NeedInit(); var (ok, msg) = NodeSite.Start(name); if (ok) Ok(msg); else No(msg); }
    public void NodeSiteStop(string name)    { NeedInit(); NodeSite.Stop(name); Ok($"node-app {name} stopped"); }
    public void NodeSiteRestart(string name) { NodeSiteStop(name); System.Threading.Thread.Sleep(400); NodeSiteStart(name); }

    /// <summary>`npm install` in a node-app's frontend ("frontend") or backend ("backend") dir.</summary>
    public (bool ok, string output) NodeSiteNpm(string name, string which)
    {
        NeedInit();
        var (ok, output) = NodeSite.Npm(name, which);
        if (ok) Ok($"npm install ({which}) complete for {name}"); else No($"npm install ({which}) failed for {name}");
        return (ok, output);
    }

    // Accessors the GUI uses for the logs viewer / .env editor / edit-config sheets.
    public string  NodeSiteLog(string name, string which)     { NeedInit(); return NodeSite.LogTail(name, which); }
    public string  NodeSiteEnvPath(string name, string which) { NeedInit(); return NodeSite.EnvPath(name, which); }
    public string  NodeSiteDir(string name, string which)     { NeedInit(); return NodeSite.ProcDir(name, which); }
    public NodeSiteConfig? NodeSiteConfig(string name)        { NeedInit(); return NodeSite.Load(name); }

    /// <summary>Change a node-app's commands/ports/api-path, re-render the vhost, and restart it.</summary>
    public void NodeSiteEdit(string name, string feCmd, int fePort, string? beCmd, int bePort, string apiPath)
    {
        NeedInit();
        var nc = NodeSite.Load(name) ?? throw new BhException($"no node-app '{name}'");
        var appCfg = Config.Load();
        var domain = $"{name}.{appCfg.Tld}";
        NodeSite.Stop(name);
        if (!string.IsNullOrWhiteSpace(feCmd)) nc.Frontend.Cmd = feCmd;
        if (fePort > 0) nc.Frontend.Port = fePort;
        if (nc.Backend is not null)
        {
            if (!string.IsNullOrWhiteSpace(beCmd)) nc.Backend.Cmd = beCmd!;
            if (bePort > 0) nc.Backend.Port = bePort;
        }
        var api = Regex.Replace(apiPath ?? "", "[^a-zA-Z0-9/_-]", "").Trim('/');
        if (api.Length > 0 && !(apiPath!.Contains(':') || apiPath.Contains(' '))) nc.ApiPath = "/" + api;
        NodeSite.Save(nc);
        NodeSite.RenderVhost(nc, domain, appCfg);
        if (Nginx.Running()) Nginx.Reload(appCfg);
        var (ok, msg) = NodeSite.Start(name); if (ok) Ok(msg); else Warn(msg);
        Ok($"node-app {name} updated");
    }

    public void NodeSiteRemove(string name)
    {
        NeedInit();
        NodeSite.Stop(name);
        NodeSite.Delete(name);
        try { File.Delete(Path.Combine(Paths.NginxSites, $"{name}.conf")); } catch { }
        var cfg = Config.Load();
        if (Nginx.Running()) Nginx.Reload(cfg);
        Ok($"removed node-app {name}");
    }

    public void NodeSiteList()
    {
        NeedInit();
        Hdr("Node-app sites");
        var any = false;
        foreach (var n in NodeSite.List())
        {
            var c = NodeSite.Load(n);
            Ok($"{n,-16} {(NodeSite.Running(n) ? "running" : "stopped"),-8} fe:{c?.Frontend.Port}{(c?.Backend is not null ? $" be:{c.Backend.Port}" : "")}");
            any = true;
        }
        if (!any) Info("none — bhserve nodesite add <name> --fe-dir … --fe-cmd … --fe-port …");
    }

    // ── Python-app sites (one supervised process behind an nginx reverse proxy) ──
    public void PySiteAdd(string name, string dir, string cmd, int port, bool venv)
    {
        NeedInit();
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$")) throw new BhException($"invalid name '{name}'");
        if (string.IsNullOrWhiteSpace(dir) || port <= 0) throw new BhException("a project folder and a port are required");
        if (!Directory.Exists(dir)) throw new BhException($"folder not found: {dir}");
        var cfg = Config.Load();
        var domain = $"{name}.{cfg.Tld}";
        var pc = new PySiteConfig
        {
            Name = name, Dir = dir, Port = port, Venv = venv,
            Cmd = string.IsNullOrWhiteSpace(cmd) ? "python app.py" : cmd,
            PyVer = Tools.PythonVersion() ?? "",
        };
        PySite.Save(pc);

        if (venv)
        {
            Hdr("Creating virtualenv (.venv)");
            var (vok, vout) = PySite.MakeVenv(dir);
            if (vok) Ok("virtualenv ready (.venv) — run 'pip install' from the row menu for requirements.txt");
            else Warn("virtualenv not created: " + vout);
        }

        TryIssueCert(domain);                  // best-effort HTTPS; RenderVhost picks up the cert
        PySite.RenderVhost(pc, domain, cfg);
        Ok($"python-app vhost: {Path.Combine(Paths.NginxSites, name + ".conf")}");
        var (ok, msg) = PySite.Start(name); if (ok) Ok(msg); else Warn(msg);
        if (Nginx.Running()) Nginx.Reload(cfg); else { var (nok, nmsg) = Nginx.Start(cfg); if (!nok) Warn(nmsg); }
        EnsureHosts(domain);
        Hdr($"Python-app '{name}' added");
        Info($"url : https://{domain}");
        Info($"run : {pc.Cmd}  (dir {dir}, :{port})");
        Info("tip : the PORT env var is set for your app — read os.environ['PORT'] in code, or use %PORT% in the command (e.g. \"gunicorn app:app -b 127.0.0.1:%PORT%\").");
    }

    public void PySiteStart(string name)   { NeedInit(); var (ok, msg) = PySite.Start(name); if (ok) Ok(msg); else No(msg); }
    public void PySiteStop(string name)    { NeedInit(); PySite.Stop(name); Ok($"python-app {name} stopped"); }
    public void PySiteRestart(string name) { PySiteStop(name); System.Threading.Thread.Sleep(400); PySiteStart(name); }

    public (bool ok, string output) PySitePip(string name)
    {
        NeedInit();
        var (ok, output) = PySite.Pip(name);
        if (ok) Ok($"pip install complete for {name}"); else No($"pip install failed for {name}");
        return (ok, output);
    }

    public string PySiteLog(string name)     { NeedInit(); return PySite.LogTail(name); }
    public string PySiteEnvPath(string name) { NeedInit(); return PySite.EnvPath(name); }
    public string PySiteDir(string name)     { NeedInit(); return PySite.DirOf(name); }
    public PySiteConfig? PySiteConfig(string name) { NeedInit(); return PySite.Load(name); }

    public void PySiteEdit(string name, string cmd, int port, bool? venv)
    {
        NeedInit();
        var pc = PySite.Load(name) ?? throw new BhException($"no python-app '{name}'");
        var appCfg = Config.Load();
        var domain = $"{name}.{appCfg.Tld}";
        PySite.Stop(name);
        if (!string.IsNullOrWhiteSpace(cmd)) pc.Cmd = cmd;
        if (port > 0) pc.Port = port;
        if (venv is bool v) pc.Venv = v;
        PySite.Save(pc);
        PySite.RenderVhost(pc, domain, appCfg);
        if (Nginx.Running()) Nginx.Reload(appCfg);
        var (ok, msg) = PySite.Start(name); if (ok) Ok(msg); else Warn(msg);
        Ok($"python-app {name} updated");
    }

    public void PySiteRemove(string name)
    {
        NeedInit();
        PySite.Stop(name);
        PySite.Delete(name);
        try { File.Delete(Path.Combine(Paths.NginxSites, $"{name}.conf")); } catch { }
        var cfg = Config.Load();
        if (Nginx.Running()) Nginx.Reload(cfg);
        Ok($"removed python-app {name}");
    }

    public void PySiteList()
    {
        NeedInit();
        Hdr("Python-app sites");
        var any = false;
        foreach (var n in PySite.List())
        {
            var c = PySite.Load(n);
            Ok($"{n,-16} {(PySite.Running(n) ? "running" : "stopped"),-8} :{c?.Port}  {c?.Cmd}");
            any = true;
        }
        if (!any) Info("none — bhserve pysite add <name> --dir … --port … [--cmd …] [--venv yes]");
    }

    /// <summary>Best-effort: issue a trusted cert for the domain (mkcert) WITHOUT touching the vhost —
    /// PySite/NodeSite RenderVhost picks it up. No-op if mkcert isn't installed.</summary>
    private void TryIssueCert(string domain)
    {
        try
        {
            var mkc = Tools.MkcertExe();
            if (mkc is null) return;
            Directory.CreateDirectory(Paths.Certs);
            EnsureMkcertCa(mkc);
            RunCapture(mkc, $"-cert-file \"{domain}.pem\" -key-file \"{domain}-key.pem\" {domain}", Paths.Certs);
        }
        catch { }
    }

    /// <summary>Cloudflare quick tunnel — share a local site on a public https URL (no account).</summary>
    public void Tunnel(string sub, params string[] args)
    {
        NeedInit();
        var name = args.Length > 0 ? args[0] : "";
        switch (sub)
        {
            case "install":
                if (Tools.CloudflaredExe() is not null) { Ok("cloudflared already installed"); return; }
                Hdr("Installing cloudflared");
                Ok($"cloudflared installed: {Downloader.InstallCloudflared().GetAwaiter().GetResult()}");
                break;
            case "start":
            {
                if (name == "") throw new BhException("usage: bhserve tunnel start <site>");
                var conf = Path.Combine(Paths.NginxSites, $"{name}.conf");
                if (!File.Exists(conf)) throw new BhException($"no site '{name}'");
                if (!Nginx.Running()) throw new BhException("nginx not running — start it first (bhserve start nginx)");
                // First share auto-installs cloudflared — the user never has to run a command.
                if (Tools.CloudflaredExe() is null)
                {
                    Hdr("Setting up public sharing (one-time cloudflared download)");
                    Ok($"cloudflared installed: {Downloader.InstallCloudflared().GetAwaiter().GetResult()}");
                }
                var cfg = Config.Load();
                var domain = $"{name}.{cfg.Tld}";
                var origin = File.ReadAllText(conf).Contains($"listen 127.0.0.1:{cfg.HttpsPort} ssl")
                    ? $"https://127.0.0.1:{cfg.HttpsPort}" : $"http://127.0.0.1:{cfg.HttpPort}";
                Hdr($"Cloudflare tunnel → {domain}");
                var (ok, msg) = BHServe.Core.Tunnel.Start(name, domain, origin);
                if (ok) { Ok($"public URL: {msg}"); Info($"origin: {origin} (Host: {domain})"); }
                else No(msg);
                break;
            }
            case "stop":
                if (name == "") throw new BhException("usage: bhserve tunnel stop <site>");
                BHServe.Core.Tunnel.Stop(name); Ok($"tunnel stopped ({name})");
                break;
            case "url":
                if (name == "") throw new BhException("usage: bhserve tunnel url <site>");
                Out(BHServe.Core.Tunnel.Url(name) ?? "(no tunnel)");
                break;
            case "" or "list" or "status":
                Hdr("Tunnels");
                var any = false;
                foreach (var (n, u) in BHServe.Core.Tunnel.List()) { Ok($"{n,-16} {u}"); any = true; }
                if (!any) Info("none running — bhserve tunnel start <site>");
                break;
            default: throw new BhException("usage: bhserve tunnel {install|start <site>|stop <site>|url <site>|list}");
        }
    }

    /// <summary>Download phpMyAdmin and serve it at phpmyadmin.&lt;tld&gt; (connects to BHServe's MySQL).</summary>
    public void PhpMyAdmin()
    {
        NeedInit();
        var cfg = Config.Load();
        var root = Path.Combine(cfg.SitesRoot, "phpmyadmin");
        if (!File.Exists(Path.Combine(root, "index.php")))
        {
            Hdr("Downloading phpMyAdmin (latest, all-languages)");
            Downloader.InstallPhpMyAdmin(root).GetAwaiter().GetResult();
            Ok($"phpMyAdmin: {root}");
        }
        if (!DbServer.Running()) Warn("database not running — start it so phpMyAdmin can log in: bhserve start mariadb");
        SiteAdd("phpmyadmin", root: root, type: "others");
    }

    /// <summary>Download Adminer (single-file DB UI) and serve it at adminer.&lt;tld&gt;.</summary>
    public void Adminer()
    {
        NeedInit();
        var cfg = Config.Load();
        var root = Path.Combine(cfg.SitesRoot, "adminer");
        Directory.CreateDirectory(root);
        var index = Path.Combine(root, "index.php");
        if (!File.Exists(index))
        {
            Hdr("Downloading Adminer");
            Downloader.InstallAdminer(index).GetAwaiter().GetResult();
            Ok($"adminer: {index}");
        }
        SiteAdd("adminer", root: root, type: "others");
    }

    /// <summary>Install + run Mailpit and front its web UI at mailpit.&lt;tld&gt;.</summary>
    public void Mailpit()
    {
        NeedInit();
        var cfg = Config.Load();
        if (Tools.MailpitExe() is null)
        {
            Hdr("Installing Mailpit");
            Downloader.InstallMailpit().GetAwaiter().GetResult();
            Ok("mailpit installed");
        }
        if (BHServe.Core.MailpitServer.Start())
            Ok($"mailpit running — SMTP :{BHServe.Core.MailpitServer.SmtpPort}, UI :{BHServe.Core.MailpitServer.UiPort}");
        else { No("mailpit failed to start"); return; }

        var domain = $"mailpit.{cfg.Tld}";
        NginxConfig.RenderProxyVhost("mailpit", domain, BHServe.Core.MailpitServer.UiPort, cfg);
        Ok($"site vhost: mailpit.conf (proxy → :{BHServe.Core.MailpitServer.UiPort})");
        if (Nginx.Running()) Nginx.Reload(cfg);
        else { var (ok, msg) = Nginx.Start(cfg); if (ok) Ok(msg); else Warn(msg); }
        EnsureHosts(domain);

        Hdr("Mailpit ready");
        Info($"url    : http://{domain}   (or http://127.0.0.1:{BHServe.Core.MailpitServer.UiPort})");
        Info($"SMTP   : 127.0.0.1:{BHServe.Core.MailpitServer.SmtpPort}  — point php sendmail/SMTP here");
    }

    /// <summary>The built-in web tools — served like sites but listed separately (not user sites).</summary>
    public static readonly string[] ToolNames = { "phpmyadmin", "adminer", "mailpit" };
    public static bool IsTool(string name) => ToolNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Enable (install + serve over HTTPS) or disable a built-in web tool.</summary>
    public void ToolSet(string tool, bool on)
    {
        NeedInit();
        tool = (tool ?? "").ToLowerInvariant();
        if (!IsTool(tool)) throw new BhException($"unknown tool '{tool}'");
        var cfg = Config.Load();
        var domain = $"{tool}.{cfg.Tld}";
        if (on)
        {
            switch (tool)
            {
                case "phpmyadmin": PhpMyAdmin(); break;
                case "adminer":    Adminer();    break;
                case "mailpit":    Mailpit();    break;
            }
            // Tools default to HTTPS (mkcert). If that can't be set up, the http vhost still works.
            try { Secure(domain); } catch (Exception ex) { Warn($"https not enabled for {tool}: {ex.Message} (served over http)"); }
        }
        else
        {
            if (tool == "mailpit" && MailpitServer.Running()) { MailpitServer.Stop(); Ok("mailpit stopped"); }
            SiteRemove(tool);   // unmap the vhost/hosts; keep the downloaded files for a fast re-enable
        }
    }
}

/// <summary>A clean, user-facing error (the CLI prints the message; no stack trace).</summary>
public sealed class BhException(string message) : Exception(message);
