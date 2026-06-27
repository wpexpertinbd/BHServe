import SwiftUI

struct SettingsView: View {
    @Environment(AppState.self) private var state
    @State private var tld = ""
    @State private var httpPort = ""
    @State private var httpsPort = ""
    @State private var sitesRoot = ""
    @State private var defaultPhp = ""
    @State private var defaultWeb = "nginx"
    @State private var loaded = false
    @State private var confirmUpdate: (version: String, pkg: String)?

    var body: some View {
        Form {
            Section("Domains & ports") {
                LabeledContent("TLD") {
                    HStack(spacing: 4) {
                        Text(".").foregroundStyle(.secondary)
                        TextField("test", text: $tld).frame(width: 120)
                    }
                }
                TextField("HTTP port", text: $httpPort).frame(width: 120)
                TextField("HTTPS port", text: $httpsPort).frame(width: 120)
                Text("Ports below 1024 (80/443) require an admin prompt when nginx restarts.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Updates") {
                LabeledContent("Current version", value: "v\(state.appVersion)")
                Toggle("Automatically check for updates", isOn: Binding(
                    get: { state.autoUpdateCheckEnabled },
                    set: { v in
                        state.autoUpdateCheckEnabled = v
                        if v { Task { await state.checkForUpdate() } }   // re-check immediately when turned on
                    }))
                switch state.updateStatus {
                case .idle:
                    Button { Task { await state.checkForUpdate() } } label: {
                        Label("Check for updates", systemImage: "arrow.triangle.2.circlepath")
                    }
                case .checking, .working:
                    HStack { ProgressView().controlSize(.small)
                        Text(state.updateStatus == .working ? "Downloading…" : "Checking…").foregroundStyle(.secondary) }
                case .upToDate:
                    HStack {
                        Label("You're on the latest version", systemImage: "checkmark.seal.fill").foregroundStyle(.green)
                        Spacer()
                        Button("Check again") { Task { await state.checkForUpdate() } }.controlSize(.small)
                    }
                case .available(let version, let pkg):
                    VStack(alignment: .leading, spacing: 6) {
                        Label("Update available: v\(version)", systemImage: "sparkles").foregroundStyle(.blue)
                        Button {
                            confirmUpdate = (version, pkg)
                        } label: { Label("Download & Install v\(version)", systemImage: "arrow.down.circle.fill") }
                        Text("Downloads the new installer, opens it, and quits BHServe so it can update. Reopen when done.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                case .failed(let msg):
                    HStack {
                        Label("Check failed", systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                        Spacer()
                        Button("Retry") { Task { await state.checkForUpdate() } }.controlSize(.small)
                    }
                    .help(msg)
                }
            }
            Section("Startup") {
                Toggle("Launch BHServe at login", isOn: Binding(
                    get: { state.loginItemEnabled },
                    set: { v in Task { await state.setLoginItem(v) } }))
                Toggle("Start services when BHServe launches", isOn: Binding(
                    get: { state.autostartEnabled },
                    set: { v in Task { await state.setAutostart(v) } }))

                if state.helperInstalled {
                    LabeledContent("Password-less control") {
                        HStack {
                            Label("Enabled", systemImage: "checkmark.seal.fill").foregroundStyle(.green)
                            Button("Remove") { Task { await state.uninstallHelper() } }.controlSize(.small)
                        }
                    }
                } else {
                    Button {
                        Task { await state.installHelper() }
                    } label: {
                        Label("Enable password-less control (one-time)", systemImage: "lock.open")
                    }
                    Text("nginx binds :80/:443 (root), so Start/Stop asks for your password each time. This installs a one-time sudoers rule so it never asks again — and lets BHServe auto-start at login without a prompt.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Section("List sizes") {
                LabeledContent("Sites per page — Dashboard") {
                    TextField("10", value: Binding(
                        get: { state.homeSitesPerPage },
                        set: { state.homeSitesPerPage = max(1, $0) }), format: .number)
                        .frame(width: 70).multilineTextAlignment(.trailing)
                }
                LabeledContent("Sites per page — Sites tab") {
                    TextField("15", value: Binding(
                        get: { state.sidebarSitesPerPage },
                        set: { state.sidebarSitesPerPage = max(1, $0) }), format: .number)
                        .frame(width: 70).multilineTextAlignment(.trailing)
                }
                LabeledContent("Databases per page — Databases tab") {
                    TextField("15", value: Binding(
                        get: { state.dbsPerPage },
                        set: { state.dbsPerPage = max(1, $0) }), format: .number)
                        .frame(width: 70).multilineTextAlignment(.trailing)
                }
                LabeledContent("Apps per page — Node & Python tabs") {
                    TextField("15", value: Binding(
                        get: { state.appsPerPage },
                        set: { state.appsPerPage = max(1, $0) }), format: .number)
                        .frame(width: 70).multilineTextAlignment(.trailing)
                }
                Text("Default page size for the website, database, and app lists. Each list's “Show” menu can override it per view; the footer has prev/next plus a jump-to-page box.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Defaults for new sites") {
                Picker("PHP version", selection: $defaultPhp) {
                    ForEach(state.phpChoices, id: \.self) { Text($0).tag($0) }
                }
                Picker("Web server", selection: $defaultWeb) {
                    Text("nginx").tag("nginx")
                    Text("apache").tag("apache")
                }
                LabeledContent("Sites root") {
                    TextField("~/BHServe/www", text: $sitesRoot)
                }
            }
            Section {
                HStack {
                    if state.busy { ProgressView().controlSize(.small) }
                    Spacer()
                    Button("Revert") { load(force: true) }
                    Button("Save changes") {
                        Task {
                            await state.saveSettings(tld: tld, httpPort: httpPort, httpsPort: httpsPort,
                                                     sitesRoot: sitesRoot, defaultPhp: defaultPhp, defaultWeb: defaultWeb)
                        }
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(state.busy || !dirty)
                }
                if dirty {
                    Text("Changing TLD or ports re-renders all site vhosts and restarts nginx (admin prompt). TLD changes also need HTTPS re-issued per site.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Settings")
        .task { load() }
        .onChange(of: state.snapshot?.config) { _, _ in load() }
        .confirmationDialog("Update BHServe to v\(confirmUpdate?.version ?? "")?",
                            isPresented: Binding(get: { confirmUpdate != nil }, set: { if !$0 { confirmUpdate = nil } }),
                            titleVisibility: .visible) {
            if let u = confirmUpdate {
                Button("Download & Install") { Task { await state.downloadAndInstall(u.pkg) } }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("BHServe will download the installer, open it, and quit so it can update.")
        }
    }

    private var dirty: Bool {
        guard let c = state.snapshot?.config else { return false }
        return tld != c.tld || httpPort != String(c.httpPort) || httpsPort != String(c.httpsPort)
            || sitesRoot != c.sitesRoot || defaultPhp != c.defaultPhp || defaultWeb != c.defaultWeb
    }

    private func load(force: Bool = false) {
        guard let c = state.snapshot?.config else { return }
        guard force || !loaded else { return }
        tld = c.tld; httpPort = String(c.httpPort); httpsPort = String(c.httpsPort)
        sitesRoot = c.sitesRoot; defaultWeb = c.defaultWeb
        defaultPhp = c.defaultPhp.hasPrefix("php") ? c.defaultPhp : "php@\(c.defaultPhp)"  // match picker keys
        loaded = true
    }
}
