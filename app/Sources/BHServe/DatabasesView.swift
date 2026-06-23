import SwiftUI

struct DatabasesView: View {
    @Environment(AppState.self) private var state
    @State private var newName = ""
    @State private var newEngine = "mysql"

    private var dbServers: [Service] {
        state.snapshot?.services.filter { ["mariadb", "mysql", "postgresql@17"].contains($0.key) && $0.installed } ?? []
    }
    private var anyServerRunning: Bool { state.mysqlRunning || state.pgRunning }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                // Servers
                VStack(alignment: .leading, spacing: 6) {
                    Text("Database servers").font(.headline).foregroundStyle(.secondary)
                    VStack(spacing: 0) { ForEach(dbServers) { ServiceRow(service: $0) } }
                        .background(.quaternary.opacity(0.4))
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                }

                // Create
                if anyServerRunning {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Create database").font(.headline).foregroundStyle(.secondary)
                        HStack {
                            TextField("database_name", text: $newName)
                                .textFieldStyle(.roundedBorder)
                            Picker("", selection: $newEngine) {
                                if state.mysqlRunning { Text("MySQL").tag("mysql") }
                                if state.pgRunning { Text("PostgreSQL").tag("pg") }
                            }
                            .labelsHidden().fixedSize()
                            Button("Create") {
                                Task { await state.createDatabase(newName, engine: newEngine); newName = "" }
                            }
                            .disabled(state.busy || newName.trimmingCharacters(in: .whitespaces).isEmpty)
                        }
                    }
                }

                // List
                VStack(alignment: .leading, spacing: 6) {
                    Text("Databases").font(.headline).foregroundStyle(.secondary)
                    if !anyServerRunning {
                        Text("Start a database server above to manage databases.")
                            .font(.callout).foregroundStyle(.secondary).padding(.vertical, 8)
                    } else if state.databases.isEmpty {
                        Text("No user databases yet.")
                            .font(.callout).foregroundStyle(.secondary).padding(.vertical, 8)
                    } else {
                        VStack(spacing: 0) {
                            ForEach(state.databases) { DatabaseRow(db: $0) }
                        }
                        .background(.quaternary.opacity(0.4))
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                    }
                }
            }
            .padding(20)
        }
        .navigationTitle("Databases")
        .toolbar {
            Button { Task { await state.reloadDatabases() } } label: { Image(systemName: "arrow.clockwise") }
                .help("Refresh")
        }
        .task {
            await state.reloadDatabases()
            // pick a sensible default engine
            if !state.mysqlRunning && state.pgRunning { newEngine = "pg" }
        }
        .onChange(of: anyServerRunning) { _, _ in Task { await state.reloadDatabases() } }
    }
}

struct DatabaseRow: View {
    @Environment(AppState.self) private var state
    let db: Database
    @State private var confirming = false

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "cylinder")
                .foregroundStyle(db.engine == "pg" ? .blue : .orange)
            VStack(alignment: .leading, spacing: 1) {
                Text(db.name).font(.body.monospaced())
                Text(db.engineLabel).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button(role: .destructive) { confirming = true } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .help("Drop \(db.name)")
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 12) }
        .disabled(state.busy)
        .confirmationDialog("Drop database “\(db.name)”? This cannot be undone.",
                            isPresented: $confirming, titleVisibility: .visible) {
            Button("Drop \(db.name)", role: .destructive) {
                Task { await state.dropDatabase(db.name, engine: db.engine) }
            }
            Button("Cancel", role: .cancel) {}
        }
    }
}
