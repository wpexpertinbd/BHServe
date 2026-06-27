import Foundation

// Mirrors the JSON emitted by `bhserve api`. Decoded with
// .convertFromSnakeCase so http_port -> httpPort, etc.

struct Snapshot: Codable, Sendable, Equatable {
    let config: EngineConfig
    let services: [Service]
    let sites: [Site]
    var helper: Bool?
    var brew: Bool?
    var cloudflared: Bool?
    var loginitem: Bool?
}

struct EngineConfig: Codable, Sendable, Equatable {
    let tld: String
    let httpPort: Int
    let httpsPort: Int
    let defaultPhp: String
    let defaultWeb: String
    let brewPrefix: String
    let sitesRoot: String
    var autostart: Bool?
}

struct Service: Codable, Sendable, Identifiable, Equatable {
    let key: String
    let formula: String
    let role: String
    let installed: Bool
    let running: Bool
    let version: String
    var enabled: Bool?
    var id: String { key }
    var autoStart: Bool { enabled ?? false }

    /// Short, human label for the row (strips the "PHP "/"nginx version: " noise).
    var shortVersion: String {
        var v = version
        for p in ["nginx version: ", "Server version: "] where v.hasPrefix(p) {
            v = String(v.dropFirst(p.count))
        }
        // keep up to the first parenthesis / comma
        if let i = v.firstIndex(where: { $0 == "(" || $0 == "," }) {
            v = String(v[..<i])
        }
        return v.trimmingCharacters(in: .whitespaces)
    }
}

struct Site: Codable, Sendable, Identifiable, Equatable {
    let name: String
    let domain: String
    let php: String
    let root: String
    let secure: Bool
    var enabled: Bool = true
    var server: String?
    var tunnel: String?   // public Cloudflare quick-tunnel URL when sharing
    // ── Node-site fields (present only when server == "node") ────────────────
    var node: Bool = false
    var feRunning: Bool = false
    var beRunning: Bool = false
    var fePort: String?
    var bePort: String?
    var feDir: String?
    var beDir: String?
    var feCmd: String?
    var beCmd: String?
    var apiPaths: String?
    // ── Python-site fields (present only when server == "python") ─────────────
    var python: Bool = false
    var pyRunning: Bool = false
    var pyPort: String?
    var pyDir: String?
    var pyCmd: String?
    var pyVenv: String?
    var pyVer: String?
    var id: String { name }

    var serverKind: String { node ? "node" : (python ? "python" : (server ?? "nginx")) }
    var hasBackend: Bool { !(beDir ?? "").isEmpty }
    /// Node site is "up" when the frontend (and backend, if any) processes run.
    var nodeRunning: Bool { node && feRunning && (!hasBackend || beRunning) }
    var url: URL? { URL(string: (secure ? "https://" : "http://") + domain) }

    enum CodingKeys: String, CodingKey {
        case name, domain, php, root, secure, enabled, server, tunnel
        case node, feRunning, beRunning, fePort, bePort, feDir, beDir, feCmd, beCmd, apiPaths
        case python, pyRunning, pyPort, pyDir, pyCmd, pyVenv, pyVer
    }
    // Custom decode so site rows without the Node fields (every PHP/WordPress site)
    // still decode — synthesized Decodable would throw keyNotFound and drop the whole list.
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        name      = try c.decode(String.self, forKey: .name)
        domain    = try c.decode(String.self, forKey: .domain)
        php       = try c.decodeIfPresent(String.self, forKey: .php) ?? ""
        root      = try c.decodeIfPresent(String.self, forKey: .root) ?? ""
        secure    = try c.decodeIfPresent(Bool.self, forKey: .secure) ?? false
        enabled   = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        server    = try c.decodeIfPresent(String.self, forKey: .server)
        tunnel    = try c.decodeIfPresent(String.self, forKey: .tunnel)
        node      = try c.decodeIfPresent(Bool.self, forKey: .node) ?? false
        feRunning = try c.decodeIfPresent(Bool.self, forKey: .feRunning) ?? false
        beRunning = try c.decodeIfPresent(Bool.self, forKey: .beRunning) ?? false
        fePort    = try c.decodeIfPresent(String.self, forKey: .fePort)
        bePort    = try c.decodeIfPresent(String.self, forKey: .bePort)
        feDir     = try c.decodeIfPresent(String.self, forKey: .feDir)
        beDir     = try c.decodeIfPresent(String.self, forKey: .beDir)
        feCmd     = try c.decodeIfPresent(String.self, forKey: .feCmd)
        beCmd     = try c.decodeIfPresent(String.self, forKey: .beCmd)
        apiPaths  = try c.decodeIfPresent(String.self, forKey: .apiPaths)
        python    = try c.decodeIfPresent(Bool.self, forKey: .python) ?? false
        pyRunning = try c.decodeIfPresent(Bool.self, forKey: .pyRunning) ?? false
        pyPort    = try c.decodeIfPresent(String.self, forKey: .pyPort)
        pyDir     = try c.decodeIfPresent(String.self, forKey: .pyDir)
        pyCmd     = try c.decodeIfPresent(String.self, forKey: .pyCmd)
        pyVenv    = try c.decodeIfPresent(String.self, forKey: .pyVenv)
        pyVer     = try c.decodeIfPresent(String.self, forKey: .pyVer)
    }
}

struct Database: Codable, Sendable, Identifiable, Equatable {
    let name: String
    let engine: String   // "mysql" | "pg"
    let hasUser: Bool
    let user: String
    var id: String { "\(engine):\(name)" }
    var engineLabel: String { engine == "pg" ? "PostgreSQL" : "MySQL / MariaDB" }
}

struct NodeVersion: Codable, Sendable, Identifiable, Equatable {
    let version: String
    let isDefault: Bool
    var id: String { version }
    enum CodingKeys: String, CodingKey { case version; case isDefault = "default" }
}

enum PasswordGen {
    /// Unambiguous, shell/SQL-safe charset (no quotes, backslash, dollar, or O/0/l/1).
    static func make(_ length: Int = 16) -> String {
        let chars = Array("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%^*-_=+")
        return String((0..<length).map { _ in chars.randomElement()! })
    }
}

// Roles grouped for display, in the order ServBay-style apps show them.
enum ServiceRole: String, CaseIterable {
    case php, web, db, cache, dns, tls, mail, node, python

    var title: String {
        switch self {
        case .php: "PHP"
        case .web: "Web Server"
        case .db: "Databases"
        case .cache: "Cache"
        case .dns: "DNS"
        case .tls: "TLS / Certificates"
        case .mail: "Mail"
        case .node: "Node"
        case .python: "Python"
        }
    }
}
