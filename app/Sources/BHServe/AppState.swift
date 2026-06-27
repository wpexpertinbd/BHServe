import SwiftUI
import Observation
import ServiceManagement
import UserNotifications

@MainActor
@Observable
final class AppState {
    var snapshot: Snapshot?
    var databases: [Database] = []
    var rootStatus = ""   // "set" | "blank" | "unavailable" | ""
    var errorText: String?
    var busy = false
    var lastAction: String?

    /// Outcome of a long action (add site / install service) → shown in a result sheet.
    struct ActionResult: Identifiable, Equatable {
        let id = UUID()
        var title: String
        var success: Bool
        var steps: [Step]
        var url: String?
        struct Step: Identifiable, Equatable { let id = UUID(); var done: Bool; var text: String }
    }
    var actionResult: ActionResult?

    /// Parse engine output into result steps: ANSI-stripped, ✓-lines are "done".
    static func parseSteps(_ raw: String) -> [ActionResult.Step] {
        let clean = raw.replacingOccurrences(of: "\u{1B}\\[[0-9;]*m", with: "", options: .regularExpression)
        return clean.split(separator: "\n").compactMap { l in
            let t = l.trimmingCharacters(in: .whitespaces)
            guard !t.isEmpty else { return nil }
            let done = t.hasPrefix("✓")
            var text = t
            for p in ["✓ ", "✗ ", "! ", "✓", "✗"] where text.hasPrefix(p) { text = String(text.dropFirst(p.count)); break }
            // skip the engine's own header lines (e.g. "Site 'x' added")
            if text.hasPrefix("Site '") || text.hasPrefix("Installing ") { return nil }
            return ActionResult.Step(done: done, text: text)
        }
    }

    let engine: Engine
    static let shared = AppState()

    /// True when launchd started us at login (LaunchAgent passes --background) — we
    /// stay menu-bar-only (no Dock, no window) and just bring services up.
    static let isBackgroundLaunch = CommandLine.arguments.contains("--background")
    private var didBoot = false

    init() {
        engine = Engine(enginePath: AppState.resolveEnginePath())
    }

    /// Run once per app launch: refresh, then (if enabled) start all services.
    /// start-all is idempotent — already-running services (e.g. Homebrew's own
    /// MariaDB/Redis LaunchAgents) are skipped, so this no longer bails just because
    /// something is already up.
    func bootAutostart() async {
        guard !didBoot else { return }
        didBoot = true
        retireOldSMAgents()
        await reload()
        if autostartEnabled { await control("start", "all") }
        if autoUpdateCheckEnabled { await checkForUpdate(auto: true) }
        startUpdatePolling()
    }

    /// BHServe runs persistently (menu bar), so a one-shot launch check misses releases
    /// published later. Re-check every 6h while running (cheap; well under GitHub's limit).
    private var updatePolling = false
    private func startUpdatePolling() {
        guard !updatePolling else { return }
        updatePolling = true
        Task { [self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(6 * 3600))
                if autoUpdateCheckEnabled, !updateAvailable { await checkForUpdate(auto: true) }
            }
        }
    }

    /// One-time cleanup: earlier builds used SMAppService (mainApp, then a `.helper`
    /// agent) for start-at-login. Both are fragile for an ad-hoc-signed app (the
    /// `.helper` agent's code requirement goes stale on update → "spawn failed").
    /// We now use a plain LaunchAgent managed by the engine; unregister the old ones.
    private func retireOldSMAgents() {
        let d = UserDefaults.standard
        guard !d.bool(forKey: "retiredSMAgents") else { return }
        try? SMAppService.mainApp.unregister()
        try? SMAppService.agent(plistName: "com.biswashost.bhserve.helper.plist").unregister()
        d.set(true, forKey: "retiredSMAgents")
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

    /// Installed *daemon* services that Start/Stop/Restart-All actually manage — excludes
    /// non-daemon tools (mkcert/fnm always report "active" once installed, so they'd skew
    /// the all-running check). Drives the enabled/disabled state of the footer buttons.
    var daemonServices: [Service] {
        (snapshot?.services ?? []).filter { $0.installed && ["php", "web", "db", "cache", "mail", "dns"].contains($0.role) }
    }
    var hasDaemons: Bool { !daemonServices.isEmpty }
    var allDaemonsRunning: Bool { let d = daemonServices; return !d.isEmpty && d.allSatisfy { $0.running } }
    var anyDaemonRunning: Bool { daemonServices.contains { $0.running } }

    /// App version from the bundle (falls back to "dev" under `swift run`).
    var appVersion: String { (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "dev" }

    /// BHServe-managed tool sites — not real websites, hidden from the site lists.
    static let systemSites: Set<String> = ["phpmyadmin", "adminer", "mailpit"]
    var realSites: [Site] { (snapshot?.sites ?? []).filter { !AppState.systemSites.contains($0.name) } }
    /// Sites that are actually serving right now — Node apps running, PHP/others enabled
    /// (the green-dot ones). Used for the menu-bar quick-open list.
    var activeSites: [Site] { realSites.filter { $0.node ? $0.nodeRunning : $0.enabled } }

    var nginxRunning: Bool { snapshot?.services.contains { $0.key == "nginx" && $0.running } ?? false }
    func serviceRunning(_ key: String) -> Bool { snapshot?.services.contains { $0.key == key && $0.running } ?? false }

    /// A tool is "active" only when it's actually reachable RIGHT NOW: nginx up + its
    /// site enabled, and (for mailpit, which has a daemon) the mailpit service running.
    /// The menu bar shows only active tools — a stopped/disabled tool drops off.
    func toolActive(_ name: String) -> Bool {
        guard nginxRunning,
              let site = snapshot?.sites.first(where: { $0.name == name }), site.enabled
        else { return false }
        if name == "mailpit" { return serviceRunning("mailpit") }   // mailpit needs its daemon
        return true                                                 // pma/adminer are static
    }

    /// On/off state of a tool (drives the Web-tools toggle).
    func toolEnabled(_ name: String) -> Bool {
        if name == "mailpit" { return serviceRunning("mailpit") }
        return snapshot?.sites.first(where: { $0.name == name })?.enabled ?? false
    }

    /// Turn a tool on/off. Mailpit = start/stop its daemon; phpMyAdmin/Adminer (no
    /// daemon) = enable/disable their site so nginx stops/starts serving them.
    func setToolEnabled(_ name: String, _ on: Bool) async {
        if name == "mailpit" {
            await control(on ? "start" : "stop", "mailpit")
        } else {
            await setSiteEnabled(name, on)
        }
    }
    func openTool(_ name: String) {
        let tld = snapshot?.config.tld ?? "test"
        if let u = URL(string: "http://\(name).\(tld)") { NSWorkspace.shared.open(u) }
    }

    func services(role: ServiceRole) -> [Service] {
        snapshot?.services.filter { $0.role == role.rawValue } ?? []
    }

    /// PHP versions available to assign to a site.
    var phpChoices: [String] {
        services(role: .php).filter { $0.installed }.map { $0.key }
    }

    private var didInit = false

    func reload() async {
        let eng = engine
        // make sure ~/.bhserve exists (idempotent; needs no Homebrew) so `api` works
        if !didInit { didInit = true; _ = try? await Task.detached { try eng.run(["init"]) }.value }
        do {
            let snap = try await Task.detached { try eng.snapshot() }.value
            if snap != snapshot { snapshot = snap }   // avoid needless re-render (keeps TextField focus steady)
            errorText = nil
        } catch {
            errorText = error.localizedDescription
        }
    }

    // ── first-run onboarding ────────────────────────────────────────────────
    var brewInstalled: Bool {
        if let b = snapshot?.brew { return b }
        return FileManager.default.isExecutableFile(atPath: "/opt/homebrew/bin/brew")
            || FileManager.default.isExecutableFile(atPath: "/usr/local/bin/brew")
    }
    var coreInstalled: Bool { snapshot?.services.contains { $0.key == "nginx" && $0.installed } ?? false }
    /// Only prompt first-run setup once we've actually loaded state AND it's truly
    /// empty (no Homebrew or no nginx). While snapshot is nil we return false so the
    /// Welcome screen never flashes for an existing, working install.
    var needsSetup: Bool {
        guard let snap = snapshot else { return false }
        let brew = snap.brew ?? brewInstalled
        let core = snap.services.contains { $0.key == "nginx" && $0.installed }
        return !brew || !core
    }

    /// Open Terminal and run the official Homebrew installer (interactive — handles
    /// sudo + Command Line Tools with full visibility).
    func openHomebrewInstaller() {
        let cmd = #"/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)""#
        let esc = cmd.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let osa = "tell application \"Terminal\"\nactivate\ndo script \"\(esc)\"\nend tell"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", osa]
        try? p.run()
    }

    func installCoreServices() async {
        await runUser(["bootstrap"], note: "installing core services (this can take a few minutes)…")
    }

    /// start/stop/restart a service (or "all"). nginx/all/dns need root.
    func control(_ action: String, _ target: String) async {
        guard !busy else { return }
        busy = true
        lastAction = "\(action) \(target)…"
        defer { busy = false; lastAction = nil }
        let eng = engine
        // dnsmasq needs root (binds :53 + writes /etc/resolver) and the helper only
        // covers nginx — so DNS ALWAYS prompts (osascript admin). nginx/all need root
        // for :80/:443 but go password-less once the helper is installed.
        let dnsLike = (target == "dnsmasq" || target == "dns")
        let needsPrompt = dnsLike || ((target == "nginx" || target == "all") && !helperInstalled)
        do {
            try await Task.detached {
                if needsPrompt { try eng.runPrivileged([action, target]) }
                else { _ = try eng.run([action, target]) }
            }.value
            await reload()
        } catch {
            errorText = error.localizedDescription
        }
    }

    var helperInstalled: Bool { snapshot?.helper ?? false }

    func installHelper() async {
        guard !busy else { return }
        busy = true; lastAction = "installing helper…"; defer { busy = false; lastAction = nil }
        let eng = engine
        do { try await Task.detached { try eng.runPrivileged(["helper", "install"]) }.value; await reload() }
        catch { errorText = error.localizedDescription }
    }

    func uninstallHelper() async {
        guard !busy else { return }
        busy = true; lastAction = "removing helper…"; defer { busy = false; lastAction = nil }
        let eng = engine
        do { try await Task.detached { try eng.runPrivileged(["helper", "uninstall"]) }.value; await reload() }
        catch { errorText = error.localizedDescription }
    }

    var httpdInstalled: Bool { snapshot?.services.contains { $0.key == "httpd" && $0.installed } ?? false }

    func serviceInstalled(_ key: String) -> Bool { snapshot?.services.contains { $0.key == key && $0.installed } ?? false }

    var mysqlRunning: Bool {
        snapshot?.services.contains { ($0.key == "mariadb" || $0.key == "mysql") && $0.running } ?? false
    }
    var pgRunning: Bool {
        snapshot?.services.contains { $0.key == "postgresql@17" && $0.running } ?? false
    }
    /// The installed MySQL-family service key (MariaDB or MySQL), or "mariadb" as the
    /// default install target when neither is installed yet.
    var mysqlServiceKey: String {
        serviceInstalled("mariadb") ? "mariadb" : (serviceInstalled("mysql") ? "mysql" : "mariadb")
    }
    var mysqlLabel: String { mysqlServiceKey == "mysql" ? "MySQL" : "MariaDB" }
    var mysqlInstalled: Bool { serviceInstalled("mariadb") || serviceInstalled("mysql") }
    var pgInstalled: Bool { serviceInstalled("postgresql@17") }

    /// Engine choices for the "Create database" picker — only the installed engines.
    var dbEngineOptions: [(tag: String, label: String)] {
        var opts: [(String, String)] = []
        if mysqlInstalled { opts.append(("mysql", mysqlLabel)) }
        if pgInstalled { opts.append(("pg", "PostgreSQL")) }
        return opts
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

    // ── node (fnm) ────────────────────────────────────────────────────────
    var nodeVersions: [NodeVersion] = []
    var fnmInstalled: Bool { snapshot?.services.contains { $0.key == "fnm" && $0.installed } ?? false }

    func reloadNode() async {
        guard fnmInstalled else { nodeVersions = []; return }
        let eng = engine
        if let j = try? await Task.detached(operation: { try eng.run(["node", "list", "--json"]) }).value {
            nodeVersions = (try? JSONDecoder().decode([NodeVersion].self, from: Data(j.utf8))) ?? []
        }
    }

    func installNode(_ v: String) async {
        let clean = v.trimmingCharacters(in: .whitespaces)
        guard !clean.isEmpty else { return }
        await runUser(["node", "install", clean], note: "installing Node \(clean)… (downloading)")
        await reloadNode()
    }

    func useNode(_ v: String) async {
        await runUser(["node", "use", v], note: "setting Node \(v) as default…")
        await reloadNode()
    }

    func uninstallNode(_ v: String) async {
        await runUser(["node", "uninstall", v], note: "uninstalling Node \(v)…")
        await reloadNode()
    }

    func setServiceAutoStart(_ key: String, _ on: Bool) async {
        await runUser([on ? "enable" : "disable", key], note: "\(on ? "enabling" : "disabling") auto-start for \(key)…")
    }

    func updateService(_ key: String) async {
        await runUser(["update", key], note: "updating \(key) (brew upgrade)…")
    }
    func uninstallService(_ key: String) async {
        await runUser(["uninstall", key], note: "uninstalling \(key)…")
    }

    // ── php.ini editing ─────────────────────────────────────────────────────
    /// Resolve (creating if needed) the loaded php.ini path for a php version.
    func phpIniPath(_ key: String) async -> String? {
        let eng = engine
        let out = try? await Task.detached(operation: { try eng.run(["php", "ini", "path", key]) }).value
        let p = out?.trimmingCharacters(in: .whitespacesAndNewlines)
        return (p?.isEmpty == false) ? p : nil
    }

    /// Write the edited php.ini, then restart that version's FPM so it takes effect.
    func savePhpIni(_ key: String, path: String, content: String) async {
        guard !busy else { return }
        busy = true
        lastAction = "saving php.ini for \(key)…"
        defer { busy = false; lastAction = nil }
        do {
            try content.write(toFile: path, atomically: true, encoding: .utf8)
            let eng = engine
            try await Task.detached { _ = try eng.run(["php", "ini", "reload", key]) }.value
            await reload()
        } catch {
            errorText = error.localizedDescription
        }
    }

    // ── self-update via GitHub Releases ─────────────────────────────────────
    static let repoSlug = "wpexpertinbd/BHServe"
    enum UpdateStatus: Equatable {
        case idle, checking, working, upToDate
        case available(version: String, pkg: String)
        case failed(String)
    }
    var updateStatus: UpdateStatus = .idle

    /// Persisted "check for updates automatically" preference (default ON). When on,
    /// we run one quiet check at launch so a waiting update is visible without the
    /// user opening Settings (a dot appears on the sidebar's Settings row).
    private static let autoUpdateKey = "autoUpdateCheck"
    var autoUpdateCheckEnabled: Bool = (UserDefaults.standard.object(forKey: AppState.autoUpdateKey) as? Bool) ?? true {
        didSet { UserDefaults.standard.set(autoUpdateCheckEnabled, forKey: AppState.autoUpdateKey) }
    }
    /// True only when a newer version was found — drives the sidebar badge.
    var updateAvailable: Bool { if case .available = updateStatus { return true }; return false }

    /// Drives the proactive "Update now / Later" alert (once per session). A check that
    /// finds an update sets this; ContentView shows the alert when a window is on screen.
    var pendingUpdatePrompt = false
    private var updatePromptedThisSession = false
    /// Called when a check finds a newer version: surface it proactively (alert when a
    /// window is up; a system notification when the app launched hidden in the menu bar).
    private func announceUpdate(_ version: String) {
        guard !updatePromptedThisSession else { return }
        updatePromptedThisSession = true
        pendingUpdatePrompt = true
        // No visible window (background/login launch) → post a notification so the user knows.
        if NSApp.windows.allSatisfy({ !$0.isVisible }) {
            guard Bundle.main.bundleIdentifier != nil else { return }   // skip under `swift run`
            let c = UNUserNotificationCenter.current()
            c.requestAuthorization(options: [.alert, .sound]) { granted, _ in
                guard granted else { return }
                let content = UNMutableNotificationContent()
                content.title = "BHServe update available"
                content.body = "Version \(version) is ready — open BHServe to update."
                c.add(UNNotificationRequest(identifier: "bhserve-update", content: content, trigger: nil))
            }
        }
    }

    // ── site-list page-size preferences (persisted; each list's "Show" menu can override) ──
    private static let homePerPageKey = "homeSitesPerPage"
    private static let sidebarPerPageKey = "sidebarSitesPerPage"
    /// Default sites-per-page on the Dashboard websites panel (default 10).
    var homeSitesPerPage: Int = (UserDefaults.standard.object(forKey: AppState.homePerPageKey) as? Int) ?? 10 {
        didSet { UserDefaults.standard.set(max(1, homeSitesPerPage), forKey: AppState.homePerPageKey) }
    }
    /// Default sites-per-page on the Sites tab (default 15).
    var sidebarSitesPerPage: Int = (UserDefaults.standard.object(forKey: AppState.sidebarPerPageKey) as? Int) ?? 15 {
        didSet { UserDefaults.standard.set(max(1, sidebarSitesPerPage), forKey: AppState.sidebarPerPageKey) }
    }

    struct Release: Decodable {
        let tagName: String, htmlURL: String, assets: [Asset]
        struct Asset: Decodable { let name: String; let url: String
            enum CodingKeys: String, CodingKey { case name; case url = "browser_download_url" } }
        enum CodingKeys: String, CodingKey { case tagName = "tag_name"; case htmlURL = "html_url"; case assets }
    }

    /// `auto` = the silent launch check: don't surface a "checking…" spinner or a
    /// scary "check failed" (offline at login is normal) — only promote to
    /// `.available`, otherwise leave the status untouched.
    func checkForUpdate(auto: Bool = false) async {
        if !auto { updateStatus = .checking }
        guard let url = URL(string: "https://api.github.com/repos/\(AppState.repoSlug)/releases/latest") else {
            if !auto { updateStatus = .failed("bad URL") }; return
        }
        do {
            var req = URLRequest(url: url)
            req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            let (data, resp) = try await URLSession.shared.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard code == 200 else {
                // Don't claim "up to date" on a failed check — that hides the real state.
                if !auto { updateStatus = .failed(code == 403
                    ? "GitHub rate-limited the check — try again in a few minutes"
                    : "couldn't reach GitHub (HTTP \(code))") }
                return
            }
            let rel = try JSONDecoder().decode(Release.self, from: data)
            let latest = rel.tagName.trimmingCharacters(in: CharacterSet(charactersIn: "vV "))
            if AppState.isNewer(latest, than: appVersion),
               let pkg = rel.assets.first(where: { $0.name.hasSuffix(".pkg") })?.url {
                updateStatus = .available(version: latest, pkg: pkg)
                if auto { announceUpdate(latest) }   // proactive prompt only for background/launch checks
            } else if !auto {
                updateStatus = .upToDate
            }
        } catch {
            if !auto { updateStatus = .failed(error.localizedDescription) }
        }
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    /// Download the .pkg, launch the macOS Installer, then quit so it can replace
    /// /Applications/BHServe.app. User clicks through (one password) and reopens.
    func downloadAndInstall(_ pkgURL: String) async {
        updateStatus = .working
        guard let url = URL(string: pkgURL) else { updateStatus = .failed("bad asset URL"); return }
        do {
            let (tmp, _) = try await URLSession.shared.download(from: url)
            let dest = FileManager.default.temporaryDirectory.appendingPathComponent("BHServe-update.pkg")
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: tmp, to: dest)
            NSWorkspace.shared.open(dest)
            try? await Task.sleep(for: .seconds(1))
            NSApp.terminate(nil)
        } catch {
            updateStatus = .failed(error.localizedDescription)
        }
    }

    // ── Add-site requirement guard (don't let users create a dead site) ──────
    struct SiteRequirement: Identifiable, Equatable { let key: String; let label: String; var id: String { key } }

    /// Services a site of this type needs installed AND running (else it 502s / won't serve).
    func siteRequirements(type: String, php: String, server: String) -> [SiteRequirement] {
        var reqs: [SiteRequirement] = []
        switch type {
        case "node":
            reqs.append(.init(key: "nginx", label: "nginx"))
            reqs.append(.init(key: "fnm", label: "Node (fnm)"))
        case "python":
            reqs.append(.init(key: "nginx", label: "nginx"))
            reqs.append(.init(key: "python", label: "Python"))
        default:
            if server == "apache" { reqs.append(.init(key: "httpd", label: "Apache")) }
            else { reqs.append(.init(key: "nginx", label: "nginx")) }
            if type == "php" || type == "wordpress" {
                reqs.append(.init(key: php, label: "PHP \(php.replacingOccurrences(of: "php@", with: ""))"))
                reqs.append(.init(key: mysqlServiceKey, label: mysqlLabel))
            }
        }
        return reqs
    }
    /// Required services that are not installed OR not running.
    func missingForSite(type: String, php: String, server: String) -> [SiteRequirement] {
        siteRequirements(type: type, php: php, server: server)
            .filter { !serviceInstalled($0.key) || !serviceRunning($0.key) }
    }
    /// Install any missing required services, then start any that aren't running (idempotent).
    func ensureSiteServices(type: String, php: String, server: String) async {
        let reqs = siteRequirements(type: type, php: php, server: server)
        for r in reqs where !serviceInstalled(r.key) {
            await runUser(["install", r.key], note: "installing \(r.label)…")
        }
        // fnm + python are tools (active once installed), not startable daemons — skip starting them.
        for r in reqs where r.key != "fnm" && r.key != "python" && !serviceRunning(r.key) {
            await control("start", r.key)
        }
    }

    func installService(_ key: String) async {
        let (ok, steps) = await runCapturing(["install", key], note: "installing \(key)…")
        actionResult = ActionResult(title: ok ? "\(key) installed" : "Couldn't install \(key)",
                                    success: ok, steps: steps, url: nil)
    }

    func addSite(name: String, php: String, server: String = "nginx", type: String = "php",
                 root: String? = nil, https: Bool = true) async {
        let clean = name.trimmingCharacters(in: .whitespaces)
        guard !clean.isEmpty else { return }
        var args = ["site", "add", clean, "--php", php, "--server", server, "--type", type]
        if let r = root?.trimmingCharacters(in: .whitespaces), !r.isEmpty { args += ["--root", r] }
        let note = type == "wordpress" ? "creating \(clean) + downloading WordPress…" : "adding \(clean)…"
        let tld = snapshot?.config.tld ?? "test"
        var (ok, steps) = await runCapturing(args, note: note)
        // Best-effort HTTPS: issue a trusted cert + re-render the vhost BEFORE the single
        // nginx restart below. A cert failure (e.g. mkcert missing) must NOT fail the add —
        // the site stays added over http.
        if ok && https {
            let (_, ssteps) = await runCapturing(["secure", "\(clean).\(tld)"], note: "enabling HTTPS for \(clean)…")
            steps += ssteps
        }
        await control("restart", "nginx")   // ONE restart loads the new vhost (+ its SSL)
        let secure = snapshot?.sites.first { $0.name == clean }?.secure ?? false
        actionResult = ActionResult(title: ok ? "Site ‘\(clean)’ added" : "Couldn't add ‘\(clean)’",
                                    success: ok, steps: steps,
                                    url: ok ? "\(secure ? "https" : "http")://\(clean).\(tld)" : nil)
    }

    func setSitePhp(_ name: String, _ php: String) async {
        await runUser(["site", "php", name, php], note: "switching \(name) to \(php)…")
        await control("restart", "nginx")
    }

    // ── Cloudflare quick tunnels (share a site publicly) ────────────────────
    var cloudflaredInstalled: Bool { snapshot?.cloudflared ?? false }
    func tunnelURL(_ name: String) -> String? {
        let t = snapshot?.sites.first { $0.name == name }?.tunnel
        return (t?.isEmpty == false) ? t : nil
    }
    func installCloudflared() async { await runUser(["tunnel", "install"], note: "installing cloudflared…") }
    func startTunnel(_ name: String) async { await runUser(["tunnel", "start", name], note: "starting tunnel for \(name)…") }
    func stopTunnel(_ name: String) async { await runUser(["tunnel", "stop", name], note: "stopping tunnel…") }

    func setSiteRoot(_ name: String, _ path: String) async {
        let p = path.trimmingCharacters(in: .whitespaces)
        guard !p.isEmpty else { return }
        await runUser(["site", "root", name, p], note: "changing \(name) root…")
        await control("restart", "nginx")
    }

    func restartAll() async { await control("restart", "all") }

    func removeSite(_ name: String, purge: Bool = false) async {
        var args = ["site", "rm", name]
        if purge { args.append("--purge") }
        await runUser(args, note: purge ? "removing \(name) + files + database…" : "removing \(name)…")
        await control("restart", "nginx")  // drop the vhost from the running server
    }

    func setSiteEnabled(_ name: String, _ enabled: Bool) async {
        await runUser(["site", enabled ? "enable" : "disable", name],
                      note: "\(enabled ? "starting" : "stopping") \(name)…")
        await control("restart", "nginx")
    }

    // ── Node sites (managed frontend + optional backend) ─────────────────────
    /// Add a Node site: frontend (dir/cmd/port) + optional backend (dir/cmd/port).
    func addNodeSite(name: String, feDir: String, feCmd: String, fePort: String,
                     beDir: String, beCmd: String, bePort: String,
                     apiPaths: String, start: Bool = true) async {
        let clean = name.trimmingCharacters(in: .whitespaces).lowercased()
        guard !clean.isEmpty, !feDir.isEmpty, !fePort.isEmpty else { return }
        var args = ["nodesite", "add", clean,
                    "--fe-dir", feDir, "--fe-cmd", feCmd, "--fe-port", fePort]
        if !beDir.trimmingCharacters(in: .whitespaces).isEmpty {
            args += ["--be-dir", beDir, "--be-cmd", beCmd, "--be-port", bePort]
        }
        if !apiPaths.trimmingCharacters(in: .whitespaces).isEmpty { args += ["--api-paths", apiPaths] }
        await runUser(args, note: "adding Node site \(clean)…")
        if start { await nodeStart(clean) }
    }

    func nodeStart(_ name: String)   async { await runUser(["nodesite", "start", name],   note: "starting \(name)…") }
    func nodeStop(_ name: String)    async { await runUser(["nodesite", "stop", name],    note: "stopping \(name)…") }
    func nodeRestart(_ name: String) async { await runUser(["nodesite", "restart", name], note: "restarting \(name)…") }
    func removeNodeSite(_ name: String) async {
        await runUser(["nodesite", "rm", name], note: "removing \(name)…")
        await control("restart", "nginx")
    }
    /// Run `npm install` for a site's frontend or backend (output goes to the action log).
    func nodeNpmInstall(_ name: String, _ part: String) async {
        await runUser(["nodesite", "npm", name, part], note: "npm install — \(name)/\(part)… (can take a minute)")
    }

    // ── Python apps (Flask / Django / FastAPI / …) ───────────────────────────
    func addPySite(name: String, dir: String, port: String, cmd: String,
                   venv: Bool, python: String = "3.13", start: Bool = true) async {
        let clean = name.trimmingCharacters(in: .whitespaces).lowercased()
        guard !clean.isEmpty, !dir.isEmpty, !port.isEmpty else { return }
        let runCmd = cmd.trimmingCharacters(in: .whitespaces).isEmpty ? "python app.py" : cmd
        let args = ["pysite", "add", clean,
                    "--dir", dir, "--port", port, "--cmd", runCmd,
                    "--venv", venv ? "yes" : "no", "--python", python]
        await runUser(args, note: "adding Python app \(clean)…")
        if start { await pyStart(clean) }
    }

    func pyStart(_ name: String)   async { await runUser(["pysite", "start", name],   note: "starting \(name)…") }
    func pyStop(_ name: String)    async { await runUser(["pysite", "stop", name],    note: "stopping \(name)…") }
    func pyRestart(_ name: String) async { await runUser(["pysite", "restart", name], note: "restarting \(name)…") }
    func removePySite(_ name: String) async {
        await runUser(["pysite", "rm", name], note: "removing \(name)…")
        await control("restart", "nginx")
    }
    /// Install requirements.txt into the app's virtualenv (output goes to the action log).
    func pyPip(_ name: String) async {
        await runUser(["pysite", "pip", name], note: "pip install — \(name)… (can take a minute)")
    }

    /// Path to a Node app's `.env` (or null if the dir is empty).
    func envPath(_ dir: String?) -> String? {
        guard let d = dir, !d.isEmpty else { return nil }
        return d + "/.env"
    }
    /// Save an edited `.env`, then restart the site so the new values (e.g. ports) take effect.
    func saveEnv(siteName: String, path: String, content: String) async {
        guard !busy else { return }
        busy = true; lastAction = "saving .env for \(siteName)…"; defer { busy = false; lastAction = nil }
        do {
            try content.write(toFile: path, atomically: true, encoding: .utf8)
            let eng = engine
            try await Task.detached { _ = try eng.run(["nodesite", "restart", siteName]) }.value
            await reload()
        } catch { errorText = error.localizedDescription }
    }

    /// Reveal a path in Finder (view-level convenience lives here for reuse).
    func openInFinder(_ path: String) {
        NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: path)
    }

    /// Auto-detect an installed code editor and open the site folder in it.
    /// Order: VS Code → Cursor → Sublime Text → PhpStorm; falls back to Finder.
    /// Returns the name of the editor used (nil = none found, opened in Finder).
    @discardableResult
    func openInEditor(_ path: String) -> String? {
        let candidates: [(app: String, label: String)] = [
            ("Visual Studio Code", "VS Code"),
            ("Cursor",             "Cursor"),
            ("Sublime Text",       "Sublime Text"),
            ("PhpStorm",           "PhpStorm"),
        ]
        let fm = FileManager.default
        for c in candidates {
            // NSWorkspace finds the app whether it's in /Applications or ~/Applications.
            if NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleId(for: c.app)) != nil
                || fm.fileExists(atPath: "/Applications/\(c.app).app")
                || fm.fileExists(atPath: "\(NSHomeDirectory())/Applications/\(c.app).app") {
                _ = runOpen(["-a", c.app, path])
                return c.label
            }
        }
        // No known editor — reveal in Finder so the user can drag it into theirs.
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
        return nil
    }

    /// Open a terminal at the site folder — iTerm if installed, else Terminal.app.
    func openTerminal(_ path: String) {
        let term = FileManager.default.fileExists(atPath: "/Applications/iTerm.app") ? "iTerm" : "Terminal"
        _ = runOpen(["-a", term, path])
    }

    private func bundleId(for app: String) -> String {
        switch app {
        case "Visual Studio Code": return "com.microsoft.VSCode"
        case "Cursor":             return "com.todesktop.230313mzl4w4u92"  // Cursor
        case "Sublime Text":       return "com.sublimetext.4"
        case "PhpStorm":           return "com.jetbrains.PhpStorm"
        default:                   return ""
        }
    }

    @discardableResult
    private func runOpen(_ args: [String]) -> Bool {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = args
        do { try p.run(); return true } catch { return false }
    }

    func setSitePHP(_ name: String, php: String) async {
        await runUser(["site", "php", name, php], note: "switching \(name) → \(php)…")
        await control("restart", "nginx")  // re-rendered vhost needs a reload
    }

    func setSiteServer(_ name: String, _ server: String) async {
        await runUser(["site", "server", name, server], note: "switching \(name) → \(server)…")
        await control("restart", "nginx")
    }

    // ── startup: login item (SMAppService) + autostart-on-launch ────────────
    var autostartEnabled: Bool { snapshot?.config.autostart ?? false }

    // Start-at-login is a plain LaunchAgent managed by the engine (no SMAppService —
    // see retireOldSMAgents). It launches the app via `open --args --background`.
    var loginItemEnabled: Bool { snapshot?.loginitem ?? false }

    func setLoginItem(_ on: Bool) async {
        await runUser(["loginitem", on ? "enable" : "disable"],
                      note: on ? "enabling start-at-login…" : "disabling start-at-login…")
    }

    func setAutostart(_ on: Bool) async {
        await runUser(["config", "set", "autostart", on ? "true" : "false"], note: "saving startup setting…")
    }

    // ── web tools (phpMyAdmin / Adminer / Mailpit) ──────────────────────────
    func siteExists(_ name: String) -> Bool { snapshot?.sites.contains { $0.name == name } ?? false }
    func installPma() async { await runUser(["pma", "install"], note: "installing phpMyAdmin…"); await control("restart", "nginx") }
    func installAdminer() async { await runUser(["adminer", "install"], note: "installing Adminer…"); await control("restart", "nginx") }
    func setupMailpit() async { await runUser(["mailpit", "setup"], note: "setting up Mailpit…"); await control("restart", "nginx") }

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

    /// Like runUser but captures the engine output and returns (ok, parsed steps) for a result sheet.
    private func runCapturing(_ args: [String], note: String) async -> (ok: Bool, steps: [ActionResult.Step]) {
        guard !busy else { return (false, []) }
        busy = true; lastAction = note; defer { busy = false; lastAction = nil }
        let eng = engine
        do {
            let out = try await Task.detached { try eng.run(args) }.value
            await reload()
            return (true, AppState.parseSteps(out))
        } catch {
            let msg = (error as? EngineError)?.message ?? error.localizedDescription
            return (false, AppState.parseSteps(msg))
        }
    }
}
