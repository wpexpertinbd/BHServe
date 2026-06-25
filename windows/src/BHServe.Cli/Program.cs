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
                case "rm" or "remove":     engine.SiteRemove(Arg(rest, 1)); break;
                case "php":                engine.SitePhp(Arg(rest, 1), Arg(rest, 2)); break;
                case "list" or "ls" or "": engine.Status(); break;
                default: Usage(); return 1;
            }
            break;

        case "php":
            switch (Arg(rest, 0))
            {
                case "ini" when Arg(rest, 1) == "path":   Console.WriteLine(engine.PhpIniPath(Arg(rest, 2))); break;
                case "ini" when Arg(rest, 1) == "reload": engine.PhpIniReload(Arg(rest, 2)); break;
                default: Usage(); return 1;
            }
            break;

        case "db":      engine.Db(Arg(rest, 0), rest.Skip(1).ToArray()); break;
        case "node":    engine.Node(Arg(rest, 0), rest.Skip(1).ToArray()); break;
        case "adminer": engine.Adminer(); break;
        case "mailpit": engine.Mailpit(); break;

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
      bhserve init
      bhserve install <nginx|php@8.4|mkcert>
      bhserve start|stop|restart [svc|all]
      bhserve enable|disable <svc>
      bhserve site add <name> [--php 8.4] [--root path] [--type wordpress|php|others]
      bhserve site rm|php|list <name> [args]
      bhserve secure <domain>
      bhserve php ini path|reload <ver>
      bhserve status | api
    """);
