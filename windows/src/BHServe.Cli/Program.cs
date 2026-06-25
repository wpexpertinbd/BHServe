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
        case "init":     engine.Init(); Console.WriteLine($"initialized at {Paths.Home}"); break;
        case "install":  engine.Install(Arg(rest, 0)); break;
        case "update":   engine.Update(Arg(rest, 0)); break;
        case "uninstall":engine.Uninstall(Arg(rest, 0)); break;
        case "start":    engine.Start(rest.FirstOrDefault() ?? "all"); break;
        case "stop":     engine.Stop(rest.FirstOrDefault() ?? "all"); break;
        case "restart":  engine.Restart(rest.FirstOrDefault() ?? "all"); break;
        case "enable":   engine.Enable(Arg(rest, 0)); break;
        case "disable":  engine.Disable(Arg(rest, 0)); break;
        case "secure":   engine.Secure(Arg(rest, 0)); break;
        case "api":      Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(engine.Api())); break;

        case "site":
            switch (Arg(rest, 0))
            {
                case "add": engine.SiteAdd(Arg(rest, 1)); break;
                case "rm":  engine.SiteRemove(Arg(rest, 1)); break;
                case "php": engine.SitePhp(Arg(rest, 1), Arg(rest, 2)); break;
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

        case "db":   engine.Db(Arg(rest, 0), rest.Skip(1).ToArray()); break;
        case "node": engine.Node(Arg(rest, 0), rest.Skip(1).ToArray()); break;

        case "help" or "-h" or "--help": Usage(); break;
        default: Usage(); return 1;
    }
    return 0;
}
catch (NotImplementedException)
{
    Console.Error.WriteLine($"[stub] '{cmd}' not implemented yet — see docs/WINDOWS-PORT.md");
    return 2;
}

static string Arg(string[] a, int i) => i < a.Length ? a[i] : "";

static void Usage() => Console.WriteLine("""
    BHServe (Windows) — usage:
      bhserve init
      bhserve install|update|uninstall <tool>
      bhserve start|stop|restart [svc|all]
      bhserve enable|disable <svc>
      bhserve site add|rm|php <name> [args]
      bhserve secure <domain>
      bhserve php ini path|reload <ver>
      bhserve db|node <sub> [args]
      bhserve api | status
    """);
