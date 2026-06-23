import SwiftUI
import Observation

@MainActor
@Observable
final class AppState {
    var snapshot: Snapshot?
    var databases: [Database] = []
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
        let candidates = [
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
        guard mysqlRunning || pgRunning else { databases = []; return }
        let eng = engine
        do {
            let json = try await Task.detached { try eng.run(["db", "list", "--json"]) }.value
            databases = try JSONDecoder().decode([Database].self, from: Data(json.utf8))
        } catch {
            errorText = error.localizedDescription
        }
    }

    func createDatabase(_ name: String, engine dbEngine: String) async {
        let clean = name.trimmingCharacters(in: .whitespaces)
        guard !clean.isEmpty else { return }
        await runUser(["db", "create", clean, "--engine", dbEngine], note: "creating \(clean)…")
        await reloadDatabases()
    }

    func dropDatabase(_ name: String, engine dbEngine: String) async {
        await runUser(["db", "drop", name, "--engine", dbEngine], note: "dropping \(name)…")
        await reloadDatabases()
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

    private func runUser(_ args: [String], note: String) async {
        guard !busy else { return }
        busy = true
        lastAction = note
        defer { busy = false; lastAction = nil }
        let eng = engine
        do {
            try await Task.detached { _ = try eng.run(args) }.value
            await reload()
        } catch {
            errorText = error.localizedDescription
        }
    }
}
