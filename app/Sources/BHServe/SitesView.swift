import SwiftUI
import AppKit

/// Native folder chooser — returns the picked absolute path, or nil if cancelled.
func bhChooseFolder(start: String? = nil) -> String? {
    let panel = NSOpenPanel()
    panel.canChooseDirectories = true
    panel.canChooseFiles = false
    panel.canCreateDirectories = true
    panel.allowsMultipleSelection = false
    panel.prompt = "Choose"
    if let s = start, !s.isEmpty { panel.directoryURL = URL(fileURLWithPath: s) }
    return panel.runModal() == .OK ? panel.url?.path : nil
}

/// Shared site-list paging for the Sites tab + the Dashboard panel. `size <= 0` = "show all".
enum SitePaging {
    static func pageCount(_ count: Int, size: Int) -> Int {
        size <= 0 ? 1 : max(1, (count + size - 1) / size)
    }
    /// The slice of `items` for `page`, clamped so an out-of-range page never crashes.
    static func page<T>(_ items: [T], _ page: Int, size: Int) -> [T] {
        guard !items.isEmpty else { return [] }
        if size <= 0 { return items }                       // "All"
        let p = min(max(0, page), pageCount(items.count, size: size) - 1)
        let start = p * size
        return Array(items[start ..< min(start + size, items.count)])
    }
}

/// Top-of-list "Show: 10/15/20/50/100/All" menu. Defaults to the Settings value,
/// which is always included as an option even if it's a custom number.
struct PerPagePicker: View {
    @Binding var size: Int            // 0 = All
    let settingsDefault: Int
    private var options: [Int] {
        Array(Set([10, 15, 20, 50, 100, settingsDefault]).filter { $0 > 0 }).sorted()
    }
    var body: some View {
        Picker("Show", selection: $size) {
            ForEach(options, id: \.self) { Text("\($0)").tag($0) }
            Text("All").tag(0)
        }
        .pickerStyle(.menu).fixedSize()
        .help("How many sites to show per page")
    }
}

/// Prev / "Page X of Y" / Next + a jump-to-page box — only renders when paginated.
struct PageBar: View {
    @Binding var page: Int
    let pageCount: Int
    @State private var jump = ""

    var body: some View {
        if pageCount > 1 {
            HStack(spacing: 12) {
                Button { if page > 0 { page -= 1 } } label: { Image(systemName: "chevron.left") }
                    .buttonStyle(.borderless).disabled(page <= 0)
                Text("Page \(min(page, pageCount - 1) + 1) of \(pageCount)")
                    .font(.caption).foregroundStyle(.secondary).monospacedDigit()
                Button { if page < pageCount - 1 { page += 1 } } label: { Image(systemName: "chevron.right") }
                    .buttonStyle(.borderless).disabled(page >= pageCount - 1)

                Divider().frame(height: 14)
                Text("Go to").font(.caption).foregroundStyle(.secondary)
                TextField("#", text: $jump)
                    .frame(width: 42).textFieldStyle(.roundedBorder).multilineTextAlignment(.center)
                    .onSubmit(go)
                Button("Go", action: go).controlSize(.small).disabled(Int(jump) == nil)
            }
            .frame(maxWidth: .infinity)
        }
    }

    private func go() {
        if let n = Int(jump.trimmingCharacters(in: .whitespaces)), n >= 1 {
            page = min(n, pageCount) - 1     // clamp; convert 1-based input → 0-based
        }
        jump = ""
    }
}

struct SitesView: View {
    @Environment(AppState.self) private var state
    @State private var showingAdd = false
    @State private var query = ""
    @State private var page = 0
    @State private var perPage = 15            // overwritten from Settings on appear
    @FocusState private var searchFocused: Bool

    private var sites: [Site] {
        let all = state.realSites   // your websites (managed tools live in the Web tools card)
        guard !query.isEmpty else { return all }
        return all.filter { $0.name.localizedCaseInsensitiveContains(query)
                          || $0.domain.localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        Group {
            if state.realSites.isEmpty {
                ContentUnavailableView {
                    Label("No sites yet", systemImage: "globe")
                } description: {
                    Text("Add a site to serve it at name.\(state.snapshot?.config.tld ?? "test").")
                } actions: {
                    Button("Add Site") { showingAdd = true }
                }
            } else {
                VStack(alignment: .leading, spacing: 0) {
                    HStack {
                        Text("\(state.realSites.count) site\(state.realSites.count == 1 ? "" : "s")")
                            .font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        PerPagePicker(size: $perPage, settingsDefault: state.sidebarSitesPerPage)
                        TextField("Search", text: $query)
                            .textFieldStyle(.roundedBorder).frame(width: 200)
                            .focused($searchFocused)
                    }
                    .padding([.horizontal, .top], 16).padding(.bottom, 8)
                    .defaultFocus($searchFocused, false)   // don't auto-grab focus on open

                    if sites.isEmpty {
                        Text("No sites match “\(query)”.")
                            .font(.callout).foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(16)
                    } else {
                        ScrollView {
                            VStack(spacing: 0) {
                                ForEach(SitePaging.page(sites, page, size: perPage)) { WebsiteRow(site: $0) }
                            }
                            .background(.quaternary.opacity(0.4))
                            .clipShape(RoundedRectangle(cornerRadius: 10))
                            .padding([.horizontal], 16)
                        }
                        PageBar(page: $page, pageCount: SitePaging.pageCount(sites.count, size: perPage))
                            .padding(.horizontal, 16).padding(.vertical, 10)
                    }
                }
                .onAppear { perPage = state.sidebarSitesPerPage }
                .onChange(of: query) { page = 0 }
                .onChange(of: perPage) { page = 0 }
                .onChange(of: sites.count) {
                    let last = SitePaging.pageCount(sites.count, size: perPage) - 1
                    if page > last { page = max(0, last) }
                }
            }
        }
        .navigationTitle("Sites")
        .toolbar {
            Button { showingAdd = true } label: { Image(systemName: "plus") }
                .help("Add site")
            Button { Task { await state.reload() } } label: { Image(systemName: "arrow.clockwise") }
                .help("Refresh")
        }
        .sheet(isPresented: $showingAdd) { AddSiteSheet() }
    }
}

struct AddSiteSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var php = ""
    @State private var server = "nginx"
    @State private var type = "php"
    @State private var rootMode = "default"   // "default" | "custom"
    @State private var customRoot = ""
    @State private var enableHttps = true     // issue a trusted cert right after adding
    // Node app fields
    @State private var feDir = ""; @State private var feCmd = "npm run dev"; @State private var fePort = ""
    @State private var beDir = ""; @State private var beCmd = ""; @State private var bePort = ""
    @State private var apiPaths = "api|storage|sanctum|admin|livewire|vendor|build|up"

    private var isNode: Bool { type == "node" }
    private var nodeReady: Bool { !feDir.isEmpty && !fePort.isEmpty
        && (beDir.trimmingCharacters(in: .whitespaces).isEmpty || !bePort.isEmpty) }

    private var defaultRoot: String {
        (state.snapshot?.config.sitesRoot ?? "~/BHServe/www") + "/" + (cleanName.isEmpty ? "<name>" : cleanName)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Add Site").font(.title2.bold())
            HStack {
                TextField("name", text: $name)
                    .textFieldStyle(.roundedBorder)
                Text(".\(state.snapshot?.config.tld ?? "test")")
                    .foregroundStyle(.secondary)
            }
            Picker("Type", selection: $type) {
                Text("WordPress").tag("wordpress")
                Text("PHP").tag("php")
                Text("Others (static)").tag("others")
                Text("Node app").tag("node")
            }
            if isNode {
                Text("A managed Node app: a frontend (e.g. Next.js) plus an optional backend/API (e.g. Laravel). BHServe runs both and reverse-proxies them at the domain.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                NodeAppFields(title: "Frontend", dir: $feDir, cmd: $feCmd, port: $fePort)
                NodeAppFields(title: "Backend / API (optional)", dir: $beDir, cmd: $beCmd, port: $bePort)
                if !beDir.trimmingCharacters(in: .whitespaces).isEmpty {
                    LabeledContent("API paths → backend") {
                        TextField("api|storage|…", text: $apiPaths).font(.caption.monospaced())
                    }
                }
            } else {
                Group {
                    switch type {
                    case "wordpress": Text("Creates a database, downloads WordPress, and pre-fills wp-config (DB user root, no password). Just finish the title + admin step.")
                    case "php": Text("Creates a database named after the site (DB user root, no password).")
                    default: Text("Just sets up the domain — no database.")
                    }
                }
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Picker("PHP version", selection: $php) {
                    ForEach(state.phpChoices, id: \.self) { Text($0).tag($0) }
                }
                Picker("Web server", selection: $server) {
                    Text("nginx (fast)").tag("nginx")
                    Text(state.httpdInstalled ? "Apache (.htaccess)" : "Apache — needs httpd").tag("apache")
                }
                if server == "apache" && !state.httpdInstalled {
                    Label("Install httpd in Services to use Apache.", systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.orange)
                }
                Picker("Site root", selection: $rootMode) {
                    Text("Default folder").tag("default")
                    Text("Custom folder…").tag("custom")
                }
                if rootMode == "default" {
                    Text(defaultRoot).font(.caption.monospaced()).foregroundStyle(.secondary)
                        .lineLimit(1).truncationMode(.middle)
                } else {
                    HStack {
                        TextField("/path/to/site", text: $customRoot).textFieldStyle(.roundedBorder)
                        Button("Choose…") { if let p = bhChooseFolder(start: state.snapshot?.config.sitesRoot) { customRoot = p } }
                    }
                }
                Toggle("Enable HTTPS (trusted local certificate)", isOn: $enableHttps)
            }
            if !name.isEmpty {
                Label("Will be served at \((isNode || enableHttps) ? "https" : scheme)://\(cleanName).\(state.snapshot?.config.tld ?? "test")",
                      systemImage: "info.circle")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Add") {
                    if isNode {
                        let n = cleanName
                        Task {
                            await state.addNodeSite(name: n, feDir: feDir, feCmd: feCmd, fePort: fePort,
                                                    beDir: beDir, beCmd: beCmd, bePort: bePort, apiPaths: apiPaths)
                        }
                    } else {
                        let n = cleanName, p = php, s = server, t = type, h = enableHttps
                        let r = rootMode == "custom" ? customRoot.trimmingCharacters(in: .whitespaces) : nil
                        Task { await state.addSite(name: n, php: p, server: s, type: t, root: r, https: h) }
                    }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(cleanName.isEmpty
                          || (isNode ? !nodeReady
                                     : (php.isEmpty || (server == "apache" && !state.httpdInstalled)
                                        || (rootMode == "custom" && !customRoot.hasPrefix("/")))))
            }
        }
        .padding(20)
        .frame(width: isNode ? 460 : 380)
        .onAppear { if php.isEmpty { php = state.snapshot?.config.defaultPhp.hasPrefix("php") == true
            ? state.snapshot!.config.defaultPhp
            : (state.phpChoices.first ?? "php@8.4") } }
    }

    private var cleanName: String {
        name.trimmingCharacters(in: .whitespaces)
            .lowercased()
            .replacingOccurrences(of: " ", with: "-")
    }
    private var scheme: String { "http" }
}
