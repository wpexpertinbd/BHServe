import SwiftUI

struct DatabasesView: View {
    @Environment(AppState.self) private var state
    @State private var newName = ""
    @State private var newPassword = ""
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
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Create database").font(.headline).foregroundStyle(.secondary)
                        HStack {
                            TextField("database_name", text: $newName)
                                .textFieldStyle(.roundedBorder)
                            Picker("", selection: $newEngine) {
                                if state.mysqlRunning { Text("MySQL").tag("mysql") }
                                if state.pgRunning { Text("PostgreSQL").tag("pg") }
                            }
                            .labelsHidden().fixedSize()
                        }
                        HStack {
                            TextField("password (optional)", text: $newPassword)
                                .textFieldStyle(.roundedBorder).font(.body.monospaced())
                            Button("Generate") { newPassword = PasswordGen.make() }
                            Button("Create") {
                                Task {
                                    await state.createDatabase(newName, engine: newEngine, password: newPassword)
                                    newName = ""; newPassword = ""
                                }
                            }
                            .disabled(state.busy || newName.trimmingCharacters(in: .whitespaces).isEmpty)
                        }
                        Text("Blank password = no dedicated user (use the database via the local socket). A password creates a user named after the database.")
                            .font(.caption2).foregroundStyle(.secondary)
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
            if !state.mysqlRunning && state.pgRunning { newEngine = "pg" }
        }
        .onChange(of: anyServerRunning) { _, _ in Task { await state.reloadDatabases() } }
    }
}

struct DatabaseRow: View {
    @Environment(AppState.self) private var state
    let db: Database
    @State private var confirming = false
    @State private var pwSheet = false

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "cylinder")
                .foregroundStyle(db.engine == "pg" ? .blue : .orange)
            VStack(alignment: .leading, spacing: 1) {
                Text(db.name).font(.body.monospaced())
                HStack(spacing: 5) {
                    Text(db.engineLabel)
                    if db.hasUser {
                        Label("user: \(db.user)", systemImage: "key.fill").labelStyle(.titleAndIcon)
                    }
                }
                .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                pwSheet = true
            } label: {
                Label(db.hasUser ? "Change password" : "Set password",
                      systemImage: db.hasUser ? "key.fill" : "key")
                    .labelStyle(.titleAndIcon)
            }
            .controlSize(.small)

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
        .sheet(isPresented: $pwSheet) {
            PasswordSheet(db: db) { pw in
                Task { await state.setDatabasePassword(db.name, engine: db.engine, password: pw) }
            }
        }
    }
}

struct PasswordSheet: View {
    let db: Database
    let onSave: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var pw = PasswordGen.make()

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(db.hasUser ? "Change password" : "Set password").font(.title2.bold())
            Text("User “\(db.name)”@localhost · \(db.engineLabel)")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                TextField("password", text: $pw)
                    .textFieldStyle(.roundedBorder).font(.body.monospaced())
                Button("Generate") { pw = PasswordGen.make() }
            }
            Text("Copy this now — it grants ALL privileges on “\(db.name)”.")
                .font(.caption2).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") { onSave(pw); dismiss() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(pw.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 420)
    }
}
