using System.Security.Principal;
using System.Text.RegularExpressions;

namespace BHServe.Core;

/// <summary>
/// Windows hosts-file management (the analog of mac dnsmasq/resolver, since the
/// Windows hosts file can't do wildcards). Each managed line is tagged so we can
/// add/remove cleanly. Writing requires Administrator — callers check
/// <see cref="IsElevated"/> and surface an elevation hint when false.
/// </summary>
public static class Hosts
{
    private const string Tag = "# BHServe";

    /// <summary>A strict hostname: lowercase labels of [a-z0-9-] joined by dots, no leading/trailing
    /// hyphen, max 253 chars. Critically rejects whitespace/newlines so the value can never inject
    /// extra lines into the hosts file (this code runs elevated).</summary>
    public static bool IsValidDomain(string domain) =>
        !string.IsNullOrEmpty(domain) && domain.Length <= 253 &&
        Regex.IsMatch(domain, @"^(?=.{1,253}$)([a-z0-9](-?[a-z0-9])*)(\.[a-z0-9](-?[a-z0-9])*)+$");

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool Has(string domain)
    {
        try
        {
            return File.Exists(Paths.HostsFile) &&
                   File.ReadLines(Paths.HostsFile)
                       .Any(l => l.Contains(Tag) && l.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                                                     .Contains(domain));
        }
        catch { return false; }
    }

    /// <summary>Append "127.0.0.1 domain # BHServe" if absent. Returns false (no-throw) when not elevated.</summary>
    public static bool Add(string domain)
    {
        if (!IsValidDomain(domain)) return false;   // never write unvalidated input to the hosts file
        if (Has(domain)) return true;
        if (!IsElevated()) return false;
        try
        {
            File.AppendAllText(Paths.HostsFile, $"127.0.0.1 {domain} {Tag}{Environment.NewLine}");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Remove our tagged line for a domain. Returns false when not elevated.</summary>
    public static bool Remove(string domain)
    {
        if (!IsValidDomain(domain)) return false;
        if (!Has(domain)) return true;
        if (!IsElevated()) return false;
        try
        {
            var kept = File.ReadAllLines(Paths.HostsFile)
                           .Where(l => !(l.Contains(Tag) && l.Contains(domain)));
            File.WriteAllLines(Paths.HostsFile, kept);
            return true;
        }
        catch { return false; }
    }
}
