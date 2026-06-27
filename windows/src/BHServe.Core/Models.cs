namespace BHServe.Core;

/// <summary>Role buckets, matching the mac engine's service registry.</summary>
public enum ServiceRole { Php, Web, Db, Cache, Mail, Dns, Node, Python, Tool, Other }

/// <summary>A managed service (a PHP version, nginx, MariaDB, …).</summary>
public record Service(
    string Key,           // "php@8.4", "nginx", "mariadb"
    ServiceRole Role,
    bool Installed,
    bool Running,
    string Version,
    bool AutoStart);

/// <summary>A site/vhost.</summary>
public record Site(
    string Name,
    string Domain,        // name + "." + tld
    string Php,           // "8.4"
    string Root,
    bool Secure,
    bool Enabled,
    string Server);       // "nginx" | "apache"

/// <summary>JSON snapshot the GUI decodes (the `api` verb's payload).</summary>
public record Snapshot(IReadOnlyList<Service> Services, IReadOnlyList<Site> Sites);
