using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BHServe.Core;

/// <summary>
/// Starts BHServe's background servers with Windows' hard-error dialogs suppressed.
///
/// A child process inherits the CREATING process's error mode, so whatever we set here applies to
/// the server we launch AND to anything it launches itself — which matters because nginx forks its
/// own worker processes, and those workers were the ones putting up
/// "nginx.exe - This application was unable to start correctly" during shutdown.
///
/// Why suppress at all: these are invisible background workers. If one fails to load — killed
/// mid-initialisation at shutdown, or missing a DLL — the useful outcome is a line in the log, not
/// a modal box the user has to dismiss and can't act on. (The same inheritance is what made the
/// missing-VC-runtime failure show up as a popup storm rather than one clear message.)
///
/// Deliberately NOT applied to short-lived, user-initiated things (installers, mkcert, the
/// elevation helper): there, a Windows error dialog is genuinely useful feedback.
/// </summary>
public static class ChildProc
{
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX  = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetErrorMode(uint uMode);

    /// <summary>Process.Start, but the child (and its children) inherit a no-dialog error mode.
    /// Our own error mode is restored immediately, so a BHServe crash still reports normally.</summary>
    public static Process? Start(ProcessStartInfo psi)
    {
        var prev = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        try { return Process.Start(psi); }
        finally { SetErrorMode(prev); }
    }
}
