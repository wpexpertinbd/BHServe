import SwiftUI

struct ServicesView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 18) {
                ForEach(ServiceRole.allCases, id: \.self) { role in
                    let svcs = state.services(role: role)
                    if !svcs.isEmpty {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(role.title)
                                .font(.headline)
                                .foregroundStyle(.secondary)
                            VStack(spacing: 0) {
                                ForEach(svcs) { ServiceRow(service: $0) }
                            }
                            .background(.quaternary.opacity(0.4))
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        }
                    }
                }
            }
            .padding(20)
        }
        .navigationTitle("Services")
        .toolbar {
            Button { Task { await state.reload() } } label: {
                Image(systemName: "arrow.clockwise")
            }
            .help("Refresh")
        }
    }
}

struct ServiceRow: View {
    @Environment(AppState.self) private var state
    let service: Service

    private var manageable: Bool {
        // these the engine can start/stop today
        ["php", "web", "db", "cache", "mail", "dns"].contains(service.role)
            && service.installed && service.key != "httpd"
    }

    var body: some View {
        HStack(spacing: 10) {
            StatusDot(on: service.running)
            VStack(alignment: .leading, spacing: 1) {
                Text(service.key).font(.body.monospaced())
                Text(service.installed ? service.shortVersion : "not installed")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if !service.installed {
                Button("Install") { Task { await state.installService(service.key) } }
                    .controlSize(.small)
            } else if manageable {
                if service.running {
                    Button("Stop") { Task { await state.control("stop", service.key) } }
                        .controlSize(.small)
                } else {
                    Button("Start") { Task { await state.control("start", service.key) } }
                        .controlSize(.small).tint(.green)
                }
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .overlay(alignment: .bottom) { Divider().padding(.leading, 12) }
        .disabled(state.busy)
    }
}

struct StatusDot: View {
    let on: Bool
    var body: some View {
        Circle()
            .fill(on ? Color.green : Color.secondary.opacity(0.4))
            .frame(width: 10, height: 10)
            .overlay(Circle().stroke(.black.opacity(0.1), lineWidth: 0.5))
    }
}
