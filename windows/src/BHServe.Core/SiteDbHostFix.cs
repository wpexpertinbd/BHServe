using System.Text.RegularExpressions;

namespace BHServe.Core;

/// <summary>
/// One-shot, idempotent repair for the Windows "localhost" database stall. On Windows, PHP resolves
/// <c>localhost</c> to IPv6 <c>::1</c> first; MariaDB listens on IPv4 <c>127.0.0.1</c>, so every DB
/// connection waits ~2 s for <c>::1</c> to time out before falling back — pages feel like they're
/// loading from a remote server. Rewriting the DB host to <c>127.0.0.1</c> skips that entirely.
///
/// Sites imported from XAMPP / Laragon / MAMP ship with <c>localhost</c>; this fixes them in place.
/// New WordPress sites BHServe creates already use 127.0.0.1 (see <see cref="Downloader.InstallWordPress"/>).
/// Safe to run on every launch: only the DB-host directive is touched, files are written UTF-8 without
/// a BOM (a BOM before <c>&lt;?php</c> breaks PHP), and a file is rewritten only if it actually changed.
/// </summary>
public static class SiteDbHostFix
{
    // wp-config.php:        define( 'DB_HOST', 'localhost' );
    private static readonly Regex Wp  = new(@"(define\(\s*'DB_HOST'\s*,\s*)'localhost'", RegexOptions.Compiled);
    // OpenCart config.php:  define('DB_HOSTNAME', 'localhost');
    private static readonly Regex Oc  = new(@"(define\(\s*'DB_HOSTNAME'\s*,\s*)'localhost'", RegexOptions.Compiled);
    // Laravel .env:         DB_HOST=localhost   (don't consume the trailing newline/comment)
    private static readonly Regex Env = new(@"(?m)^(\s*DB_HOST\s*=\s*)localhost(?=\s*(?:#.*)?$)", RegexOptions.Compiled);

    private static readonly System.Text.UTF8Encoding NoBom = new(false);

    /// <summary>Scan <paramref name="sitesRoot"/> and rewrite any <c>localhost</c> DB host to
    /// <c>127.0.0.1</c>. Returns the files that were changed (empty if none / on any error).</summary>
    public static List<string> Run(string sitesRoot)
    {
        var fixedFiles = new List<string>();
        if (string.IsNullOrWhiteSpace(sitesRoot) || !Directory.Exists(sitesRoot)) return fixedFiles;

        foreach (var (file, rx, quoted) in Targets(sitesRoot))
        {
            try
            {
                var text = File.ReadAllText(file);
                var repl = quoted ? "${1}'127.0.0.1'" : "${1}127.0.0.1";
                var updated = rx.Replace(text, repl);
                if (updated != text)
                {
                    File.WriteAllText(file, updated, NoBom);
                    fixedFiles.Add(file);
                }
            }
            catch { /* skip unreadable / locked files */ }
        }
        return fixedFiles;
    }

    private static IEnumerable<(string file, Regex rx, bool quoted)> Targets(string root)
    {
        foreach (var f in Enumerate(root, "wp-config.php")) yield return (f, Wp, true);
        foreach (var f in Enumerate(root, "config.php"))    yield return (f, Oc, true);
        foreach (var f in Enumerate(root, ".env"))          yield return (f, Env, false);
    }

    private static IEnumerable<string> Enumerate(string root, string pattern)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
        {
            // skip dependency / framework trees (config.php especially is common inside libraries)
            if (f.Contains(@"\vendor\") || f.Contains(@"\node_modules\") || f.Contains(@"\storage\")) continue;
            yield return f;
        }
    }
}
