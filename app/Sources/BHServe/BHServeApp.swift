import SwiftUI
import AppKit

/// Without a proper .app bundle (e.g. `swift run`), macOS may launch the process
/// as an accessory app, so its window never becomes key → TextFields can't take
/// keyboard input (mouse still works). Force a regular, activated app.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { NSApp.activate(ignoringOtherApps: true) }
        return true
    }
}

@main
struct BHServeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var state = AppState()
    @State private var metrics = Metrics()

    var body: some Scene {
        Window("BHServe", id: "main") {
            ContentView().environment(state).environment(metrics)
        }
        .defaultSize(width: 820, height: 600)

        MenuBarExtra("BHServe", systemImage: "server.rack") {
            MenuBarView().environment(state).environment(metrics)
        }
        .menuBarExtraStyle(.window)
    }
}

/// Compact menu-bar panel: status, Start/Stop All, quick links, open window.
struct MenuBarView: View {
    @Environment(AppState.self) private var state
    @Environment(Metrics.self) private var metrics
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: "server.rack")
                Text("BHServe").font(.headline)
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
                Button("Stop All") { Task { await state.control("stop", "all") } }
            }
            .disabled(state.busy)

            if let sites = state.snapshot?.sites, !sites.isEmpty {
                Divider()
                Text("Sites").font(.caption).foregroundStyle(.secondary)
                ForEach(sites.prefix(6)) { site in
                    if let url = site.url {
                        Button {
                            NSWorkspace.shared.open(url)
                        } label: {
                            Label(site.domain, systemImage: site.secure ? "lock.fill" : "globe")
                        }
                        .buttonStyle(.plain)
                    }
                }
            }

            Divider()
            HStack {
                Button("Open BHServe") {
                    openWindow(id: "main")
                    NSApp.activate(ignoringOtherApps: true)
                }
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
