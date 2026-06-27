import SwiftUI
import AppKit

/// Identifiable wrapper so a single sheet can edit either the FE or BE .env.
struct EnvTarget: Identifiable { let part: String; var id: String { part } }

/// Edit a Node app's `.env` (frontend or backend). Saving restarts the site so new
/// values — including ports — take effect. (Changing the listen PORT also needs the
/// site's port updated in "Edit config" so nginx proxies to the right place.)
struct EnvEditorSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site
    let part: String                      // "fe" | "be"

    @State private var content = ""
    @State private var path: String?
    @State private var loading = true
    @State private var note: String?

    private var dir: String? { part == "be" ? site.beDir : site.feDir }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                Text("Edit .env — \(site.name) (\(part == "be" ? "backend" : "frontend"))").font(.headline)
                if let path { Text(path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
            }
            if loading {
                Spacer(); ProgressView("Loading…").frame(maxWidth: .infinity); Spacer()
            } else if let note {
                Spacer(); Label(note, systemImage: "exclamationmark.triangle").foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity); Spacer()
            } else {
                TextEditor(text: $content)
                    .font(.system(.caption, design: .monospaced)).autocorrectionDisabled()
                    .frame(minWidth: 620, minHeight: 380)
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))
            }
            HStack {
                Text("Saving restarts \(site.name) so new values apply.")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") {
                    Task {
                        if let p = path { await state.saveEnv(siteName: site.name, path: p, content: content) }
                        dismiss()
                    }
                }
                .keyboardShortcut("s", modifiers: .command)
                .disabled(loading || path == nil || state.busy)
            }
        }
        .padding(16).frame(width: 700, height: 500)
        .task {
            guard let p = state.envPath(dir) else { note = "No folder set for \(part)."; loading = false; return }
            path = p
            if let text = try? String(contentsOfFile: p, encoding: .utf8) { content = text }
            else { content = ""; note = nil }   // a missing .env is editable (creates it on save)
            loading = false
        }
    }
}

/// Live frontend/backend process logs for a Node site.
struct NodeLogsSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site
    @State private var part = "fe"
    @State private var content = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Logs — \(site.name)").font(.headline)
                Picker("", selection: $part) {
                    Text("Frontend").tag("fe")
                    if site.hasBackend { Text("Backend").tag("be") }
                }.pickerStyle(.segmented).labelsHidden().fixedSize()
                Spacer()
                Button { Task { await load() } } label: { Image(systemName: "arrow.clockwise") }
                Button("Done") { dismiss() }
            }
            ScrollView {
                Text(content.isEmpty ? "(no log yet)" : content)
                    .font(.system(.caption, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled).padding(8)
            }
            .frame(minWidth: 640, minHeight: 380)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
        }
        .padding(16).frame(width: 720, height: 480)
        .task { await load() }
        .onChange(of: part) { _, _ in Task { await load() } }
    }

    private func load() async { content = await state.readLog("node-\(site.name)-\(part).log") }
}

/// Process log for a Python app (`py-<name>.log`).
struct PyLogsSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site
    @State private var content = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Logs — \(site.name)").font(.headline)
                Spacer()
                Button { Task { await load() } } label: { Image(systemName: "arrow.clockwise") }
                Button("Done") { dismiss() }
            }
            ScrollView {
                Text(content.isEmpty ? "(no log yet)" : content)
                    .font(.system(.caption, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled).padding(8)
            }
            .frame(minWidth: 640, minHeight: 380)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
        }
        .padding(16).frame(width: 720, height: 480)
        .task { await load() }
    }

    private func load() async { content = await state.readLog("py-\(site.name).log") }
}

/// Edit a Node site's config (folders / commands / ports / api paths). Saving
/// overwrites the definition + re-renders the vhost and restarts the site.
struct EditNodeSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site

    @State private var feDir = ""; @State private var feCmd = ""; @State private var fePort = ""
    @State private var beDir = ""; @State private var beCmd = ""; @State private var bePort = ""
    @State private var apiPaths = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Edit Node site — \(site.name)").font(.title2.bold())
            Text(site.domain).font(.caption).foregroundStyle(.secondary)
            NodeAppFields(title: "Frontend", dir: $feDir, cmd: $feCmd, port: $fePort)
            NodeAppFields(title: "Backend (optional)", dir: $beDir, cmd: $beCmd, port: $bePort)
            if !beDir.trimmingCharacters(in: .whitespaces).isEmpty {
                LabeledContent("API paths → backend") {
                    TextField("api|storage|…", text: $apiPaths).font(.caption.monospaced())
                }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save & Restart") {
                    Task {
                        await state.addNodeSite(name: site.name, feDir: feDir, feCmd: feCmd, fePort: fePort,
                                                beDir: beDir, beCmd: beCmd, bePort: bePort, apiPaths: apiPaths)
                        dismiss()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(feDir.isEmpty || fePort.isEmpty || state.busy)
            }
        }
        .padding(20).frame(width: 560)
        .onAppear {
            feDir = site.feDir ?? ""; feCmd = site.feCmd ?? "npm run dev"; fePort = site.fePort ?? ""
            beDir = site.beDir ?? ""; beCmd = site.beCmd ?? ""; bePort = site.bePort ?? ""
            apiPaths = site.apiPaths ?? "api|storage|sanctum|admin|livewire|vendor|build|up"
        }
    }
}

/// Dedicated "Add Node app" sheet (Node tab). Same fields as the Sites-tab Node
/// type, without the site-type picker.
struct AddNodeAppSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var feDir = ""; @State private var feCmd = "npm run dev"; @State private var fePort = "3000"
    @State private var beDir = ""; @State private var beCmd = ""; @State private var bePort = ""
    @State private var apiPaths = "api|storage|sanctum|admin|livewire|vendor|build|up"

    private var cleanName: String {
        name.trimmingCharacters(in: .whitespaces).lowercased().replacingOccurrences(of: " ", with: "-")
    }
    private var ready: Bool { !cleanName.isEmpty && !feDir.isEmpty && !fePort.isEmpty
        && (beDir.trimmingCharacters(in: .whitespaces).isEmpty || !bePort.isEmpty) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Add Node app").font(.title2.bold())
            HStack {
                TextField("myapp", text: $name).textFieldStyle(.roundedBorder)
                Text(".\(state.snapshot?.config.tld ?? "test")").foregroundStyle(.secondary)
            }
            NodeAppFields(title: "Frontend", dir: $feDir, cmd: $feCmd, port: $fePort)
            NodeAppFields(title: "Backend / API (optional)", dir: $beDir, cmd: $beCmd, port: $bePort)
            if !beDir.trimmingCharacters(in: .whitespaces).isEmpty {
                LabeledContent("API paths → backend") {
                    TextField("api|storage|…", text: $apiPaths).font(.caption.monospaced())
                }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Create") {
                    let n = cleanName
                    Task {
                        await state.addNodeSite(name: n, feDir: feDir, feCmd: feCmd, fePort: fePort,
                                                beDir: beDir, beCmd: beCmd, bePort: bePort, apiPaths: apiPaths)
                    }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction).disabled(!ready || state.busy)
            }
        }
        .padding(20).frame(width: 480)
    }
}

/// Reusable folder + command + port block (used by Add and Edit).
struct NodeAppFields: View {
    let title: String
    @Binding var dir: String
    @Binding var cmd: String
    @Binding var port: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
            HStack {
                TextField("/path/to/app", text: $dir).textFieldStyle(.roundedBorder).font(.caption.monospaced())
                Button("Choose…") { if let p = bhChooseFolder(start: dir.isEmpty ? nil : dir) { dir = p } }
            }
            HStack {
                TextField("run command (e.g. npm run dev)", text: $cmd).textFieldStyle(.roundedBorder).font(.caption.monospaced())
                TextField("port", text: $port).textFieldStyle(.roundedBorder).frame(width: 70)
            }
        }
        .padding(10).background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 8))
    }
}
