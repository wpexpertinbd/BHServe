import SwiftUI

struct ServicesView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 18) {
                ForEach(ServiceRole.allCases, id: \.self) { role in
                    let svcs = state.services(role: role)
                    if !svcs.isEmpty {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(role.title)
                                .font(.headline)
                                .foregroundStyle(.secondary)
                            VStack(spacing: 0) {
                                ForEach(svcs) { ServiceRow(service: $0) }
                            }
                            .background(.quaternary.opacity(0.4))
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        }
                    }
                }
            }
            .padding(20)
        }
        .navigationTitle("Services")
        .toolbar {
            Button { Task { await state.reload() } } label: {
                Image(systemName: "arrow.clockwise")
            }
            .help("Refresh")
        }
    }
}

struct ServiceRow: View {
    @Environment(AppState.self) private var state
    let service: Service

    @State private var confirmUninstall = false
    @State private var editingIni = false

    private var manageable: Bool {
        // these the engine can start/stop today
        ["php", "web", "db", "cache", "mail", "dns"].contains(service.role)
            && service.installed && service.key != "httpd"
    }

    var body: some View {
        HStack(spacing: 10) {
            StatusDot(on: service.running)
            VStack(alignment: .leading, spacing: 1) {
                Text(service.key).font(.body.monospaced())
                Text(service.installed ? service.shortVersion : "not installed")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if service.installed && manageable {
                Button {
                    Task { await state.setServiceAutoStart(service.key, !service.autoStart) }
                } label: {
                    Image(systemName: service.autoStart ? "star.fill" : "star")
                        .foregroundStyle(service.autoStart ? .yellow : .secondary)
                }
                .buttonStyle(.borderless)
                .help(service.autoStart ? "Auto-starts with “Start All”" : "Click to auto-start with “Start All”")
            }
            if !service.installed {
                Button("Install") { Task { await state.installService(service.key) } }
                    .controlSize(.small)
            } else if manageable {
                if service.running {
                    Button("Stop") { Task { await state.control("stop", service.key) } }
                        .controlSize(.small)
                } else {
                    Button("Start") { Task { await state.control("start", service.key) } }
                        .controlSize(.small).tint(.green)
                }
            }
            if service.installed {
                Menu {
                    Button { Task { await state.updateService(service.key) } } label: {
                        Label("Update to latest", systemImage: "arrow.up.circle")
                    }
                    if service.role == "php" {
                        Button { editingIni = true } label: {
                            Label("Edit php.ini", systemImage: "doc.text")
                        }
                    }
                    Button(role: .destructive) { confirmUninstall = true } label: {
                        Label("Uninstall", systemImage: "trash")
                    }
                } label: { Image(systemName: "ellipsis.circle") }
                    .menuStyle(.borderlessButton).fixedSize()
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 12) }
        .disabled(state.busy)
        .confirmationDialog("Uninstall \(service.key)? (brew uninstall — stops it first)",
                            isPresented: $confirmUninstall, titleVisibility: .visible) {
            Button("Uninstall \(service.key)", role: .destructive) {
                Task { await state.uninstallService(service.key) }
            }
            Button("Cancel", role: .cancel) {}
        }
        .sheet(isPresented: $editingIni) {
            EditPhpIniSheet(service: service)
        }
    }
}

struct EditPhpIniSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let service: Service

    @State private var content = ""
    @State private var path: String?
    @State private var loading = true
    @State private var loadError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                VStack(alignment: .leading, spacing: 1) {
                    Text("Edit php.ini — \(service.key)").font(.headline)
                    if let path { Text(path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
                }
                Spacer()
            }
            if loading {
                Spacer(); ProgressView("Loading…").frame(maxWidth: .infinity); Spacer()
            } else if let loadError {
                Spacer()
                Label(loadError, systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.secondary).frame(maxWidth: .infinity)
                Spacer()
            } else {
                TextEditor(text: $content)
                    .font(.system(.caption, design: .monospaced))
                    .autocorrectionDisabled()
                    .frame(minWidth: 640, minHeight: 440)
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))
            }
            HStack {
                Text("Saving restarts php-fpm \(service.key) if it's running.")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") {
                    Task {
                        if let path { await state.savePhpIni(service.key, path: path, content: content) }
                        dismiss()
                    }
                }
                .keyboardShortcut("s", modifiers: .command)
                .disabled(loading || loadError != nil || state.busy)
            }
        }
        .padding(16)
        .frame(width: 720, height: 560)
        .task {
            guard let p = await state.phpIniPath(service.key) else {
                loadError = "Couldn't locate php.ini for \(service.key)."; loading = false; return
            }
            path = p
            do { content = try String(contentsOfFile: p, encoding: .utf8) }
            catch { loadError = error.localizedDescription }
            loading = false
        }
    }
}

struct StatusDot: View {
    let on: Bool
    var body: some View {
        Circle()
            .fill(on ? Color.green : Color.secondary.opacity(0.4))
            .frame(width: 10, height: 10)
            .overlay(Circle().stroke(.black.opacity(0.1), lineWidth: 0.5))
    }
}
