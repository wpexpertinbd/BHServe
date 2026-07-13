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

            // mkcert -install only trusts the CA for the CURRENT USER (CurrentUser\Root).
            // HTTPS-scanning security software (ESET, Kaspersky, Avast…) validates server certs
            // against the MACHINE store — without the CA there it re-signs every local site with
            // an "untrusted" placeholder → ERR_CERT_AUTHORITY_INVALID in every browser. We're
            // already elevated here, so add the CA machine-wide too.
            try
            {
                var caOut = Process.Start(new ProcessStartInfo
                { FileName = mkc, Arguments = "-CAROOT", UseShellExecute = false, RedirectStandardOutput = true })!;
                var caroot = caOut.StandardOutput.ReadToEnd().Trim();
                caOut.WaitForExit();
                var rootPem = Path.Combine(caroot, "rootCA.pem");
                if (File.Exists(rootPem))
                {
                    var cu = Process.Start(new ProcessStartInfo
                    { FileName = "certutil.exe", Arguments = $"-addstore -f Root \"{rootPem}\"", UseShellExecute = false, CreateNoWindow = true })!;
                    cu.WaitForExit();
                    if (cu.ExitCode != 0) Console.Error.WriteLine("certutil machine-store add failed (user-store trust still installed)");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"machine-store CA add failed: {ex.Message}"); }

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
