import SwiftUI
import AppKit

enum SidebarItem: String, CaseIterable, Identifiable {
    case dashboard, services, sites, databases, node, logs, settings
    var id: String { rawValue }
    var title: String {
        switch self {
        case .dashboard: "Dashboard"
        case .services: "Services"
        case .sites: "Sites"
        case .databases: "Databases"
        case .node: "Node"
        case .logs: "Logs"
        case .settings: "Settings"
        }
    }
    var icon: String {
        switch self {
        case .dashboard: "gauge.with.dots.needle.67percent"
        case .services: "server.rack"
        case .sites: "globe"
        case .databases: "cylinder.split.1x2"
        case .node: "hexagon"
        case .logs: "doc.plaintext"
        case .settings: "gearshape"
        }
    }
}

struct ContentView: View {
    @Environment(AppState.self) private var state
    @State private var selection: SidebarItem = .dashboard

    var body: some View {
        Group {
            if state.needsSetup { SetupView() }
            else { mainView }
        }
        .onAppear {
            // The window only ever appears on an explicit open → ensure a Dock icon.
            NSApp.setActivationPolicy(.regular)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    private var mainView: some View {
        NavigationSplitView {
            List(SidebarItem.allCases, selection: $selection) { item in
                HStack {
                    Label(item.title, systemImage: item.icon)
                    if item == .settings && state.updateAvailable {
                        Spacer()
                        Circle().fill(.blue).frame(width: 7, height: 7)
                            .help("An update is available")
                    }
                }
                .tag(item)
            }
            .navigationSplitViewColumnWidth(min: 170, ideal: 190)
            .safeAreaInset(edge: .bottom) { StatusFooter() }
        } detail: {
            Group {
                switch selection {
                case .dashboard: DashboardView()
                case .services: ServicesView()
                case .sites: SitesView()
                case .databases: DatabasesView()
                case .node: NodeView()
                case .logs: LogsView()
                case .settings: SettingsView()
                }
            }
            .frame(minWidth: 520, minHeight: 420)
        }
        .task {
            await state.reload()   // boot auto-start is handled in AppDelegate (window-independent)
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
        // Success/failure notice after add-site / install (shown over any tab).
        .sheet(item: Binding(get: { state.actionResult }, set: { state.actionResult = $0 })) { r in
            ResultSheet(result: r)
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
            VStack(spacing: 6) {
                Button { Task { await state.control("start", "all") } } label: {
                    Label("Start All", systemImage: "play.fill").frame(maxWidth: .infinity)
                }
                Button { Task { await state.control("stop", "all") } } label: {
                    Label("Stop All", systemImage: "stop.fill").frame(maxWidth: .infinity)
                }
                Button { Task { await state.restartAll() } } label: {
                    Label("Restart All", systemImage: "arrow.clockwise").frame(maxWidth: .infinity)
                }
            }
            .controlSize(.large)
            .disabled(state.busy)
            if let note = state.lastAction {
                Text(note).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            HStack(spacing: 4) {
                Image(systemName: "server.rack").font(.caption2)
                Text("BHServe v\(state.appVersion)").font(.caption2)
                Spacer()
            }
            .foregroundStyle(.tertiary)
        }
        .padding(10)
    }
}
