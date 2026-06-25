using System.Diagnostics;
using BHServe.Core;

// bhserve-elevate — runs elevated (requireAdministrator). Does ONLY the admin-only
// steps so the main CLI/GUI can stay unprivileged. Invoked via runas by Core.Elevation.
//   bhserve-elevate hosts-add <domain>
//   bhserve-elevate hosts-remove <domain>
//   bhserve-elevate mkcert-install

if (args.Length == 0) return 1;
var verb = args[0];

try
{
    switch (verb)
    {
        case "hosts-add":
            if (args.Length < 2 || !Hosts.IsValidDomain(args[1])) return 1;
            return Hosts.Add(args[1]) ? 0 : 1;

        case "hosts-remove":
            if (args.Length < 2 || !Hosts.IsValidDomain(args[1])) return 1;
            return Hosts.Remove(args[1]) ? 0 : 1;

        case "mkcert-install":
        {
            var mkc = Tools.MkcertExe();
            if (mkc is null) { Console.Error.WriteLine("mkcert not installed"); return 1; }
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = mkc,
                Arguments = "-install",
                UseShellExecute = false,
            })!;
            p.WaitForExit();
            return p.ExitCode;
        }

        default:
            Console.Error.WriteLine($"unknown verb: {verb}");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
