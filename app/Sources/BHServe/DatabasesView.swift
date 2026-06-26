import SwiftUI

struct DatabasesView: View {
    @Environment(AppState.self) private var state
    @State private var newName = ""
    @State private var newPassword = ""
    @State private var newEngine = "mysql"

    private var anyServerRunning: Bool { state.mysqlRunning || state.pgRunning }
    /// The selected engine must actually be running to create into it.
    private var selectedEngineRunning: Bool { newEngine == "pg" ? state.pgRunning : state.mysqlRunning }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                // Servers — always show the MySQL-family row first, PostgreSQL second.
                VStack(alignment: .leading, spacing: 6) {
                    Text("Database servers").font(.headline).foregroundStyle(.secondary)
                    VStack(spacing: 0) {
                        DbServerRow(label: state.mysqlLabel, key: state.mysqlServiceKey, isMysqlFamily: true)
                        DbServerRow(label: "PostgreSQL", key: "postgresql@17", isMysqlFamily: false)
                    }
                    .background(.quaternary.opacity(0.4))
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                }

                // MySQL/MariaDB root user
                if state.mysqlRunning {
                    RootUserCard()
                }

                // Create — engine dropdown auto-detects the installed engines
                if !state.dbEngineOptions.isEmpty {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Create database").font(.headline).foregroundStyle(.secondary)
                        HStack {
                            TextField("database_name", text: $newName)
                                .textFieldStyle(.roundedBorder)
                            Picker("", selection: $newEngine) {
                                ForEach(state.dbEngineOptions, id: \.tag) { Text($0.label).tag($0.tag) }
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
                            .disabled(state.busy || newName.trimmingCharacters(in: .whitespaces).isEmpty || !selectedEngineRunning)
                        }
                        if !selectedEngineRunning {
                            Label("Start \(newEngine == "pg" ? "PostgreSQL" : state.mysqlLabel) above to create databases.",
                                  systemImage: "exclamationmark.circle")
                                .font(.caption).foregroundStyle(.orange)
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
            // default the picker to the first available engine (or a running one)
            if let opts = state.dbEngineOptions.first?.tag, !state.dbEngineOptions.contains(where: { $0.tag == newEngine }) {
                newEngine = opts
            }
            if !state.mysqlRunning && state.pgRunning { newEngine = "pg" }
        }
        .onChange(of: anyServerRunning) { _, _ in Task { await state.reloadDatabases() } }
    }
}

/// A database-server row (MariaDB/MySQL or PostgreSQL): version + state-aware
/// Start/Stop, Install when absent, Root-password for the MySQL family. Always shown.
struct DbServerRow: View {
    @Environment(AppState.self) private var state
    let label: String          // "MariaDB" / "MySQL" / "PostgreSQL"
    let key: String            // service key: "mariadb" / "mysql" / "postgresql@17"
    let isMysqlFamily: Bool
    @State private var rootSheet = false

    private var svc: Service? { state.snapshot?.services.first { $0.key == key } }
    private var installed: Bool { svc?.installed ?? false }
    private var running: Bool { svc?.running ?? false }

    private var statusText: String {
        guard installed else { return "not installed" }
        if !running { return "stopped" }
        return isMysqlFamily ? "running · \(state.rootStatus == "set" ? "password set" : "no password")" : "running"
    }

    var body: some View {
        HStack(spacing: 10) {
            Circle().fill(running ? Color.green : Color.secondary.opacity(0.4)).frame(width: 9, height: 9)
            VStack(alignment: .leading, spacing: 1) {
                Text(installed ? "\(label) \(svc?.shortVersion ?? "")".trimmingCharacters(in: .whitespaces) : label)
                    .font(.body.weight(.medium))
                Text(statusText).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if !installed {
                Button("Install \(label)") { Task { await state.installService(key) } }
                    .controlSize(.small).disabled(state.busy)
            } else {
                Button("Start") { Task { await state.control("start", key) } }
                    .controlSize(.small).disabled(state.busy || running)
                Button("Stop") { Task { await state.control("stop", key) } }
                    .controlSize(.small).disabled(state.busy || !running)
                if isMysqlFamily {
                    Button("Root password…") { rootSheet = true }
                        .controlSize(.small).disabled(!running)
                }
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 10)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 12) }
        .sheet(isPresented: $rootSheet) {
            RootPasswordSheet(hasPassword: state.rootStatus == "set") { pw in
                Task { await state.setRootPassword(pw) }
            }
        }
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

struct RootUserCard: View {
    @Environment(AppState.self) private var state
    @State private var sheet = false

    private var hasPassword: Bool { state.rootStatus == "set" }
    private var label: String {
        switch state.rootStatus {
        case "set": "Password set"
        case "blank": "No password (blank)"
        default: "—"
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "crown.fill").foregroundStyle(.yellow)
            VStack(alignment: .leading, spacing: 1) {
                Text("root@localhost").font(.body.monospaced())
                Text(label).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button(hasPassword ? "Change password" : "Set password") { sheet = true }
                .controlSize(.small)
        }
        .padding(.horizontal, 12).padding(.vertical, 10)
        .background(.quaternary.opacity(0.4))
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .disabled(state.busy)
        .sheet(isPresented: $sheet) {
            RootPasswordSheet(hasPassword: hasPassword) { pw in
                Task { await state.setRootPassword(pw) }
            }
        }
    }
}

struct RootPasswordSheet: View {
    let hasPassword: Bool
    let onSave: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var pw = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("MySQL root password").font(.title2.bold())
            Text("Sets the password for root@localhost.").font(.caption).foregroundStyle(.secondary)
            HStack {
                TextField("password (leave blank for none)", text: $pw)
                    .textFieldStyle(.roundedBorder).font(.body.monospaced())
                Button("Generate") { pw = PasswordGen.make() }
            }
            Text(pw.isEmpty
                 ? "Blank = root has no password (anyone local can connect as root)."
                 : "Copy this now — it's the root account password.")
                .font(.caption2).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button(pw.isEmpty ? "Set blank" : "Save") { onSave(pw); dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 440)
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
