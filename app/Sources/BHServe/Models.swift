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
    var id: String { name }

    var serverKind: String { (server ?? "nginx") }
    var url: URL? { URL(string: (secure ? "https://" : "http://") + domain) }
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
    case php, web, db, cache, dns, tls, mail, node

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
        }
    }
}
