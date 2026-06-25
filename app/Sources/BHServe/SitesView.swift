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

/// Shared site-list paging (10 per page) for the Sites tab + the Dashboard panel.
enum SitePaging {
    static let pageSize = 10
    static func pageCount(_ count: Int) -> Int { max(1, (count + pageSize - 1) / pageSize) }
    /// The slice of `items` for `page`, clamped so an out-of-range page never crashes.
    static func page<T>(_ items: [T], _ page: Int) -> [T] {
        guard !items.isEmpty else { return [] }
        let p = min(max(0, page), pageCount(items.count) - 1)
        let start = p * pageSize
        return Array(items[start ..< min(start + pageSize, items.count)])
    }
}

/// Prev / "Page X of Y" / Next — only renders when there's more than one page.
struct PageBar: View {
    @Binding var page: Int
    let pageCount: Int
    var body: some View {
        if pageCount > 1 {
            HStack(spacing: 14) {
                Button { if page > 0 { page -= 1 } } label: { Image(systemName: "chevron.left") }
                    .disabled(page <= 0)
                Text("Page \(min(page, pageCount - 1) + 1) of \(pageCount)")
                    .font(.caption).foregroundStyle(.secondary).monospacedDigit()
                Button { if page < pageCount - 1 { page += 1 } } label: { Image(systemName: "chevron.right") }
                    .disabled(page >= pageCount - 1)
            }
            .buttonStyle(.borderless)
            .frame(maxWidth: .infinity)
        }
    }
}

struct SitesView: View {
    @Environment(AppState.self) private var state
    @State private var showingAdd = false
    @State private var query = ""
    @State private var page = 0
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
                                ForEach(SitePaging.page(sites, page)) { WebsiteRow(site: $0) }
                            }
                            .background(.quaternary.opacity(0.4))
                            .clipShape(RoundedRectangle(cornerRadius: 10))
                            .padding([.horizontal], 16)
                        }
                        PageBar(page: $page, pageCount: SitePaging.pageCount(sites.count))
                            .padding(.horizontal, 16).padding(.vertical, 10)
                    }
                }
                .onChange(of: query) { page = 0 }
                .onChange(of: sites.count) {
                    let last = SitePaging.pageCount(sites.count) - 1
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
            }
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
            if !name.isEmpty {
                Label("Will be served at \(scheme)://\(cleanName).\(state.snapshot?.config.tld ?? "test")",
                      systemImage: "info.circle")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Add") {
                    let n = cleanName, p = php, s = server, t = type
                    let r = rootMode == "custom" ? customRoot.trimmingCharacters(in: .whitespaces) : nil
                    Task { await state.addSite(name: n, php: p, server: s, type: t, root: r) }
                    dismiss()   // provisioning (incl. WP download) runs in the background
                }
                .keyboardShortcut(.defaultAction)
                .disabled(cleanName.isEmpty || php.isEmpty
                          || (server == "apache" && !state.httpdInstalled)
                          || (rootMode == "custom" && !customRoot.hasPrefix("/")))
            }
        }
        .padding(20)
        .frame(width: 380)
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
