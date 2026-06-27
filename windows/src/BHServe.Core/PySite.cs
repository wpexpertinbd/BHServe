using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace BHServe.Core;

/// <summary>A Python web-app site: ONE supervised process (Flask / Django / FastAPI / Gunicorn /
/// Uvicorn) reverse-proxied behind nginx, with an optional per-project virtualenv. The analog of the
/// mac <c>pysite</c> engine — and the single-process sibling of <see cref="NodeSite"/>. Config lives
/// at <c>py-sites\&lt;name&gt;.json</c>.</summary>
public sealed class PySiteConfig
{
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Cmd { get; set; } = "python app.py";   // runs with $PORT exported
    public int Port { get; set; }
    public bool Venv { get; set; } = true;
    public string PyVer { get; set; } = "";
}

public static class PySite
{
    private static string Dir => Path.Combine(Paths.Home, "py-sites");
    private static string ConfPath(string name) => Path.Combine(Dir, $"{name}.json");
    private static string RunFile(string name) => Path.Combine(Paths.Run, $"pysite-{name}.json");
    private static string LogFile(string name) => Path.Combine(Paths.Logs, $"pysite-{name}.log");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static IReadOnlyList<string> List() =>
        Directory.Exists(Dir)
            ? Directory.EnumerateFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null).Cast<string>().OrderBy(n => n).ToList()
            : Array.Empty<string>();

    public static PySiteConfig? Load(string name)
    {
        try { return File.Exists(ConfPath(name)) ? JsonSerializer.Deserialize<PySiteConfig>(File.ReadAllText(ConfPath(name)), Opts) : null; }
        catch { return null; }
    }

    public static void Save(PySiteConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfPath(cfg.Name), JsonSerializer.Serialize(cfg, Opts));
    }

    public static void Delete(string name) { try { File.Delete(ConfPath(name)); } catch { } }

    private static bool PortOpen(int port)
    {
        try { using var c = new TcpClient(); return c.ConnectAsync("127.0.0.1", port).Wait(400) && c.Connected; }
        catch { return false; }
    }

    public static bool Running(string name) { var cfg = Load(name); return cfg is not null && PortOpen(cfg.Port); }
    public static int PortOf(string name) => Load(name)?.Port ?? 0;
    public static string DirOf(string name) => Load(name)?.Dir ?? "";

    /// <summary>The venv's executables dir on Windows is <c>.venv\Scripts</c> (not <c>bin</c>).</summary>
    public static string? VenvBin(string dir)
    {
        var b = Path.Combine(dir, ".venv", "Scripts");
        return Directory.Exists(b) ? b : null;
    }

    /// <summary>Create a virtualenv (.venv) in the project dir using the managed python.</summary>
    public static (bool ok, string output) MakeVenv(string dir)
    {
        var py = Tools.PythonExe();
        if (py is null) return (false, "python not installed — bhserve install python");
        var venv = Path.Combine(dir, ".venv");
        if (Directory.Exists(Path.Combine(venv, "Scripts"))) return (true, "venv already exists");
        var psi = NewPsi($"\"{py}\" -m venv \"{venv}\"", dir, capture: true);
        try { var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit(); return (p.ExitCode == 0, (o + e).Trim()); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static ProcessStartInfo NewPsi(string innerCmd, string workDir, bool capture) => new()
    {
        FileName = "cmd.exe",
        Arguments = $"/c \"{innerCmd}\"",
        WorkingDirectory = workDir,
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = capture, RedirectStandardError = capture,
    };

    /// <summary>Put the venv's Scripts (then the managed python's dir) first on PATH, and export PORT +
    /// PYTHONUNBUFFERED so gunicorn/uvicorn/flask resolve and logs are live.</summary>
    private static void SetEnv(ProcessStartInfo psi, PySiteConfig cfg)
    {
        var parts = new List<string>();
        if (cfg.Venv && VenvBin(cfg.Dir) is { } vb) parts.Add(vb);
        if (Tools.PythonBinDir() is { } pb) parts.Add(pb);
        parts.Add(Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["PATH"] = string.Join(";", parts);
        psi.Environment["PORT"] = cfg.Port.ToString();
        psi.Environment["PYTHONUNBUFFERED"] = "1";
    }

    public static (bool ok, string msg) Start(string name)
    {
        var cfg = Load(name);
        if (cfg is null) return (false, $"no python-app '{name}'");
        if (PortOpen(cfg.Port)) return (true, $"python-app {name} already running on :{cfg.Port}");
        if (string.IsNullOrWhiteSpace(cfg.Cmd) || !Directory.Exists(cfg.Dir)) return (false, "missing run command or directory");

        Directory.CreateDirectory(Paths.Logs);
        var log = LogFile(name);
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cfg.Cmd} > \"{log}\" 2>&1\"",
            WorkingDirectory = cfg.Dir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        SetEnv(psi, cfg);
        var proc = Process.Start(psi);
        if (proc is null) return (false, "failed to spawn the process");
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(RunFile(name), JsonSerializer.Serialize(new { pid = proc.Id, port = cfg.Port }));
        for (var i = 0; i < 16 && !PortOpen(cfg.Port); i++) System.Threading.Thread.Sleep(500);
        return (true, $"python-app {name} starting on :{cfg.Port}");
    }

    public static void Stop(string name)
    {
        var f = RunFile(name);
        // kill the whole tree — gunicorn/uvicorn/django spawn worker children
        try { if (File.Exists(f)) { using var doc = JsonDocument.Parse(File.ReadAllText(f)); Process.GetProcessById(doc.RootElement.GetProperty("pid").GetInt32()).Kill(true); } } catch { }
        try { File.Delete(f); } catch { }
    }

    public static string EnvPath(string name) { var d = DirOf(name); return d.Length == 0 ? "" : Path.Combine(d, ".env"); }

    public static string LogTail(string name, int maxBytes = 40000)
    {
        try
        {
            var f = LogFile(name);
            if (!File.Exists(f)) return "";
            var bytes = File.ReadAllBytes(f);
            var start = Math.Max(0, bytes.Length - maxBytes);
            return System.Text.Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }
        catch { return ""; }
    }

    /// <summary>`pip install -r requirements.txt` into the project venv (or `python -m pip` if no venv).
    /// Falls back to upgrading pip when there's no requirements.txt. Blocks; returns output.</summary>
    public static (bool ok, string output) Pip(string name)
    {
        var cfg = Load(name);
        if (cfg is null || !Directory.Exists(cfg.Dir)) return (false, "no python-app directory");
        var hasReq = File.Exists(Path.Combine(cfg.Dir, "requirements.txt"));
        var target = hasReq ? "-r requirements.txt" : "--upgrade pip";
        var pip = cfg.Venv && VenvBin(cfg.Dir) is { } vb ? Path.Combine(vb, "pip.exe") : null;
        var inner = pip is not null ? $"\"{pip}\" install {target}" : $"python -m pip install {target}";
        var psi = NewPsi(inner, cfg.Dir, capture: true);
        SetEnv(psi, cfg);
        try { var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit(); return (p.ExitCode == 0, (o + e).Trim()); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Render the nginx reverse-proxy vhost (labeled <c>server=python</c>) for a Python-app site.</summary>
    public static void RenderVhost(PySiteConfig cfg, string domain, Config appCfg)
    {
        var home = NginxConfig.Fwd(Paths.Home);
        var listen = $"    listen 127.0.0.1:{appCfg.HttpPort};";
        var cert = Path.Combine(Paths.Certs, $"{domain}.pem");
        var key  = Path.Combine(Paths.Certs, $"{domain}-key.pem");
        if (File.Exists(cert) && File.Exists(key))
            listen += $"\n    listen 127.0.0.1:{appCfg.HttpsPort} ssl;\n    ssl_certificate {NginxConfig.Fwd(cert)};\n    ssl_certificate_key {NginxConfig.Fwd(key)};";

        var body = $$"""
        # BHServe site: {{cfg.Name}}  ({{domain}})  php=- server=python
        server {
        {{listen}}
            server_name {{domain}};
            access_log {{home}}/logs/{{cfg.Name}}-access.log;
            error_log  {{home}}/logs/{{cfg.Name}}-error.log;
            location / {
                proxy_pass http://127.0.0.1:{{cfg.Port}};
                proxy_set_header Host $host;
                proxy_http_version 1.1;
                proxy_set_header Upgrade $http_upgrade;
                proxy_set_header Connection "upgrade";
                proxy_set_header X-Real-IP $remote_addr;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                proxy_set_header X-Forwarded-Proto $scheme;
                proxy_read_timeout 600;
            }
        }

        """;
        Directory.CreateDirectory(Paths.NginxSites);
        File.WriteAllText(Path.Combine(Paths.NginxSites, $"{cfg.Name}.conf"), body);
    }
}
