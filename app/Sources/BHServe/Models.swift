import Foundation

// Mirrors the JSON emitted by `bhserve api`. Decoded with
// .convertFromSnakeCase so http_port -> httpPort, etc.

struct Snapshot: Codable, Sendable {
    let config: EngineConfig
    let services: [Service]
    let sites: [Site]
}

struct EngineConfig: Codable, Sendable {
    let tld: String
    let httpPort: Int
    let httpsPort: Int
    let defaultPhp: String
    let defaultWeb: String
    let brewPrefix: String
    let sitesRoot: String
}

struct Service: Codable, Sendable, Identifiable {
    let key: String
    let formula: String
    let role: String
    let installed: Bool
    let running: Bool
    let version: String
    var id: String { key }

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

struct Site: Codable, Sendable, Identifiable {
    let name: String
    let domain: String
    let php: String
    let root: String
    let secure: Bool
    var id: String { name }

    var url: URL? { URL(string: (secure ? "https://" : "http://") + domain) }
}

struct Database: Codable, Sendable, Identifiable {
    let name: String
    let engine: String   // "mysql" | "pg"
    var id: String { "\(engine):\(name)" }
    var engineLabel: String { engine == "pg" ? "PostgreSQL" : "MySQL / MariaDB" }
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
