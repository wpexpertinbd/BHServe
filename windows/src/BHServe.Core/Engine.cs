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
        if (string.IsNullOrEmpty(tool)) throw new BhException("usage: bhserve install <nginx|php@8.4|mkcert>");
        var cfg = Config.Load();
        try
        {
            if (tool == "nginx")
            {
                if (Services.Installed("nginx", cfg)) { Ok("nginx already installed"); return; }
                Hdr("Installing nginx (portable zip from nginx.org)");
                var exe = Downloader.InstallNginx().GetAwaiter().GetResult();
                Ok($"nginx installed: {exe}");
            }
            else if (tool == "mkcert")
            {
                if (Services.Installed("mkcert", cfg)) { Ok("mkcert already installed"); return; }
                Hdr("Installing mkcert (GitHub release)");
                Ok($"mkcert installed: {Downloader.InstallMkcert().GetAwaiter().GetResult()}");
            }
            else if (tool.StartsWith("php"))
            {
                var verArg = tool == "php" ? "default" : tool[(tool.IndexOf('@') + 1)..];
                var ver = Services.PhpVersion(Services.PhpKey(verArg, cfg), cfg);
                if (Tools.PhpCgiExe(ver) is not null) { Ok($"php {ver} already installed"); return; }
                Hdr($"Installing PHP {ver} (NTS x64 from windows.php.net)");
                Ok($"php {ver} installed: {Downloader.InstallPhp(ver).GetAwaiter().GetResult()}");
            }
            else throw new BhException($"unknown tool: {tool}");
        }
        catch (BhException) { throw; }
        catch (Exception ex) { No($"install {tool} failed: {ex.Message}"); }
    }

    public void Update(string tool)    => throw new BhException("update: not yet on Windows (reinstall the tool for now)");
    public void Uninstall(string tool) => throw new BhException("uninstall: not yet on Windows");

    // ── start/stop ──────────────────────────────────────────────────────────────
    /// <summary>"all" → start nginx + every PHP version an enabled site uses (the 502 fix).</summary>
    public void Start(string svc)
    {
        NeedInit();
        var cfg = Config.Load();
        if (svc is "all" or "")
        {
            foreach (var v in SitePhpVersions(cfg))
                if (PhpCgi.Start(v)) Ok($"php-cgi {v} on :{PhpCgi.PortFor(v)}");
                else Warn($"php {v} not installed — bhserve install php@{v}");
            if (Services.Enabled("mariadb", cfg) && Tools.MysqldExe() is not null)
            {
                var (dok, dmsg) = DbServer.Start(); if (dok) Ok(dmsg); else Warn(dmsg);
            }
            var (ok, msg) = Nginx.Start(cfg);
            if (ok) Ok(msg); else No(msg);
            return;
        }
        if (svc == "nginx") { var (ok, msg) = Nginx.Start(cfg); if (ok) Ok(msg); else No(msg); return; }
        if (svc == "mariadb") { var (ok, msg) = DbServer.Start(); if (ok) Ok(msg); else No(msg); return; }
        if (svc == "mailpit") { if (MailpitServer.Start()) Ok($"mailpit on UI :{MailpitServer.UiPort} / SMTP :{MailpitServer.SmtpPort}"); else No("mailpit not installed — bhserve mailpit"); return; }
        if (svc.StartsWith("php"))
        {
            var v = PhpVersionOf(svc, cfg);
            if (PhpCgi.Start(v)) Ok($"php-cgi {v} on :{PhpCgi.PortFor(v)}"); else No($"php {v} not installed");
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
            if (MailpitServer.Running()) { MailpitServer.Stop(); Ok("mailpit stopped"); }
            return;
        }
        if (svc == "nginx") { Nginx.Stop(); Ok("nginx stopped"); return; }
        if (svc == "mariadb") { DbServer.Stop(); Ok("database stopped"); return; }
        if (svc == "mailpit") { MailpitServer.Stop(); Ok("mailpit stopped"); return; }
        if (svc.StartsWith("php")) { var v = PhpVersionOf(svc, cfg); PhpCgi.Stop(v); Ok($"php-cgi {v} stopped"); return; }
        throw new BhException($"don't know how to stop '{svc}'");
    }

    public void Restart(string svc) { Stop(svc); Start(svc); }
    public void Enable(string svc)  { NeedInit(); Services.Enable(svc, Config.Load()); Ok($"{svc} will auto-start"); }
    public void Disable(string svc) { NeedInit(); Services.Disable(svc, Config.Load()); Ok($"{svc} won't auto-start"); }

    // ── sites ─────────────────────────────────────────────────────────────────────
    public void SiteAdd(string name, string php = "", string? root = null, string server = "", string type = "others")
    {
        NeedInit();
        if (string.IsNullOrWhiteSpace(name)) throw new BhException("usage: bhserve site add <name> [--php 8.4] [--root path]");
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$"))
            throw new BhException($"invalid site name '{name}' (lowercase letters, digits, hyphens)");

        var cfg = Config.Load();
        var domain = $"{name}.{cfg.Tld}";
        root ??= Path.Combine(cfg.SitesRoot, name);
        if (string.IsNullOrEmpty(server)) server = cfg.DefaultWeb;
        if (server != "nginx") throw new BhException("only --server nginx is implemented on Windows so far");
        var phpKey = Services.PhpKey(php, cfg);
        var version = Services.PhpVersion(phpKey, cfg);

        Directory.CreateDirectory(root);
        if (!File.Exists(Path.Combine(root, "index.php")) && !File.Exists(Path.Combine(root, "index.html")))
            File.WriteAllText(Path.Combine(root, "index.php"),
                $"<?php // BHServe placeholder for {domain}\n" +
                $"echo \"<h1>{domain}</h1><p>BHServe is serving this site on PHP \" . PHP_VERSION . \".</p>\";\n" +
                "phpinfo(INFO_GENERAL);\n");

        if (PhpCgi.Start(version)) Ok($"php-cgi {version} on :{PhpCgi.PortFor(version)}");
        else Warn($"php {version} not installed — bhserve install php@{version} (site will 502 until then)");

        NginxConfig.RenderPhpVhost(name, domain, root, phpKey, cfg);
        Ok($"site vhost: {Path.Combine(Paths.NginxSites, name + ".conf")}");

        // Serve it first (so the site works immediately), then map the hostname.
        if (Nginx.Running()) Nginx.Reload(cfg);
        else { var (ok, msg) = Nginx.Start(cfg); if (ok) Ok(msg); else Warn(msg); }

        EnsureHosts(domain);

        Hdr($"Site '{name}' added");
        Info($"url    : http://{domain}");
        Info($"root   : {root}");
        Info($"php    : {phpKey}   server: {server}   type: {type}");
    }

    public void SiteRemove(string name)
    {
        NeedInit();
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$")) throw new BhException($"invalid site name '{name}'");
        var cfg = Config.Load();
        foreach (var f in new[] { $"{name}.conf", $"{name}.conf.disabled" })
            try { File.Delete(Path.Combine(Paths.NginxSites, f)); } catch { }
        Ok($"removed vhost for {name}");
        var rmDomain = $"{name}.{cfg.Tld}";
        if (!Hosts.Remove(rmDomain) && Hosts.Has(rmDomain)) Elevation.Run("hosts-remove", rmDomain);
        if (Nginx.Running()) Nginx.Reload(cfg);
        Info("(site files left on disk; remove manually if desired)");
    }

    public void SitePhp(string name, string version)
    {
        NeedInit();
        var cfg = Config.Load();
        var conf = Path.Combine(Paths.NginxSites, $"{name}.conf");
        if (!File.Exists(conf)) throw new BhException($"no such site: {name}");
        var (domain, root, _) = ParseVhost(conf);
        var phpKey = Services.PhpKey(version, cfg);
        var v = Services.PhpVersion(phpKey, cfg);
        PhpCgi.Start(v);
        NginxConfig.RenderPhpVhost(name, domain, root, phpKey, cfg);
        if (Nginx.Running()) Nginx.Reload(cfg);
        Ok($"{name} now on {phpKey}");
    }

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

        var name = domain.Split('.')[0];
        var conf = Path.Combine(Paths.NginxSites, $"{name}.conf");
        if (File.Exists(conf))
        {
            var cfg = Config.Load();
            var (dom, root, phpKey) = ParseVhost(conf);
            NginxConfig.RenderPhpVhost(name, dom, root, phpKey, cfg);
            if (Nginx.Running()) Nginx.Reload(cfg);
            Ok("re-rendered vhost with HTTPS");
        }
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
                ServiceRole.Web => s.Key == "nginx" && Nginx.Running(),
                ServiceRole.Php => PhpCgi.Running(Services.PhpVersion(s.Key, cfg)),
                ServiceRole.Db   => s.Key == "mariadb" && DbServer.Running(),
                ServiceRole.Mail => s.Key == "mailpit" && MailpitServer.Running(),
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
        if (Elevation.Run("hosts-add", domain)) Ok($"hosts: {domain} → 127.0.0.1 (elevated)");
        else Warn($"hosts not updated — add manually: 127.0.0.1 {domain}");
    }

    /// <summary>Ensure mkcert's local CA exists + is trusted (first run needs admin/UAC).</summary>
    private void EnsureMkcertCa(string mkc)
    {
        var (_, caroot) = RunCapture(mkc, "-CAROOT", null);
        caroot = caroot.Trim();
        var rootPem = string.IsNullOrEmpty(caroot) ? null : Path.Combine(caroot, "rootCA.pem");
        if (rootPem is not null && File.Exists(rootPem)) return;   // CA already present/trusted

        Info("installing mkcert local CA (one-time, needs admin)…");
        if (Elevation.Run("mkcert-install")) Ok("mkcert CA installed (browsers will trust BHServe certs)");
        else Warn("mkcert CA not installed in trust store — certs work but show untrusted (curl -k is fine)");
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
        foreach (var f in Directory.EnumerateFiles(Paths.NginxSites, "*.conf"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var text = File.ReadAllText(f);
            var domain = Regex.Match(text, @"server_name\s+([^;]+);").Groups[1].Value.Trim();
            var php    = Regex.Match(text, @"php=(\S+)").Groups[1].Value.Trim();
            var root   = Regex.Match(text, @"(?m)^\s*root\s+([^;]+);").Groups[1].Value.Trim();
            var secure = text.Contains("ssl_certificate ");
            list.Add(new Site(name, domain, php, root, secure, true,
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
                if (name == "") throw new BhException("usage: bhserve db create <name>");
                Ok($"database '{Database.Create(name)}' ready  (root · no password · 127.0.0.1:{DbServer.Port})");
                break;
            case "drop":
                if (name == "") throw new BhException("usage: bhserve db drop <name>");
                Database.Drop(name); Ok($"dropped database '{name}'");
                break;
            default: throw new BhException("usage: bhserve db {list|create|drop} [name]");
        }
    }

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
}

/// <summary>A clean, user-facing error (the CLI prints the message; no stack trace).</summary>
public sealed class BhException(string message) : Exception(message);
