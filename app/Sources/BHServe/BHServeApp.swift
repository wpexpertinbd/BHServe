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

    var body: some Scene {
        Window("BHServe", id: "main") {
            ContentView().environment(state)
        }
        .defaultSize(width: 780, height: 560)

        MenuBarExtra("BHServe", systemImage: "server.rack") {
            MenuBarView().environment(state)
        }
        .menuBarExtraStyle(.window)
    }
}

/// Compact menu-bar panel: status, Start/Stop All, quick links, open window.
struct MenuBarView: View {
    @Environment(AppState.self) private var state
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
        .frame(width: 280)
        .task { await state.reload() }
    }
}
