import SwiftUI
import AppKit

/// Dashboard websites list (ServBay-style) with per-site actions.
struct WebsitesPanel: View {
    @Environment(AppState.self) private var state
    @State private var showingAdd = false
    @State private var query = ""
    @State private var page = 0
    @State private var perPage = 10            // overwritten from Settings on appear
    @FocusState private var searchFocused: Bool

    private var allCount: Int { state.realSites.count }

    private var sites: [Site] {
        let all = state.realSites   // hide phpMyAdmin/Adminer/Mailpit (managed tools)
        guard !query.isEmpty else { return all }
        return all.filter { $0.name.localizedCaseInsensitiveContains(query) || $0.domain.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Websites").font(.headline)
                Text("\(allCount)").font(.caption).foregroundStyle(.secondary)
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(.quaternary, in: Capsule())
                Spacer()
                PerPagePicker(size: $perPage, settingsDefault: state.homeSitesPerPage)
                TextField("Search", text: $query)
                    .textFieldStyle(.roundedBorder).frame(width: 180)
                    .focused($searchFocused)
                Button { showingAdd = true } label: { Image(systemName: "plus") }
                    .help("Add site")
            }
            .padding(.bottom, 8)
            .defaultFocus($searchFocused, false)   // don't auto-grab focus on open

            if sites.isEmpty {
                Text(query.isEmpty ? "No sites yet — add one." : "No sites match “\(query)”.")
                    .font(.callout).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 16)
            } else {
                VStack(spacing: 0) {
                    ForEach(SitePaging.page(sites, page, size: perPage)) { WebsiteRow(site: $0) }
                }
                .background(.quaternary.opacity(0.4))
                .clipShape(RoundedRectangle(cornerRadius: 10))
                PageBar(page: $page, pageCount: SitePaging.pageCount(sites.count, size: perPage))
                    .padding(.top, 10)
            }
        }
        .padding(16)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 14))
        .sheet(isPresented: $showingAdd) { AddSiteSheet() }
        .onAppear { perPage = state.homeSitesPerPage }
        .onChange(of: query) { page = 0 }
        .onChange(of: perPage) { page = 0 }
        .onChange(of: sites.count) {
            let last = SitePaging.pageCount(sites.count, size: perPage) - 1
            if page > last { page = max(0, last) }
        }
    }
}

struct ToolsPanel: View {
    @Environment(AppState.self) private var state
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Web tools").font(.headline)
            HStack(spacing: 10) {
                ToolButton(name: "phpMyAdmin", site: "phpmyadmin", icon: "cylinder.split.1x2") { await state.installPma() }
                ToolButton(name: "Adminer", site: "adminer", icon: "tablecells") { await state.installAdminer() }
                ToolButton(name: "Mailpit", site: "mailpit", icon: "envelope") { await state.setupMailpit() }
            }
        }
        .padding(16)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 14))
    }
}

struct ToolButton: View {
    @Environment(AppState.self) private var state
    let name: String, site: String, icon: String
    let install: () async -> Void

    var body: some View {
        let exists = state.siteExists(site)
        let on = state.toolEnabled(site)
        let tld = state.snapshot?.config.tld ?? "test"
        VStack(spacing: 8) {
            Image(systemName: icon).font(.title2)
                .foregroundStyle(exists && on ? .blue : .secondary)
            Text(name).font(.subheadline.weight(.medium))
            if exists {
                Toggle("", isOn: Binding(
                    get: { on },
                    set: { v in Task { await state.setToolEnabled(site, v) } }
                ))
                .toggleStyle(.switch).controlSize(.mini).labelsHidden()
                Text(on ? "Active" : "Off")
                    .font(.caption2).foregroundStyle(on ? .green : .secondary)
                Button("Open") {
                    if let u = URL(string: "http://\(site).\(tld)") { NSWorkspace.shared.open(u) }
                }.controlSize(.small).disabled(!on)
            } else {
                Button("Install") { Task { await install() } }.controlSize(.small).tint(.green)
            }
        }
        .frame(maxWidth: .infinity).padding(12)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
        .disabled(state.busy)
    }
}

struct WebsiteRow: View {
    @Environment(AppState.self) private var state
    let site: Site
    @State private var editing = false
    @State private var showingLogs = false
    @State private var confirmDelete = false
    @State private var sharing = false
    @State private var editingNode = false
    @State private var envPart: String?       // "fe" | "be" when editing .env
    @State private var showingNodeLogs = false

    private var runDot: Bool { site.node ? site.nodeRunning : site.enabled }

    var body: some View {
        HStack(spacing: 10) {
            Circle().fill(runDot ? Color.green : Color.secondary.opacity(0.4)).frame(width: 9, height: 9)
            VStack(alignment: .leading, spacing: 1) {
                Text(site.name).font(.body.weight(.medium))
                Text(site.domain).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            // server badge
            Text(site.serverKind).font(.caption2.weight(.medium))
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(badgeBg, in: Capsule())
                .foregroundStyle(badgeFg)
            // version/port badge
            if site.node {
                let ports = [site.fePort.map { "fe :\($0)" }, site.hasBackend ? site.bePort.map { "be :\($0)" } : nil]
                    .compactMap { $0 }.joined(separator: "  ")
                Text(ports).font(.caption2.monospaced()).foregroundStyle(.secondary)
                    .padding(.horizontal, 6).padding(.vertical, 2).background(.quaternary, in: Capsule())
            } else {
                Text(site.php).font(.caption2.monospaced()).foregroundStyle(.secondary)
                    .padding(.horizontal, 6).padding(.vertical, 2).background(.quaternary, in: Capsule())
            }

            HStack(spacing: 5) { site.node ? AnyView(nodeActions) : AnyView(phpActions) }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 12) }
        .disabled(state.busy)
        .sheet(isPresented: $editing) { EditSiteSheet(site: site) }
        .sheet(isPresented: $editingNode) { EditNodeSheet(site: site) }
        .sheet(isPresented: $showingLogs) { SiteLogsSheet(site: site) }
        .sheet(isPresented: $showingNodeLogs) { NodeLogsSheet(site: site) }
        .sheet(isPresented: $sharing) { ShareSheet(site: site) }
        .sheet(item: Binding(get: { envPart.map { EnvTarget(part: $0) } },
                             set: { envPart = $0?.part })) { t in
            EnvEditorSheet(site: site, part: t.part)
        }
        .confirmationDialog("Remove “\(site.name)”? (project files are kept on disk)",
                            isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Remove \(site.name)", role: .destructive) {
                Task { site.node ? await state.removeNodeSite(site.name) : await state.removeSite(site.name) }
            }
            Button("Cancel", role: .cancel) {}
        }
    }

    private var badgeBg: Color {
        site.node ? Color.green.opacity(0.15) : (site.serverKind == "apache" ? Color.orange.opacity(0.2) : Color.blue.opacity(0.15))
    }
    private var badgeFg: Color {
        site.node ? .green : (site.serverKind == "apache" ? .orange : .blue)
    }

    @ViewBuilder private var phpActions: some View {
        CircleAction("safari", .blue, "Open in browser") { if let u = site.url { NSWorkspace.shared.open(u) } }
        CircleAction("folder", .gray, "Open folder") { state.openInFinder(site.root) }
        CircleAction("pencil", .blue, "Edit") { editing = true }
        CircleAction("doc.text.magnifyingglass", .gray, "Logs") { showingLogs = true }
        CircleAction("antenna.radiowaves.left.and.right",
                     state.tunnelURL(site.name) != nil ? .green : .gray,
                     state.tunnelURL(site.name) != nil ? "Sharing publicly — manage" : "Share publicly (Cloudflare)") { sharing = true }
        if site.enabled {
            CircleAction("pause.fill", .orange, "Stop") { Task { await state.setSiteEnabled(site.name, false) } }
        } else {
            CircleAction("play.fill", .green, "Start") { Task { await state.setSiteEnabled(site.name, true) } }
        }
        CircleAction("trash", .red, "Delete") { confirmDelete = true }
    }

    @ViewBuilder private var nodeActions: some View {
        CircleAction("safari", .blue, "Open in browser") { if let u = site.url { NSWorkspace.shared.open(u) } }
        CircleAction("folder", .gray, "Open frontend folder") { state.openInFinder(site.feDir ?? "") }
        if site.nodeRunning {
            CircleAction("pause.fill", .orange, "Stop") { Task { await state.nodeStop(site.name) } }
        } else {
            CircleAction("play.fill", .green, "Start") { Task { await state.nodeStart(site.name) } }
        }
        CircleAction("arrow.clockwise", .blue, "Restart") { Task { await state.nodeRestart(site.name) } }
        CircleAction("doc.text.magnifyingglass", .gray, "Process logs") { showingNodeLogs = true }
        Menu {
            Button { editingNode = true } label: { Label("Edit config (ports/commands)", systemImage: "slider.horizontal.3") }
            Divider()
            Button { envPart = "fe" } label: { Label("Edit frontend .env", systemImage: "doc.text") }
            Button { Task { await state.nodeNpmInstall(site.name, "fe") } } label: { Label("npm install (frontend)", systemImage: "shippingbox") }
            if site.hasBackend {
                Divider()
                Button { envPart = "be" } label: { Label("Edit backend .env", systemImage: "doc.text") }
                Button { Task { await state.nodeNpmInstall(site.name, "be") } } label: { Label("npm install (backend)", systemImage: "shippingbox") }
            }
        } label: {
            Image(systemName: "ellipsis").font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white).frame(width: 26, height: 26).background(Color.gray, in: Circle())
        }
        .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        CircleAction("trash", .red, "Delete") { confirmDelete = true }
    }
}

struct CircleAction: View {
    let system: String, tint: Color, help: String, action: () -> Void
    init(_ system: String, _ tint: Color, _ help: String, action: @escaping () -> Void) {
        self.system = system; self.tint = tint; self.help = help; self.action = action
    }
    var body: some View {
        Button(action: action) {
            Image(systemName: system)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 26, height: 26)
                .background(tint, in: Circle())
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

struct EditSiteSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site
    @State private var php = ""
    @State private var server = "nginx"
    @State private var root = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Edit \(site.name)").font(.title2.bold())
            Text(site.domain).font(.caption).foregroundStyle(.secondary)
            Picker("PHP version", selection: $php) {
                ForEach(state.phpChoices, id: \.self) { Text($0).tag($0) }
            }
            Picker("Web server", selection: $server) {
                Text("nginx (fast)").tag("nginx")
                Text(state.httpdInstalled ? "Apache (.htaccess)" : "Apache — needs httpd").tag("apache")
            }
            if server == "apache" && !state.httpdInstalled {
                Label("Install httpd in Services first.", systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(.orange)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("Root folder").font(.caption).foregroundStyle(.secondary)
                HStack {
                    TextField("/path/to/site", text: $root).textFieldStyle(.roundedBorder)
                        .font(.caption.monospaced())
                    Button("Choose…") { if let p = bhChooseFolder(start: root) { root = p } }
                }
            }
            HStack {
                if site.secure {
                    Label("HTTPS enabled", systemImage: "lock.fill").foregroundStyle(.green).font(.caption)
                } else {
                    Button { Task { await state.secure(domain: site.domain); dismiss() } } label: {
                        Label("Enable HTTPS", systemImage: "lock")
                    }
                }
                Spacer()
            }
            Divider()
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Apply") {
                    Task {
                        if php != site.php { await state.setSitePHP(site.name, php: php) }
                        if server != site.serverKind && !(server == "apache" && !state.httpdInstalled) {
                            await state.setSiteServer(site.name, server)
                        }
                        let r = root.trimmingCharacters(in: .whitespaces)
                        if r != site.root && r.hasPrefix("/") { await state.setSiteRoot(site.name, r) }
                        dismiss()
                    }
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20).frame(width: 420)
        .onAppear { php = site.php; server = site.serverKind; root = site.root }
    }
}

struct SiteLogsSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site
    @State private var kind = "error"
    @State private var content = "Loading…"

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("\(site.name) logs").font(.headline)
                Spacer()
                Picker("", selection: $kind) {
                    Text("Error").tag("error")
                    Text("Access").tag("access")
                }.pickerStyle(.segmented).labelsHidden().frame(width: 170)
                Button { Task { await load() } } label: { Image(systemName: "arrow.clockwise") }
            }
            ScrollView {
                Text(content).font(.system(.caption, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled).padding(8)
            }
            .frame(height: 320)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            HStack { Spacer(); Button("Close") { dismiss() }.keyboardShortcut(.defaultAction) }
        }
        .padding(16).frame(width: 640)
        .task { await load() }
        .onChange(of: kind) { _, _ in Task { await load() } }
    }

    private func load() async {
        let log = await state.readLog("\(site.name)-\(kind).log")
        content = log.isEmpty ? "(empty — no \(kind) entries yet)" : log
    }
}

/// Share a site publicly through a Cloudflare quick tunnel (no account needed).
struct ShareSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site
    @State private var copied = false

    private var url: String? { state.tunnelURL(site.name) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 8) {
                Image(systemName: "antenna.radiowaves.left.and.right").foregroundStyle(.blue)
                Text("Share “\(site.name)” publicly").font(.title3.bold())
            }
            Text("Cloudflare Tunnel gives this site a temporary public **https** address — no account or port-forwarding. The link works while sharing is on.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)

            if !state.cloudflaredInstalled {
                Label("cloudflared isn't installed yet.", systemImage: "shippingbox")
                    .font(.callout).foregroundStyle(.secondary)
                Button { Task { await state.installCloudflared() } } label: {
                    Label("Install cloudflared (Homebrew)", systemImage: "arrow.down.circle")
                }.tint(.green)
            } else if let u = url {
                Label("Live — anyone with this link can reach your site.", systemImage: "dot.radiowaves.left.and.right")
                    .font(.caption).foregroundStyle(.green)
                HStack {
                    Text(u).font(.callout.monospaced()).textSelection(.enabled)
                        .lineLimit(1).truncationMode(.middle)
                        .padding(8).frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                    Button { copy(u) } label: { Image(systemName: copied ? "checkmark" : "doc.on.doc") }
                        .help("Copy link")
                    Button { if let url = URL(string: u) { NSWorkspace.shared.open(url) } } label: { Image(systemName: "safari") }
                        .help("Open link")
                }
                Button(role: .destructive) { Task { await state.stopTunnel(site.name) } } label: {
                    Label("Stop sharing", systemImage: "stop.circle")
                }
            } else {
                if !state.nginxRunning {
                    Label("Start nginx first — the tunnel forwards to your local server.", systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.orange)
                }
                Button { Task { await state.startTunnel(site.name) } } label: {
                    Label(state.busy ? "Starting…" : "Start sharing", systemImage: "antenna.radiowaves.left.and.right")
                }.tint(.blue).disabled(state.busy || !state.nginxRunning)
            }

            Divider()
            HStack { Spacer(); Button("Close") { dismiss() }.keyboardShortcut(.defaultAction) }
        }
        .padding(20).frame(width: 460)
    }

    private func copy(_ s: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(s, forType: .string)
        copied = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { copied = false }
    }
}
