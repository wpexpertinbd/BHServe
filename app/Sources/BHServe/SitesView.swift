import SwiftUI

struct SitesView: View {
    @Environment(AppState.self) private var state
    @State private var showingAdd = false

    var body: some View {
        Group {
            if let sites = state.snapshot?.sites, !sites.isEmpty {
                List {
                    ForEach(sites) { SiteRow(site: $0) }
                }
            } else {
                ContentUnavailableView {
                    Label("No sites yet", systemImage: "globe")
                } description: {
                    Text("Add a site to serve it at name.\(state.snapshot?.config.tld ?? "test").")
                } actions: {
                    Button("Add Site") { showingAdd = true }
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

struct SiteRow: View {
    @Environment(AppState.self) private var state
    let site: Site

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: site.secure ? "lock.fill" : "globe")
                .foregroundStyle(site.secure ? .green : .secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(site.domain).font(.body.weight(.medium))
                Text(site.root)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            Menu {
                ForEach(state.phpChoices, id: \.self) { choice in
                    Button {
                        if choice != site.php { Task { await state.setSitePHP(site.name, php: choice) } }
                    } label: {
                        Label(choice, systemImage: choice == site.php ? "checkmark" : "")
                    }
                }
            } label: {
                Text(site.php).font(.caption.monospaced())
            }
            .menuStyle(.borderlessButton).fixedSize()
            .help("Switch PHP version")

            if let url = site.url {
                Button {
                    NSWorkspace.shared.open(url)
                } label: { Image(systemName: "arrow.up.right.square") }
                .buttonStyle(.borderless).help("Open \(url.absoluteString)")
            }
            if !site.secure {
                Button("Secure") { Task { await state.secure(domain: site.domain) } }
                    .controlSize(.small)
            }
            Menu {
                Button(role: .destructive) {
                    Task { await state.removeSite(site.name) }
                } label: { Label("Remove site", systemImage: "trash") }
            } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton).fixedSize()
        }
        .padding(.vertical, 4)
        .disabled(state.busy)
    }
}

struct AddSiteSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var php = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Add Site").font(.title2.bold())
            HStack {
                TextField("name", text: $name)
                    .textFieldStyle(.roundedBorder)
                Text(".\(state.snapshot?.config.tld ?? "test")")
                    .foregroundStyle(.secondary)
            }
            Picker("PHP version", selection: $php) {
                ForEach(state.phpChoices, id: \.self) { Text($0).tag($0) }
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
                    Task { await state.addSite(name: cleanName, php: php); dismiss() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(cleanName.isEmpty || php.isEmpty)
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
