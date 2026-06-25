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

    /// <summary>Human-readable description of an exception, never blank (some exceptions have an
    /// empty Message — fall back to the type name and include any inner exception).</summary>
    private static string Describe(Exception ex)
    {
        // Persist the full stack trace so a terse dialog message is still diagnosable.
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Logs, "last-error.log"), ex.ToString()); } catch { }
        var msg = ex.Message;
        if (string.IsNullOrWhiteSpace(msg)) msg = ex.GetType().Name;
        else msg = $"{msg} ({ex.GetType().Name})";
        if (ex.InnerException is { } inner)
            msg += "\n↳ " + (string.IsNullOrWhiteSpace(inner.Message) ? inner.GetType().Name : inner.Message);
        return msg;
    }

    /// <summary>Run a blocking engine action on a background thread. Returns the error message
    /// if it threw (also logged), else null — so callers can surface it in the UI.</summary>
    public Task<string?> Run(Action action) => Task.Run(() =>
    {
        try { action(); return (string?)null; }
        catch (Exception ex) { var m = Describe(ex); Append($"  ✗ {m}"); return m; }
    });

    /// <summary>Run an action and capture everything the engine printed during it (the same
    /// Ok/Warn/Err lines the CLI shows), so the GUI can display the full result.</summary>
    public Task<(bool ok, string output)> RunCaptured(Action action) => Task.Run(() =>
    {
        var sb = new System.Text.StringBuilder();
        void Cap(string l) => sb.AppendLine(l);
        LogAppended += Cap;
        var ok = true;
        try { action(); }
        catch (Exception ex) { Append($"  ✗ {Describe(ex)}"); ok = false; }
        finally { LogAppended -= Cap; }
        return (ok, sb.ToString().Trim());
    });

    public Task<Snapshot> Snapshot() => Task.Run(() => Engine.Api());

    // ── tracked operations (installs etc.) — survive page navigation ──────────────
    /// <summary>State of a long-running operation, readable from any page so progress persists.</summary>
    public sealed class OpState
    {
        public string Name = "";
        public bool Running;
        public bool Success;
        public double Progress = -1;   // 0–100, or -1 = indeterminate
        public string Message = "";
    }

    public OpState? CurrentOp { get; private set; }
    public event Action? OpChanged;
    private void RaiseOp() => OpChanged?.Invoke();

    /// <summary>Clear a finished operation's banner.</summary>
    public void DismissOp() { if (CurrentOp is { Running: false }) { CurrentOp = null; RaiseOp(); } }

    /// <summary>Run an install/long op on a background thread, tracked globally: live download %,
    /// the engine's status messages, and a final success/failed — visible even after navigating away.</summary>
    public async Task RunTracked(string name, Action action)
    {
        // One at a time keeps the single progress slot meaningful.
        if (CurrentOp is { Running: true }) { await Run(action); return; }

        var op = new OpState { Name = name, Running = true, Progress = -1, Message = "Starting…" };
        CurrentOp = op;
        void Cap(string l) { var t = l.Trim(); if (t.Length > 0) { op.Message = t; RaiseOp(); } }
        LogAppended += Cap;
        Downloader.OnProgress = p => { op.Progress = p; RaiseOp(); };
        RaiseOp();
        try
        {
            await Task.Run(action);
            op.Success = true; op.Message = $"✓ {name} done";
        }
        catch (Exception ex)
        {
            var m = Describe(ex); Append($"  ✗ {m}");
            op.Success = false; op.Message = "✗ " + m;
        }
        finally
        {
            op.Running = false; op.Progress = 100;
            Downloader.OnProgress = null;
            LogAppended -= Cap;
            RaiseOp();
        }
    }
}
