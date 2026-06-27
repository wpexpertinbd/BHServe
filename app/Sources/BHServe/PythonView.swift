import SwiftUI

struct PythonView: View {
    @Environment(AppState.self) private var state
    @State private var addingApp = false

    private var pyApps: [Site] { state.realSites.filter { $0.python } }
    private var pyInstalled: Bool { state.serviceInstalled("python") }
    private var pyVersion: String { state.snapshot?.services.first { $0.key == "python" }?.version ?? "" }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                // ── Python interpreter ───────────────────────────────────────
                VStack(alignment: .leading, spacing: 8) {
                    Text("Interpreter").font(.headline).foregroundStyle(.secondary)
                    if !pyInstalled {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("BHServe runs Python apps with a managed Python (Homebrew python@3.13). Install it once — each app then gets its own virtualenv for its dependencies.")
                                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                            Button("Install Python") { Task { await state.installService("python") } }.disabled(state.busy)
                            if state.busy { ProgressView().controlSize(.small) }
                        }
                        .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
                    } else {
                        HStack(spacing: 10) {
                            Image(systemName: "checkmark.seal.fill").foregroundStyle(.teal)
                            Text(pyVersion.isEmpty ? "Python installed" : pyVersion).font(.body.monospaced())
                            Spacer()
                            Button("Update to latest") { Task { await state.updateService("python") } }
                                .controlSize(.small).disabled(state.busy)
                        }
                        .padding(.horizontal, 12).padding(.vertical, 10)
                        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
                        Text("Each Python app keeps its own dependencies in a `.venv` — use “pip install” on an app to install its requirements.txt.")
                            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }

                // ── Python apps (managed sites) ──────────────────────────────
                ManagedAppsSection(
                    title: "Python apps",
                    apps: pyApps,
                    emptyText: "No Python apps yet. Add one — Flask, Django, FastAPI, Gunicorn or Uvicorn. BHServe supervises the process and serves it at name.\(state.snapshot?.config.tld ?? "test") over HTTPS.",
                    addLabel: "Add Python app",
                    onAdd: { addingApp = true })
            }
            .padding(20)
        }
        .navigationTitle("Python")
        .toolbar {
            Button { Task { await state.reload() } } label: { Image(systemName: "arrow.clockwise") }
        }
        .sheet(isPresented: $addingApp) { AddPythonAppSheet() }
    }
}

/// Dedicated "Add Python app" sheet (parallel to AddNodeAppSheet).
struct AddPythonAppSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var dir = ""
    @State private var port = "8001"
    @State private var cmd = "python app.py"
    @State private var venv = true

    private var cleanName: String {
        name.trimmingCharacters(in: .whitespaces).lowercased().replacingOccurrences(of: " ", with: "-")
    }
    private var ready: Bool { !cleanName.isEmpty && !dir.isEmpty && !port.isEmpty }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Add Python app").font(.title2.bold())
            HStack {
                TextField("myapp", text: $name).textFieldStyle(.roundedBorder)
                Text(".\(state.snapshot?.config.tld ?? "test")").foregroundStyle(.secondary)
            }
            Text("Flask / Django / FastAPI / Gunicorn / Uvicorn. The run command can use $PORT.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    TextField("/path/to/app", text: $dir).textFieldStyle(.roundedBorder).font(.caption.monospaced())
                    Button("Choose…") { if let p = bhChooseFolder(start: dir.isEmpty ? state.snapshot?.config.sitesRoot : dir) { dir = p } }
                }
                HStack {
                    TextField("run command (e.g. uvicorn main:app --port $PORT)", text: $cmd)
                        .textFieldStyle(.roundedBorder).font(.caption.monospaced())
                    TextField("port", text: $port).textFieldStyle(.roundedBorder).frame(width: 70)
                }
            }
            .padding(10).background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 8))
            Toggle("Create a virtualenv (.venv) for this app", isOn: $venv)
            if !state.serviceInstalled("python") {
                Label("Python isn’t installed yet — it’ll be installed when you create this app.",
                      systemImage: "info.circle").font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Create") {
                    let n = cleanName, d = dir, p = port, c = cmd, v = venv
                    Task {
                        if !state.serviceInstalled("python") { await state.installService("python") }
                        await state.addPySite(name: n, dir: d, port: p, cmd: c, venv: v)
                    }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction).disabled(!ready || state.busy)
            }
        }
        .padding(20).frame(width: 480)
    }
}
