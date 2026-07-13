using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace BHServe.Core;

/// <summary>
/// Mailpit — catches all outgoing mail (SMTP on :1025) and shows it in a web UI
/// (:8025). PHP's <c>sendmail_path</c> can point at <c>mailpit sendmail</c> so
/// app email lands here instead of the internet.
/// </summary>
public static class MailpitServer
{
    public const int SmtpPort = 1025;
    public const int UiPort   = 8025;
    private static string RunFile => Path.Combine(Paths.Run, "mailpit.json");

    public static bool Running()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", UiPort).Wait(500) && c.Connected;
        }
        catch { return false; }
    }

    public static bool Start()
    {
        if (Running()) return true;
        var exe = Tools.MailpitExe();
        if (exe is null) return false;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--listen 127.0.0.1:{UiPort} --smtp 127.0.0.1:{SmtpPort}",
            UseShellExecute = false, CreateNoWindow = true,
            // Do NOT redirect stdout/stderr: nothing reads the pipes, so once the ~4KB pipe
            // buffer fills with mailpit's logs the process BLOCKS (looks "installed but not
            // running"), and the pipes die with the parent app. Let it log nowhere.
            RedirectStandardOutput = false, RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        // Same stripped-env issue as PhpCgi (see PhpCgi.Start): the tray App can launch with an
        // empty Path/SystemRoot. mailpit is a Go binary — the Go runtime needs SystemRoot to load
        // system DLLs (crypto/network) and exits immediately without it. Rebuild both.
        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pathParts = new List<string> { Path.GetDirectoryName(exe)!, sysDir, Path.Combine(sysDir, "Wbem"), winDir };
        if (psi.Environment.TryGetValue("Path", out var inherited) && !string.IsNullOrWhiteSpace(inherited))
            pathParts.Add(inherited);
        psi.Environment["Path"] = string.Join(";", pathParts);
        if (!psi.Environment.TryGetValue("SystemRoot", out var sr) || string.IsNullOrWhiteSpace(sr))
            psi.Environment["SystemRoot"] = winDir;
        if (!psi.Environment.TryGetValue("windir", out var wd) || string.IsNullOrWhiteSpace(wd))
            psi.Environment["windir"] = winDir;
        var p = Process.Start(psi);
        if (p is null) return false;
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(RunFile, JsonSerializer.Serialize(new { pid = p.Id, ui = UiPort, smtp = SmtpPort }));
        for (var i = 0; i < 10 && !Running(); i++) System.Threading.Thread.Sleep(300);
        return Running();
    }

    public static void Stop()
    {
        try
        {
            if (File.Exists(RunFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(RunFile));
                Process.GetProcessById(doc.RootElement.GetProperty("pid").GetInt32()).Kill(true);
            }
        }
        catch { }
        try { File.Delete(RunFile); } catch { }
    }
}
