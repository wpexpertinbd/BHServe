import SwiftUI

struct NodeView: View {
    @Environment(AppState.self) private var state
    @State private var version = ""
    @State private var confirmRemove: NodeVersion?

    private let quick = ["18", "20", "22", "24", "lts", "latest"]

    var body: some View {
        Group {
            if !state.fnmInstalled {
                ContentUnavailableView {
                    Label("Node manager not installed", systemImage: "hexagon")
                } description: {
                    Text("BHServe manages Node versions with fnm (versions live in ~/.bhserve/fnm).")
                } actions: {
                    Button("Install fnm") { Task { await state.installService("fnm") } }
                        .disabled(state.busy)
                }
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        // Install
                        VStack(alignment: .leading, spacing: 8) {
                            Text("Install a Node version").font(.headline).foregroundStyle(.secondary)
                            HStack {
                                TextField("18 · 20 · 22 · lts · 20.11.0", text: $version)
                                    .textFieldStyle(.roundedBorder).frame(maxWidth: 260)
                                Button("Install") {
                                    Task { await state.installNode(version); version = "" }
                                }
                                .disabled(state.busy || version.trimmingCharacters(in: .whitespaces).isEmpty)
                                if state.busy { ProgressView().controlSize(.small) }
                            }
                            HStack(spacing: 6) {
                                ForEach(quick, id: \.self) { q in
                                    Button(q) { Task { await state.installNode(q) } }
                                        .controlSize(.small).buttonStyle(.bordered)
                                        .disabled(state.busy)
                                }
                            }
                            Text("Installs the latest release of that line (e.g. “18” → newest 18.x).")
                                .font(.caption2).foregroundStyle(.secondary)
                        }

                        // Installed
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Installed versions").font(.headline).foregroundStyle(.secondary)
                            if state.nodeVersions.isEmpty {
                                Text("No versions yet — install one above.")
                                    .font(.callout).foregroundStyle(.secondary).padding(.vertical, 8)
                            } else {
                                VStack(spacing: 0) {
                                    ForEach(state.nodeVersions) { NodeRow(node: $0, confirmRemove: $confirmRemove) }
                                }
                                .background(.quaternary.opacity(0.4))
                                .clipShape(RoundedRectangle(cornerRadius: 8))
                            }
                            Text("The default version is linked into ~/.bhserve/bin. Add it to PATH: export PATH=\"$HOME/.bhserve/bin:$PATH\"")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .padding(20)
                }
            }
        }
        .navigationTitle("Node")
        .toolbar {
            Button { Task { await state.reloadNode() } } label: { Image(systemName: "arrow.clockwise") }
        }
        .task { await state.reloadNode() }
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
