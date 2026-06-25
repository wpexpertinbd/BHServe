using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace BHServe.Core;

/// <summary>Shared start/stop/running for the simple TCP cache daemons (Redis, Memcached).</summary>
internal static class CacheProc
{
    public static bool PortOpen(int port)
    {
        try { using var c = new TcpClient(); return c.ConnectAsync("127.0.0.1", port).Wait(500) && c.Connected; }
        catch { return false; }
    }

    public static bool Start(string runName, string? exe, string args, int port)
    {
        if (exe is null) return false;
        if (PortOpen(port)) return true;
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,   // detach: don't inherit the console
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        var p = Process.Start(psi);
        if (p is null) return false;
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(Path.Combine(Paths.Run, $"{runName}.json"), JsonSerializer.Serialize(new { pid = p.Id, port }));
        for (var i = 0; i < 12 && !PortOpen(port); i++) System.Threading.Thread.Sleep(250);
        return PortOpen(port);
    }

    public static void Stop(string runName)
    {
        var f = Path.Combine(Paths.Run, $"{runName}.json");
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
}

/// <summary>Redis (port 6379). Dev mode: no persistence (--save "").</summary>
public static class Redis
{
    public const int Port = 6379;
    public static bool Running() => CacheProc.PortOpen(Port);
    public static bool Start() => CacheProc.Start("redis", Tools.RedisServerExe(), $"--port {Port} --save \"\"", Port);
    public static void Stop() => CacheProc.Stop("redis");
}

/// <summary>Memcached (port 11211).</summary>
public static class Memcached
{
    public const int Port = 11211;
    public static bool Running() => CacheProc.PortOpen(Port);
    public static bool Start() => CacheProc.Start("memcached", Tools.MemcachedExe(), $"-p {Port} -m 64", Port);
    public static void Stop() => CacheProc.Stop("memcached");
}
