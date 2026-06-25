using System;
using System.Threading.Tasks;
using BHServe.Core;

namespace BHServe.App.Services;

/// <summary>
/// App-wide bridge to <see cref="Engine"/>. Captures the engine's text output into
/// a rolling log and runs blocking ops (process spawns, downloads) off the UI thread.
/// </summary>
public sealed class EngineHost
{
    public static EngineHost Instance { get; } = new();

    private readonly System.Text.StringBuilder _log = new();
    public event Action<string>? LogAppended;

    public Engine Engine { get; }

    private EngineHost()
    {
        Engine = new Engine { Out = Append, Err = Append };
        try { if (!System.IO.Directory.Exists(Paths.Config)) Engine.Init(); } catch { /* surfaced later */ }
    }

    private void Append(string line)
    {
        lock (_log) { _log.AppendLine(line); }
        LogAppended?.Invoke(line);
    }

    public string LogText { get { lock (_log) return _log.ToString(); } }

    /// <summary>Run a blocking engine action on a background thread; errors are logged, not thrown.</summary>
    public Task Run(Action action) => Task.Run(() =>
    {
        try { action(); }
        catch (Exception ex) { Append($"  ✗ {ex.Message}"); }
    });

    public Task<Snapshot> Snapshot() => Task.Run(() => Engine.Api());
}
