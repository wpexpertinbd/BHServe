namespace BHServe.Core;

/// <summary>
/// The brains — the C# analog of the mac <c>engine/bhserve</c> script. The WinUI
/// app calls these methods in-process; <c>bhserve.exe</c> exposes the same surface
/// on the command line. Most methods are stubs to fill in on Windows (see the
/// matching mac function names in <c>engine/bhserve</c> for reference behavior).
/// </summary>
public sealed class Engine
{
    /// <summary>`bhserve init` — create the data-dir skeleton + default config.</summary>
    public void Init()
    {
        Paths.EnsureSkeleton();
        if (!File.Exists(Paths.ConfigJson))
        {
            File.WriteAllText(Paths.ConfigJson, """
            {
              "tld": "test",
              "http_port": 80,
              "https_port": 443,
              "default_php": "8.4",
              "default_web": "nginx",
              "sites_root": "%USERPROFILE%\\BHServe\\www",
              "autostart": false
            }
            """);
        }
        // TODO(windows): render_nginx_main()
    }

    // ── service registry / status ────────────────────────────────────────────
    public Snapshot Api() => throw new NotImplementedException("collect services + sites → Snapshot");

    // ── install / lifecycle (download pinned portable zips into Paths.Bin) ─────
    public void Install(string tool)   => throw new NotImplementedException();
    public void Update(string tool)    => throw new NotImplementedException();
    public void Uninstall(string tool) => throw new NotImplementedException();

    public void Start(string svc)   => throw new NotImplementedException(); // "all" → start every PHP version an enabled site uses (502 fix)
    public void Stop(string svc)    => throw new NotImplementedException();
    public void Restart(string svc) => throw new NotImplementedException();
    public void Enable(string svc)  => throw new NotImplementedException();  // auto-start ★
    public void Disable(string svc) => throw new NotImplementedException();

    // ── sites ─────────────────────────────────────────────────────────────────
    public void SiteAdd(string name, string php = "8.4", string? root = null,
                        string server = "nginx", string type = "php")
        => throw new NotImplementedException(); // render vhost + hosts line + auto-DB + (WP download)
    public void SiteRemove(string name)             => throw new NotImplementedException();
    public void SitePhp(string name, string version) => throw new NotImplementedException();
    public void Secure(string domain)               => throw new NotImplementedException(); // mkcert into Windows cert store

    // ── php.ini editor (parity with the mac `php ini` verb we just shipped) ─────
    /// <summary>Resolve the loaded php.ini for a version (seed one if the build ships none).</summary>
    public string PhpIniPath(string version) => throw new NotImplementedException();
    /// <summary>Restart that version's php-cgi so an edited php.ini takes effect.</summary>
    public void PhpIniReload(string version) => throw new NotImplementedException();

    // ── databases / node / tools ───────────────────────────────────────────────
    public void Db(string sub, params string[] args)   => throw new NotImplementedException();
    public void Node(string sub, params string[] args) => throw new NotImplementedException(); // fnm-win
    public void PhpMyAdmin() => throw new NotImplementedException();
    public void Adminer()    => throw new NotImplementedException();
    public void Mailpit()    => throw new NotImplementedException();
}
