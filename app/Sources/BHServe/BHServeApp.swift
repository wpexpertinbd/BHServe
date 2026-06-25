import SwiftUI
import AppKit

/// Menu-bar-resident behavior: while the dashboard window is open the app is a
/// regular app (Dock icon + can take keyboard focus); when it's closed the app
/// drops to .accessory — no Dock icon, keeps running, reachable from the menu bar.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // The dashboard is an AppKit window we create ONLY on demand (DashboardWindow),
        // so a login (background) launch can never show it — we just start services and
        // sit in the menu bar. A normal launch opens it explicitly.
        NSApp.setActivationPolicy(.accessory)
        Task { await AppState.shared.bootAutostart() }
        if !AppState.isBackgroundLaunch {
            DashboardWindow.shared.show()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    // Double-clicking the app (running or not) → open the dashboard.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        DashboardWindow.shared.show()
        return true
    }
}

@main
struct BHServeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var state = AppState.shared
    @State private var metrics = Metrics.shared

    var body: some Scene {
        // Menu bar is the ONLY scene — the dashboard window is AppKit-managed on demand.
        MenuBarExtra("BHServe", systemImage: "server.rack") {
            MenuBarView().environment(state).environment(metrics)
        }
        .menuBarExtraStyle(.window)
    }
}

/// AppKit-managed dashboard window. It exists only after `show()` is called — never auto-
/// created — so a background/login launch stays silent. Reused across open/close.
@MainActor
final class DashboardWindow: NSObject, NSWindowDelegate {
    static let shared = DashboardWindow()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let host = NSHostingController(
                rootView: ContentView().environment(AppState.shared).environment(Metrics.shared))
            let w = NSWindow(contentViewController: host)
            w.title = "BHServe"
            // A solid, standard title bar (NOT fullSizeContentView/transparent) so each
            // tab's title + refresh button always sit on an opaque top bar — content
            // scrolls cleanly under it at any window size instead of bleeding to the top.
            w.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            w.setContentSize(NSSize(width: 860, height: 620))
            w.titlebarAppearsTransparent = false
            w.isReleasedWhenClosed = false
            w.center()
            w.delegate = self
            window = w
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    // Closing the dashboard returns the app to the menu bar (no Dock icon).
    func windowWillClose(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }
}

/// Compact menu-bar panel: status, Start/Stop All, quick links, open window.
struct MenuBarView: View {
    @Environment(AppState.self) private var state
    @Environment(Metrics.self) private var metrics

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: "server.rack")
                Text("BHServe").font(.headline)
                Text("v\(state.appVersion)").font(.caption2).foregroundStyle(.secondary)
                Spacer()
                Circle().fill(state.running.isEmpty ? Color.secondary : Color.green).frame(width: 9, height: 9)
            }

            Text(state.running.isEmpty
                 ? "All services stopped"
                 : state.running.map(\.key).joined(separator: ", "))
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            // live system metrics
            VStack(spacing: 6) {
                MenuMetricRow(label: "CPU", percent: metrics.cpu,
                              detail: String(format: "%.0f%%", metrics.cpu), tint: metrics.cpu > 80 ? .red : .green)
                MenuMetricRow(label: "RAM", percent: metrics.memPercent,
                              detail: "\(ByteFmt.giB(metrics.memUsed))/\(ByteFmt.giB(metrics.memTotal)) GB", tint: .blue)
                MenuMetricRow(label: "Disk", percent: metrics.diskPercent,
                              detail: "\(ByteFmt.gB(metrics.diskUsed))/\(ByteFmt.gB(metrics.diskTotal))", tint: .green)
            }
            Sparkline(values: metrics.cpuHistory, maxValue: 100)
                .frame(height: 28).foregroundStyle(.green)

            Divider()

            HStack {
                Button("Start All") { Task { await state.control("start", "all") } }
                    .disabled(state.busy || !state.hasDaemons || state.allDaemonsRunning)
                Button("Stop All") { Task { await state.control("stop", "all") } }
                    .disabled(state.busy || !state.anyDaemonRunning)
                Button("Restart All") { Task { await state.restartAll() } }
                    .disabled(state.busy || !state.anyDaemonRunning)
            }

            if !state.realSites.isEmpty {
                Divider()
                HStack {
                    Text("Sites").font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    if state.realSites.count > 5 {
                        Text("\(state.realSites.count) total — Open BHServe for all")
                            .font(.caption2).foregroundStyle(.secondary)
                    }
                }
                ForEach(state.realSites.prefix(5)) { site in
                    MenuLinkRow(title: site.domain, systemImage: site.secure ? "lock.fill" : "globe") {
                        if let url = site.url { NSWorkspace.shared.open(url) }
                    }
                }
            }

            // Quick-open managed tools (only when installed + served)
            if state.toolActive("phpmyadmin") || state.toolActive("adminer") || state.toolActive("mailpit") {
                Divider()
                Text("Tools").font(.caption).foregroundStyle(.secondary)
                if state.toolActive("phpmyadmin") {
                    MenuLinkRow(title: "Open phpMyAdmin", systemImage: "cylinder.split.1x2") { state.openTool("phpmyadmin") }
                }
                if state.toolActive("adminer") {
                    MenuLinkRow(title: "Open Adminer", systemImage: "tablecells") { state.openTool("adminer") }
                }
                if state.toolActive("mailpit") {
                    MenuLinkRow(title: "Open Mailpit", systemImage: "envelope") { state.openTool("mailpit") }
                }
            }

            Divider()
            HStack {
                Button("Open BHServe") { DashboardWindow.shared.show() }
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
            }
        }
        .padding(12)
        .frame(width: 300)
        .task {
            metrics.startSampling()
            await state.reload()
        }
    }
}

/// A menu-bar list row that highlights on hover (so it reads as clickable).
struct MenuLinkRow: View {
    let title: String
    let systemImage: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8).padding(.vertical, 5)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .background(RoundedRectangle(cornerRadius: 6)
            .fill(hovering ? Color.accentColor.opacity(0.18) : Color.clear))
        .onHover { hovering = $0 }
    }
}

struct MenuMetricRow: View {
    let label: String, percent: Double, detail: String, tint: Color
    var body: some View {
        HStack(spacing: 8) {
            Text(label).font(.caption.weight(.medium)).frame(width: 32, alignment: .leading)
            ProgressView(value: min(max(percent, 0), 100), total: 100).tint(tint)
            Text(detail).font(.caption2.monospacedDigit()).foregroundStyle(.secondary)
                .frame(width: 96, alignment: .trailing)
        }
    }
}
