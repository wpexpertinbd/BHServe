import SwiftUI

enum SidebarItem: String, CaseIterable, Identifiable {
    case services, sites, databases, logs, settings
    var id: String { rawValue }
    var title: String {
        switch self {
        case .services: "Services"
        case .sites: "Sites"
        case .databases: "Databases"
        case .logs: "Logs"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .services: "server.rack"
        case .sites: "globe"
        case .databases: "cylinder.split.1x2"
        case .logs: "doc.plaintext"
        case .settings: "gearshape"
        }
    }
}

struct ContentView: View {
    @Environment(AppState.self) private var state
    @State private var selection: SidebarItem = .services

    var body: some View {
        NavigationSplitView {
            List(SidebarItem.allCases, selection: $selection) { item in
                Label(item.title, systemImage: item.icon).tag(item)
            }
            .navigationSplitViewColumnWidth(min: 170, ideal: 190)
            .safeAreaInset(edge: .bottom) { StatusFooter() }
        } detail: {
            Group {
                switch selection {
                case .services: ServicesView()
                case .sites: SitesView()
                case .databases: DatabasesView()
                case .logs: LogsView()
                case .settings: SettingsView()
                }
            }
            .frame(minWidth: 520, minHeight: 420)
        }
        .task {
            await state.reload()
            // live auto-refresh while the window is open (skip while an action runs)
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(4))
                if !state.busy { await state.reload() }
            }
        }
        .alert("Engine error", isPresented: Binding(
            get: { state.errorText != nil },
            set: { if !$0 { state.errorText = nil } }
        )) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(state.errorText ?? "")
        }
    }
}

/// Bottom-of-sidebar control: overall status + Start/Stop All + refresh.
struct StatusFooter: View {
    @Environment(AppState.self) private var state

    var body: some View {
        VStack(spacing: 8) {
            Divider()
            HStack {
                Circle()
                    .fill(state.running.isEmpty ? Color.secondary : Color.green)
                    .frame(width: 9, height: 9)
                Text(state.running.isEmpty ? "Stopped" : "\(state.running.count) running")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                if state.busy { ProgressView().controlSize(.small) }
            }
            HStack(spacing: 6) {
                Button { Task { await state.control("start", "all") } } label: {
                    Label("Start All", systemImage: "play.fill").frame(maxWidth: .infinity)
                }
                Button { Task { await state.control("stop", "all") } } label: {
                    Label("Stop All", systemImage: "stop.fill").frame(maxWidth: .infinity)
                }
            }
            .controlSize(.small)
            .disabled(state.busy)
            if let note = state.lastAction {
                Text(note).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(10)
    }
}
