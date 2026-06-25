using System.Diagnostics;

namespace BHServe.Core;

/// <summary>php.ini editing, ionCube loader install, and per-version status — the
/// Windows analog of the mac engine's <c>php ini</c> / <c>php ioncube</c> / <c>php status</c>.</summary>
public static class Php
{
    private static (int code, string output) Run(string exe, string args, (string k, string v)? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (env is { } e) psi.Environment[e.k] = e.v;
        var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp);
    }

    /// <summary>Resolve (seeding if needed) the loaded php.ini for a version.</summary>
    public static string IniPath(string version)
    {
        var exe = Tools.PhpExe(version) ?? throw new BhException($"php {version} not installed");
        var (_, loaded) = Run(exe, "-r \"echo php_ini_loaded_file();\"");
        loaded = loaded.Trim();
        if (loaded.Length > 0 && File.Exists(loaded)) return loaded;

        var dir = Path.GetDirectoryName(exe)!;
        var ini = Path.Combine(dir, "php.ini");
        if (!File.Exists(ini))
        {
            var seed = new[] { "php.ini-development", "php.ini-production" }
                .Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
            if (seed is not null) File.Copy(seed, ini);
            else File.WriteAllText(ini, $"; BHServe-created php.ini for {version}\n; Add your directives below.\n");
        }
        return ini;
    }

    /// <summary>Restart the version's php-cgi (only if running) so an edited php.ini takes effect.</summary>
    public static bool IniReload(string version)
    {
        if (!PhpCgi.Running(version)) return false;
        PhpCgi.Stop(version);
        return PhpCgi.Start(version);
    }

    private static string VcTag(string phpExe)
    {
        var p = phpExe.ToLowerInvariant();
        if (p.Contains("vs17") || p.Contains("vc17")) return "vc17";
        if (p.Contains("vs15") || p.Contains("vc15")) return "vc15";
        return "vc16";   // vs16 builds (php 8.0–8.3)
    }

    /// <summary>Download + enable the ionCube loader for a PHP version (writes conf.d\00-ioncube.ini).</summary>
    public static void Ioncube(string version, Action<string> log)
    {
        var exe = Tools.PhpExe(version) ?? throw new BhException($"php {version} not installed");
        var vc = VcTag(exe);
        string loadersDir;
        try { loadersDir = Downloader.InstallIoncube(vc).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            throw new BhException(
                $"ionCube loader download/extract failed for {vc} ({ex.Message}). " +
                "ionCube's URLs change per VC build — grab ioncube_loaders_win_<vc>_x86-64.zip manually " +
                $"and extract into {Path.Combine(Paths.Bin, "ioncube", vc)}.");
        }

        // NTS loader (our php-cgi builds are NTS): ioncube_loader_win_<mm>.dll (NOT *_ts.dll)
        var dll = Directory.EnumerateFiles(loadersDir, $"ioncube_loader_win_{version}.dll", SearchOption.AllDirectories)
                           .FirstOrDefault(d => !d.Contains("_ts.dll", StringComparison.OrdinalIgnoreCase));
        if (dll is null)
            throw new BhException($"no NTS ionCube loader for PHP {version} in the {vc} bundle");

        var confd = PhpCgi.ConfDir(version);
        Directory.CreateDirectory(confd);
        File.WriteAllText(Path.Combine(confd, "00-ioncube.ini"),
            $"zend_extension={dll.Replace('\\', '/')}\n");
        log($"ionCube configured for php {version}: {dll}");
        if (IniReload(version)) log($"php-cgi {version} reloaded with ionCube");
        else log("restart this PHP to load it: bhserve restart all");
    }

    public record PhpInfo(string Version, bool Installed, bool Running, string Ioncube);

    public static IReadOnlyList<PhpInfo> Status()
    {
        var list = new List<PhpInfo>();
        foreach (var v in Services.PhpVersions)
        {
            var exe = Tools.PhpExe(v);
            if (exe is null) continue;
            var confd = PhpCgi.ConfDir(v);
            var configured = File.Exists(Path.Combine(confd, "00-ioncube.ini"));
            var (_, vout) = Run(exe, "-v", ("PHP_INI_SCAN_DIR", ";" + confd));
            var loaded = vout.Contains("ioncube", StringComparison.OrdinalIgnoreCase);
            list.Add(new PhpInfo(v, true, PhpCgi.Running(v),
                                 loaded ? "loaded" : configured ? "configured" : "no"));
        }
        return list;
    }
}
