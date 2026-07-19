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

    /// <summary>The MSVC toolset windows.php.net builds each PHP branch with — it selects the matching
    /// ionCube loader bundle. BHServe flattens PHP into bin\php\&lt;version&gt; (no vs## token in the path),
    /// so sniffing the exe path is unreliable; map by version NUMBER, which is fixed per branch:
    ///   7.x → VC15,  8.0–8.3 → VS16 (vc16),  8.4+ → VS17 (vc17).</summary>
    private static string VcFor(string version)
    {
        var parts = version.Split('.');
        var maj = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 8;
        var min = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
        if (maj <= 7) return "vc15";
        if (maj == 8 && min <= 3) return "vc16";
        return "vc17";   // 8.4, 8.5, 8.6, future
    }

    /// <summary>Download + enable the ionCube loader for a PHP version.
    /// ionCube must be the FIRST zend_extension (before OPcache) or it aborts with "The Loader must
    /// appear as the first entry". The PHP_INI_SCAN_DIR (conf.d) always loads AFTER the main php.ini —
    /// where windows.php.net enables OPcache — so a conf.d entry would load second and fail. We instead
    /// insert the loader line into the MAIN php.ini, immediately before the opcache zend_extension.</summary>
    public static void Ioncube(string version, Action<string> log)
    {
        var exe = Tools.PhpExe(version) ?? throw new BhException($"php {version} not installed");
        var vc = VcFor(version);
        string loadersDir;
        try { loadersDir = Downloader.InstallIoncube(vc).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            throw new BhException(
                $"ionCube loader download/extract failed for {vc} ({ex.Message}). " +
                "ionCube's URLs change per VC build — grab ioncube_loaders_win_nonts_<vc>_x86-64.zip manually " +
                $"and extract into {Path.Combine(Paths.Bin, "ioncube", "nonts-" + vc)}.");
        }

        // NTS loader (our php-cgi builds are NTS): ioncube_loader_win_<mm>.dll (NOT *_ts.dll)
        static string? FindDll(string dir, string version) =>
            Directory.EnumerateFiles(dir, $"ioncube_loader_win_{version}.dll", SearchOption.AllDirectories)
                     .FirstOrDefault(d => !d.Contains("_ts.dll", StringComparison.OrdinalIgnoreCase));
        var dll = FindDll(loadersDir, version);
        if (dll is null)
        {
            // A stale/partial cache dir (InstallIoncube early-returns when the dir exists) can lack this
            // version's DLL — e.g. an interrupted extract, or an AV that quarantined the file. Purge the
            // cache and download the bundle fresh, ONCE, before giving up.
            log($"ionCube {vc} cache is missing the PHP {version} loader — re-downloading the bundle");
            try { Directory.Delete(loadersDir, true); } catch { }
            loadersDir = Downloader.InstallIoncube(vc).GetAwaiter().GetResult();
            dll = FindDll(loadersDir, version);
        }
        if (dll is null)
            throw new BhException($"no NTS ionCube loader for PHP {version} in the {vc} bundle");

        var line = $"zend_extension={dll.Replace('\\', '/')}";
        var ini = IniPath(version);
        var lines = File.ReadAllLines(ini).ToList();
        // Idempotent: drop any prior ionCube line we (or anyone) added.
        lines.RemoveAll(l => l.TrimStart().StartsWith("zend_extension", StringComparison.OrdinalIgnoreCase)
                          && l.Contains("ioncube_loader", StringComparison.OrdinalIgnoreCase));
        // Insert before the first ACTIVE opcache zend_extension (commented ;… lines are skipped); else at top.
        var idx = lines.FindIndex(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("zend_extension", StringComparison.OrdinalIgnoreCase)
                && t.Contains("opcache", StringComparison.OrdinalIgnoreCase);
        });
        lines.Insert(idx < 0 ? 0 : idx, line);
        File.WriteAllLines(ini, lines);

        // Retire the old conf.d approach (a stray scan-dir copy would load second and re-trigger the error).
        try { File.Delete(Path.Combine(PhpCgi.ConfDir(version), "00-ioncube.ini")); } catch { }

        log($"ionCube enabled for php {version} (before opcache in php.ini): {dll}");
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
            var ini = IniPath(v);
            var configured = File.ReadAllText(ini).Contains("ioncube_loader", StringComparison.OrdinalIgnoreCase);
            var (_, vout) = Run(exe, "-v");
            // Match the SUCCESS banner ("with the ionCube PHP Loader"); the failure messages
            // ("Failed loading …ioncube…", "[ionCube Loader] The Loader must appear…") must NOT count as loaded.
            var loaded = vout.Contains("ionCube PHP Loader", StringComparison.OrdinalIgnoreCase);
            list.Add(new PhpInfo(v, true, PhpCgi.Running(v),
                                 loaded ? "loaded" : configured ? "configured" : "no"));
        }
        return list;
    }
}
