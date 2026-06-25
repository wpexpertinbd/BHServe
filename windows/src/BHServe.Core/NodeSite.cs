using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace BHServe.Core;

/// <summary>One supervised process (frontend or backend) of a Node-app site.</summary>
public sealed class NodeProc
{
    public string Dir { get; set; } = "";
    public string Cmd { get; set; } = "";   // e.g. "npm run dev"
    public int Port { get; set; }
}

/// <summary>A Node-app site: a frontend (and optional backend) reverse-proxied behind nginx.</summary>
public sealed class NodeSiteConfig
{
    public string Name { get; set; } = "";
    public NodeProc Frontend { get; set; } = new();
    public NodeProc? Backend { get; set; }
    public string ApiPath { get; set; } = "/api";
}

/// <summary>
/// Node-app site type — the analog of the mac <c>nodesite</c> engine. Supervises a
/// frontend (+ optional backend) Node process and fronts them with an nginx
/// reverse-proxy vhost (<c>/api</c> → backend, everything else → frontend). Config
/// lives at <c>node-sites\&lt;name&gt;.json</c>.
/// </summary>
public static class NodeSite
{
    private static string Dir => Path.Combine(Paths.Home, "node-sites");
    private static string ConfPath(string name) => Path.Combine(Dir, $"{name}.json");
    private static string RunFile(string name, string which) => Path.Combine(Paths.Run, $"nodesite-{name}-{which}.json");
    private static string LogFile(string name, string which) => Path.Combine(Paths.Logs, $"nodesite-{name}-{which}.log");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static IReadOnlyList<string> List() =>
        Directory.Exists(Dir)
            ? Directory.EnumerateFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null).Cast<string>().OrderBy(n => n).ToList()
            : Array.Empty<string>();

    public static NodeSiteConfig? Load(string name)
    {
        try { return File.Exists(ConfPath(name)) ? JsonSerializer.Deserialize<NodeSiteConfig>(File.ReadAllText(ConfPath(name)), Opts) : null; }
        catch { return null; }
    }

    public static void Save(NodeSiteConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfPath(cfg.Name), JsonSerializer.Serialize(cfg, Opts));
    }

    public static void Delete(string name)
    {
        try { File.Delete(ConfPath(name)); } catch { }
    }

    private static bool PortOpen(int port)
    {
        try { using var c = new TcpClient(); return c.ConnectAsync("127.0.0.1", port).Wait(400) && c.Connected; }
        catch { return false; }
    }

    public static bool Running(string name)
    {
        var cfg = Load(name);
        return cfg is not null && PortOpen(cfg.Frontend.Port);
    }

    /// <summary>Spawn a process (cmd run via cmd.exe with the fnm node on PATH + PORT set).</summary>
    private static bool StartProc(string name, string which, NodeProc p)
    {
        if (PortOpen(p.Port)) return true;
        if (string.IsNullOrWhiteSpace(p.Cmd) || !Directory.Exists(p.Dir)) return false;

        var log = LogFile(name, which);
        Directory.CreateDirectory(Paths.Logs);
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{p.Cmd} > \"{log}\" 2>&1\"",
            WorkingDirectory = p.Dir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var nodeBin = Tools.NodeBinDir();
        if (nodeBin is not null)
            psi.Environment["PATH"] = nodeBin + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["PORT"] = p.Port.ToString();

        var proc = Process.Start(psi);
        if (proc is null) return false;
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(RunFile(name, which), JsonSerializer.Serialize(new { pid = proc.Id, port = p.Port }));
        return true;
    }

    private static void StopProc(string name, string which)
    {
        var f = RunFile(name, which);
        try
        {
            if (File.Exists(f))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                Process.GetProcessById(doc.RootElement.GetProperty("pid").GetInt32()).Kill(true);
            }
        }
        catch { }
        try { File.Delete(f); } catch { }
    }

    public static (bool ok, string msg) Start(string name)
    {
        var cfg = Load(name);
        if (cfg is null) return (false, $"no node-app '{name}'");
        var f = StartProc(name, "frontend", cfg.Frontend);
        var b = cfg.Backend is null || StartProc(name, "backend", cfg.Backend);
        // give them a moment to bind
        for (var i = 0; i < 16 && !PortOpen(cfg.Frontend.Port); i++) System.Threading.Thread.Sleep(500);
        return (f && b)
            ? (true, $"node-app {name} starting (frontend :{cfg.Frontend.Port}{(cfg.Backend is not null ? $", backend :{cfg.Backend.Port}" : "")})")
            : (false, $"failed to start (check logs\\nodesite-{name}-*.log)");
    }

    public static void Stop(string name)
    {
        StopProc(name, "frontend");
        StopProc(name, "backend");
    }

    /// <summary>Render the nginx reverse-proxy vhost for a Node-app site.</summary>
    public static void RenderVhost(NodeSiteConfig cfg, string domain, Config appCfg)
    {
        var home = NginxConfig.Fwd(Paths.Home);
        var apiBlock = "";
        if (cfg.Backend is not null)
        {
            var api = cfg.ApiPath.Trim('/');
            apiBlock = $$"""

                location /{{api}}/ {
                    proxy_pass http://127.0.0.1:{{cfg.Backend.Port}};
                    proxy_set_header Host $host;
                    proxy_http_version 1.1;
                    proxy_set_header Upgrade $http_upgrade;
                    proxy_set_header Connection "upgrade";
                    proxy_read_timeout 600;
                }
        """;
        }
        var listen = $"    listen 127.0.0.1:{appCfg.HttpPort};";
        var cert = Path.Combine(Paths.Certs, $"{domain}.pem");
        var key  = Path.Combine(Paths.Certs, $"{domain}-key.pem");
        if (File.Exists(cert) && File.Exists(key))
            listen += $"\n    listen 127.0.0.1:{appCfg.HttpsPort} ssl;\n    ssl_certificate {NginxConfig.Fwd(cert)};\n    ssl_certificate_key {NginxConfig.Fwd(key)};";

        var body = $$"""
        # BHServe site: {{cfg.Name}}  ({{domain}})  php=- server=node
        server {
        {{listen}}
            server_name {{domain}};
            access_log {{home}}/logs/{{cfg.Name}}-access.log;
            error_log  {{home}}/logs/{{cfg.Name}}-error.log;
        {{apiBlock}}
            location / {
                proxy_pass http://127.0.0.1:{{cfg.Frontend.Port}};
                proxy_set_header Host $host;
                proxy_http_version 1.1;
                proxy_set_header Upgrade $http_upgrade;
                proxy_set_header Connection "upgrade";
                proxy_read_timeout 600;
            }
        }

        """;
        Directory.CreateDirectory(Paths.NginxSites);
        File.WriteAllText(Path.Combine(Paths.NginxSites, $"{cfg.Name}.conf"), body);
    }
}
