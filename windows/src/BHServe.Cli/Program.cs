using BHServe.Core;

// Transparent CLI over BHServe.Core — the Windows analog of `engine/bhserve`.
// Usage mirrors the mac verbs so muscle memory + docs carry across.

var engine = new Engine();
var cmd = args.Length > 0 ? args[0] : "status";
var rest = args.Skip(1).ToArray();

try
{
    switch (cmd)
    {
        case "init":      engine.Init(); break;
        case "install":   engine.Install(Arg(rest, 0)); break;
        case "update":    engine.Update(Arg(rest, 0)); break;
        case "uninstall": engine.Uninstall(Arg(rest, 0)); break;
        case "start":     engine.Start(rest.FirstOrDefault() ?? "all"); break;
        case "stop":      engine.Stop(rest.FirstOrDefault() ?? "all"); break;
        case "restart":   engine.Restart(rest.FirstOrDefault() ?? "all"); break;
        case "enable":    engine.Enable(Arg(rest, 0)); break;
        case "disable":   engine.Disable(Arg(rest, 0)); break;
        case "secure":    engine.Secure(Arg(rest, 0)); break;
        case "status":    engine.Status(); break;
        case "api":       Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(engine.Api())); break;

        case "site":
            switch (Arg(rest, 0))
            {
                case "add":
                {
                    var name = Arg(rest, 1);
                    var f = Flags(rest.Skip(2));
                    engine.SiteAdd(name,
                        php:    f.GetValueOrDefault("php", ""),
                        root:   f.GetValueOrDefault("root"),
                        server: f.GetValueOrDefault("server", ""),
                        type:   f.GetValueOrDefault("type", "others"));
                    break;
                }
                case "rm" or "remove":
                {
                    var purge = rest.Any(a => a is "--purge" or "--delete-files" or "--all");
                    engine.SiteRemove(Arg(rest, 1), purgeFiles: purge, dropDb: purge);
                    break;
                }
                case "php":                engine.SitePhp(Arg(rest, 1), Arg(rest, 2)); break;
                case "server":             engine.SiteServer(Arg(rest, 1), Arg(rest, 2)); break;
                case "enable":             engine.SiteEnable(Arg(rest, 1), true); break;
                case "disable":            engine.SiteEnable(Arg(rest, 1), false); break;
                case "root":               engine.SiteRoot(Arg(rest, 1), Arg(rest, 2)); break;
                case "list" or "ls" or "": engine.Status(); break;
                default: Usage(); return 1;
            }
            break;

        case "php":
            switch (Arg(rest, 0))
            {
                case "ini" when Arg(rest, 1) == "path":   Console.WriteLine(engine.PhpIniPath(Arg(rest, 2))); break;
                case "ini" when Arg(rest, 1) == "reload": engine.PhpIniReload(Arg(rest, 2)); break;
                case "ioncube":                           engine.PhpIoncube(Arg(rest, 1)); break;
                case "status" or "":                      engine.PhpStatus(); break;
                default: Usage(); return 1;
            }
            break;

        case "db":      engine.Db(Arg(rest, 0), rest.Skip(1).ToArray()); break;
        case "pg":      engine.Pg(Arg(rest, 0), rest.Skip(1).ToArray()); break;
        case "node":    engine.Node(Arg(rest, 0), rest.Skip(1).ToArray()); break;
        case "nodesite":
            switch (Arg(rest, 0))
            {
                case "add":
                {
                    var f = Flags(rest.Skip(2));
                    engine.NodeSiteAdd(Arg(rest, 1),
                        f.GetValueOrDefault("fe-dir", ""), f.GetValueOrDefault("fe-cmd", ""),
                        int.TryParse(f.GetValueOrDefault("fe-port"), out var fp) ? fp : 0,
                        f.GetValueOrDefault("be-dir"), f.GetValueOrDefault("be-cmd"),
                        int.TryParse(f.GetValueOrDefault("be-port"), out var bp) ? bp : 0,
                        f.GetValueOrDefault("api", "/api"));
                    break;
                }
                case "start":          engine.NodeSiteStart(Arg(rest, 1)); break;
                case "stop":           engine.NodeSiteStop(Arg(rest, 1)); break;
                case "restart":        engine.NodeSiteRestart(Arg(rest, 1)); break;
                case "rm" or "remove": engine.NodeSiteRemove(Arg(rest, 1)); break;
                case "npm":
                {
                    var which = Arg(rest, 2); if (which.Length == 0) which = "frontend";
                    var (_, o) = engine.NodeSiteNpm(Arg(rest, 1), which);
                    if (o.Length > 0) Console.WriteLine(o);
                    break;
                }
                case "list" or "":     engine.NodeSiteList(); break;
                default: Usage(); return 1;
            }
            break;
        case "pysite":
            switch (Arg(rest, 0))
            {
                case "add":
                {
                    var f = Flags(rest.Skip(2));
                    var venv = f.GetValueOrDefault("venv", "yes");
                    engine.PySiteAdd(Arg(rest, 1),
                        f.GetValueOrDefault("dir", ""),
                        f.GetValueOrDefault("cmd", ""),
                        int.TryParse(f.GetValueOrDefault("port"), out var pp) ? pp : 0,
                        venv is not ("no" or "false" or "0"));
                    break;
                }
                case "start":          engine.PySiteStart(Arg(rest, 1)); break;
                case "stop":           engine.PySiteStop(Arg(rest, 1)); break;
                case "restart":        engine.PySiteRestart(Arg(rest, 1)); break;
                case "rm" or "remove": engine.PySiteRemove(Arg(rest, 1)); break;
                case "pip":
                {
                    var (_, o) = engine.PySitePip(Arg(rest, 1));
                    if (o.Length > 0) Console.WriteLine(o);
                    break;
                }
                case "list" or "":     engine.PySiteList(); break;
                default: Usage(); return 1;
            }
            break;
        case "doctor":  engine.Doctor(); break;
        case "logs":    engine.Logs(Arg(rest, 0), int.TryParse(Arg(rest, 1), out var ln) ? ln : 200); break;
        case "config":
            switch (Arg(rest, 0))
            {
                case "set":               engine.ConfigSet(Arg(rest, 1), Arg(rest, 2)); break;
                case "show" or "" or null: engine.ConfigShow(); break;
                default: Usage(); return 1;
            }
            break;
        case "adminer": engine.Adminer(); break;
        case "pma" or "phpmyadmin": engine.PhpMyAdmin(); break;
        case "mailpit": engine.Mailpit(); break;
        case "tunnel":  engine.Tunnel(Arg(rest, 0), rest.Skip(1).ToArray()); break;

        case "help" or "-h" or "--help": Usage(); break;
        default: Usage(); return 1;
    }
    return 0;
}
catch (BhException ex)
{
    Console.Error.WriteLine($"  ✗ {ex.Message}");
    return 1;
}
catch (NotImplementedException)
{
    Console.Error.WriteLine($"[stub] '{cmd}' not implemented yet — see docs/WINDOWS-PORT.md");
    return 2;
}

static string Arg(string[] a, int i) => i < a.Length ? a[i] : "";

// Parse "--php 8.4 --root C:\x" → { php:8.4, root:C:\x }
static Dictionary<string, string> Flags(IEnumerable<string> a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var arr = a.ToArray();
    for (var i = 0; i < arr.Length; i++)
    {
        if (!arr[i].StartsWith("--")) continue;
        var key = arr[i][2..];
        var val = (i + 1 < arr.Length && !arr[i + 1].StartsWith("--")) ? arr[++i] : "true";
        d[key] = val;
    }
    return d;
}

static void Usage() => Console.WriteLine("""
    BHServe (Windows) — usage:
      bhserve init | doctor | status | api
      bhserve install <nginx|php@8.4|mkcert>
      bhserve start|stop|restart [svc|all]      (svc: nginx|mariadb|mailpit|php@X)
      bhserve enable|disable <svc>
      bhserve site add <name> [--php 8.4] [--root path] [--server nginx|apache] [--type wordpress|php|others]
      bhserve site rm|list <name>
      bhserve site php <name> <ver> | site server <name> <nginx|apache>
      bhserve secure <domain>
      bhserve db {list|create|drop} [name]
      bhserve node {list|install|use|uninstall} [version]
      bhserve php {ioncube <ver>|status|ini path|reload <ver>}
      bhserve pma | adminer | mailpit            (DB UIs + mail catcher)
      bhserve tunnel {install|start|stop|url|list} [site]
      bhserve logs [--list | <file> [lines]]
      bhserve config {show | set <key> <value>}
    """);
