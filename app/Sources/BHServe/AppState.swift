import SwiftUI
import Observation

@MainActor
@Observable
final class AppState {
    var snapshot: Snapshot?
    var databases: [Database] = []
    var rootStatus = ""   // "set" | "blank" | "unavailable" | ""
    var errorText: String?
    var busy = false
    var lastAction: String?

    let engine: Engine

    init() {
        engine = Engine(enginePath: AppState.resolveEnginePath())
    }

    /// Find the bhserve engine: explicit env override, then the user's config
    /// root, then the dev checkout. (Phase 5 will bundle it inside the .app.)
    static func resolveEnginePath() -> String {
        if let e = ProcessInfo.processInfo.environment["BHSERVE_ENGINE"], !e.isEmpty { return e }
        var candidates: [String] = []
        // bundled engine inside BHServe.app/Contents/Resources (self-contained app)
        if let res = Bundle.main.resourceURL?.appendingPathComponent("bhserve").path { candidates.append(res) }
        candidates += [
            "\(NSHomeDirectory())/.bhserve/engine/bhserve",
            "/Applications/ServBay/www/BHServe/engine/bhserve",
        ]
        for c in candidates where FileManager.default.isExecutableFile(atPath: c) { return c }
        return candidates.last!
    }

    var running: [Service] { snapshot?.services.filter { $0.running } ?? [] }
    var installed: [Service] { snapshot?.services.filter { $0.installed } ?? [] }

    func services(role: ServiceRole) -> [Service] {
        snapshot?.services.filter { $0.role == role.rawValue } ?? []
    }

    /// PHP versions available to assign to a site.
    var phpChoices: [String] {
        services(role: .php).filter { $0.installed }.map { $0.key }
    }

    func reload() async {
        let eng = engine
        do {
            let snap = try await Task.detached { try eng.snapshot() }.value
            if snap != snapshot { snapshot = snap }   // avoid needless re-render (keeps TextField focus steady)
            errorText = nil
        } catch {
            errorText = error.localizedDescription
        }
    }

    /// start/stop/restart a service (or "all"). nginx/all/dns need root.
    func control(_ action: String, _ target: String) async {
        guard !busy else { return }
        busy = true
        lastAction = "\(action) \(target)…"
        defer { busy = false; lastAction = nil }
        let eng = engine
        let privileged = (target == "nginx" || target == "all" || target == "dns")
        do {
            try await Task.detached {
                if privileged { try eng.runPrivileged([action, target]) }
                else { _ = try eng.run([action, target]) }
            }.value
            await reload()
        } catch {
            errorText = error.localizedDescription
        }
    }

    var mysqlRunning: Bool {
        snapshot?.services.contains { ($0.key == "mariadb" || $0.key == "mysql") && $0.running } ?? false
    }
    var pgRunning: Bool {
        snapshot?.services.contains { $0.key == "postgresql@17" && $0.running } ?? false
    }

    func reloadDatabases() async {
        guard mysqlRunning || pgRunning else { databases = []; rootStatus = ""; return }
        let eng = engine
        do {
            let json = try await Task.detached { try eng.run(["db", "list", "--json"]) }.value
            databases = try JSONDecoder().decode([Database].self, from: Data(json.utf8))
        } catch {
            errorText = error.localizedDescription
        }
        await reloadRootStatus()
    }

    func reloadRootStatus() async {
        guard mysqlRunning else { rootStatus = ""; return }
        let eng = engine
        if let r = try? await Task.detached(operation: { try eng.run(["db", "root-status"]) }).value {
            rootStatus = r.trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }

    /// Set the mysql/mariadb root password. Empty string clears it (blank password).
    func setRootPassword(_ pw: String) async {
        let env = pw.isEmpty ? [:] : ["BHSERVE_DB_PASSWORD": pw]
        await runUser(["db", "root-passwd"],
                      note: pw.isEmpty ? "clearing root password…" : "setting root password…",
                      env: env)
        await reloadRootStatus()
    }

    func createDatabase(_ name: String, engine dbEngine: String, password: String = "") async {
        let clean = name.trimmingCharacters(in: .whitespaces)
        guard !clean.isEmpty else { return }
        let env = password.isEmpty ? [:] : ["BHSERVE_DB_PASSWORD": password]
        await runUser(["db", "create", clean, "--engine", dbEngine], note: "creating \(clean)…", env: env)
        await reloadDatabases()
    }

    func setDatabasePassword(_ name: String, engine dbEngine: String, password: String) async {
        guard !password.isEmpty else { return }
        await runUser(["db", "passwd", name, "--engine", dbEngine],
                      note: "setting password for \(name)…",
                      env: ["BHSERVE_DB_PASSWORD": password])
        await reloadDatabases()
    }

    func dropDatabase(_ name: String, engine dbEngine: String) async {
        await runUser(["db", "drop", name, "--engine", dbEngine], note: "dropping \(name)…")
        await reloadDatabases()
    }

    // ── settings ──────────────────────────────────────────────────────────
    func saveSettings(tld: String, httpPort: String, httpsPort: String,
                      sitesRoot: String, defaultPhp: String, defaultWeb: String) async {
        guard let cfg = snapshot?.config, !busy else { return }
        var changes: [(String, String)] = []
        if tld != cfg.tld { changes.append(("tld", tld)) }
        if httpPort != String(cfg.httpPort) { changes.append(("http_port", httpPort)) }
        if httpsPort != String(cfg.httpsPort) { changes.append(("https_port", httpsPort)) }
        if sitesRoot != cfg.sitesRoot { changes.append(("sites_root", sitesRoot)) }
        if defaultPhp != cfg.defaultPhp { changes.append(("default_php", defaultPhp)) }
        if defaultWeb != cfg.defaultWeb { changes.append(("default_web", defaultWeb)) }
        guard !changes.isEmpty else { return }

        busy = true; lastAction = "saving settings…"; defer { busy = false; lastAction = nil }
        let eng = engine
        let needsRestart = changes.contains { ["tld", "http_port", "https_port"].contains($0.0) }
        let nginxUp = snapshot?.services.contains { $0.key == "nginx" && $0.running } ?? false
        do {
            try await Task.detached {
                for (k, v) in changes { _ = try eng.run(["config", "set", k, v]) }
                if needsRestart && nginxUp { try eng.runPrivileged(["restart", "nginx"]) }
            }.value
            await reload()
        } catch {
            errorText = error.localizedDescription
        }
    }

    // ── logs ──────────────────────────────────────────────────────────────
    var logFiles: [String] = []

    func listLogs() async {
        let eng = engine
        if let j = try? await Task.detached(operation: { try eng.run(["logs", "--list"]) }).value {
            logFiles = (try? JSONDecoder().decode([String].self, from: Data(j.utf8))) ?? []
        }
    }

    func readLog(_ name: String, lines: Int = 400) async -> String {
        let eng = engine
        return (try? await Task.detached(operation: { try eng.run(["logs", name, String(lines)]) }).value) ?? ""
    }

    func installService(_ key: String) async {
        await runUser(["install", key], note: "installing \(key)…")
    }

    func addSite(name: String, php: String) async {
        let clean = name.trimmingCharacters(in: .whitespaces)
        guard !clean.isEmpty else { return }
        await runUser(["site", "add", clean, "--php", php], note: "adding \(clean)…")
    }

    func removeSite(_ name: String) async {
        await runUser(["site", "rm", name], note: "removing \(name)…")
    }

    func setSitePHP(_ name: String, php: String) async {
        await runUser(["site", "php", name, php], note: "switching \(name) → \(php)…")
        await control("restart", "nginx")  // re-rendered vhost needs a reload
    }

    func secure(domain: String) async {
        await runUser(["secure", domain], note: "securing \(domain)…")
        // turning HTTPS on re-renders the vhost → nginx needs a reload (root)
        await control("restart", "nginx")
    }

    private func runUser(_ args: [String], note: String, env: [String: String] = [:]) async {
        guard !busy else { return }
        busy = true
        lastAction = note
        defer { busy = false; lastAction = nil }
        let eng = engine
        do {
            try await Task.detached { _ = try eng.run(args, env: env) }.value
            await reload()
        } catch {
            errorText = error.localizedDescription
        }
    }
}
