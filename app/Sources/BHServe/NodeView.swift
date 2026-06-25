import SwiftUI

struct NodeView: View {
    @Environment(AppState.self) private var state
    @State private var version = ""
    @State private var confirmRemove: NodeVersion?
    @State private var addingApp = false

    private let quick = ["18", "20", "22", "24", "lts", "latest"]
    private var nodeApps: [Site] { state.realSites.filter { $0.node } }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                // ── Node versions (fnm) ──────────────────────────────────────
                VStack(alignment: .leading, spacing: 8) {
                    Text("Versions").font(.headline).foregroundStyle(.secondary)
                    if !state.fnmInstalled {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("BHServe manages Node versions with fnm. Install it to add Node versions (Node apps below run with the default version).")
                                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                            Button("Install fnm") { Task { await state.installService("fnm") } }.disabled(state.busy)
                        }
                        .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
                    } else {
                        HStack {
                            TextField("version (e.g. 20, 22, lts)", text: $version)
                                .textFieldStyle(.roundedBorder).frame(maxWidth: 240)
                            Button("Install") { Task { await state.installNode(version); version = "" } }
                                .disabled(state.busy || version.trimmingCharacters(in: .whitespaces).isEmpty)
                            if state.busy { ProgressView().controlSize(.small) }
                        }
                        HStack(spacing: 6) {
                            Text("Quick install:").font(.caption).foregroundStyle(.secondary)
                            ForEach(quick, id: \.self) { q in
                                Button(q) { Task { await state.installNode(q) } }
                                    .controlSize(.small).buttonStyle(.bordered).disabled(state.busy)
                            }
                        }
                        if state.nodeVersions.isEmpty {
                            Text("No Node versions installed yet.").font(.callout).foregroundStyle(.secondary).padding(.vertical, 6)
                        } else {
                            VStack(spacing: 0) {
                                ForEach(state.nodeVersions) { NodeRow(node: $0, confirmRemove: $confirmRemove) }
                            }
                            .background(.quaternary.opacity(0.4)).clipShape(RoundedRectangle(cornerRadius: 8))
                        }
                    }
                }

                // ── Node apps (managed sites) ─────────────────────────────────
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("Node apps").font(.headline).foregroundStyle(.secondary)
                        Spacer()
                        Button { addingApp = true } label: { Label("Add Node app", systemImage: "plus") }
                            .buttonStyle(.borderedProminent).controlSize(.small)
                    }
                    if nodeApps.isEmpty {
                        Text("No Node apps yet. Add one — a frontend (e.g. Next.js) plus an optional backend/API (e.g. Laravel). BHServe runs both and serves them at name.\(state.snapshot?.config.tld ?? "test").")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).padding(.vertical, 8)
                    } else {
                        VStack(spacing: 0) { ForEach(nodeApps) { WebsiteRow(site: $0) } }
                            .background(.quaternary.opacity(0.4)).clipShape(RoundedRectangle(cornerRadius: 10))
                    }
                }
            }
            .padding(20)
        }
        .navigationTitle("Node")
        .toolbar {
            Button { Task { await state.reload(); await state.reloadNode() } } label: { Image(systemName: "arrow.clockwise") }
        }
        .task { await state.reloadNode() }
        .sheet(isPresented: $addingApp) { AddNodeAppSheet() }
        .confirmationDialog("Uninstall Node \(confirmRemove?.version ?? "")?",
                            isPresented: Binding(get: { confirmRemove != nil }, set: { if !$0 { confirmRemove = nil } }),
                            titleVisibility: .visible) {
            if let n = confirmRemove {
                Button("Uninstall \(n.version)", role: .destructive) { Task { await state.uninstallNode(n.version) } }
            }
            Button("Cancel", role: .cancel) {}
        }
    }
}

struct NodeRow: View {
    @Environment(AppState.self) private var state
    let node: NodeVersion
    @Binding var confirmRemove: NodeVersion?

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "hexagon.fill").foregroundStyle(node.isDefault ? .green : .secondary)
            Text(node.version).font(.body.monospaced())
            if node.isDefault {
                Text("default").font(.caption2.weight(.semibold))
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(.green.opacity(0.2), in: Capsule()).foregroundStyle(.green)
            }
            Spacer()
            if !node.isDefault {
                Button("Use") { Task { await state.useNode(node.version) } }
                    .controlSize(.small)
            }
            Button(role: .destructive) { confirmRemove = node } label: { Image(systemName: "trash") }
                .buttonStyle(.borderless).help("Uninstall \(node.version)")
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 12) }
        .disabled(state.busy)
    }
}
