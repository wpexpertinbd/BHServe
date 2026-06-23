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
            Section("Startup") {
                Toggle("Launch BHServe at login", isOn: Binding(
                    get: { state.loginItemEnabled },
                    set: { state.setLoginItem($0) }))
                Toggle("Start services when BHServe launches", isOn: Binding(
                    get: { state.autostartEnabled },
                    set: { v in Task { await state.setAutostart(v) } }))
                Text("Auto-start runs “Start All” on launch (prompts once for admin to bind :80/:443).")
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
                    TextField("~/Sites", text: $sitesRoot)
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
