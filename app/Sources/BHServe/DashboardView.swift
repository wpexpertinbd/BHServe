import SwiftUI

struct DashboardView: View {
    @Environment(AppState.self) private var state

    private var web: Service? { state.snapshot?.services.first { $0.key == "nginx" } }
    private var phpRunning: [String] { state.services(role: .php).filter { $0.running }.map { $0.key.replacingOccurrences(of: "php@", with: "") } }
    private var db: Service? { state.snapshot?.services.first { ($0.key == "mariadb" || $0.key == "mysql") && $0.installed } }
    private var cache: Service? { state.snapshot?.services.first { $0.key == "redis" } }
    private var siteCount: Int { state.snapshot?.sites.count ?? 0 }

    private let cols = [GridItem(.adaptive(minimum: 220), spacing: 14)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                // Service status cards
                LazyVGrid(columns: cols, spacing: 14) {
                    StatusCard(title: "Web Server", icon: "globe",
                               value: web?.running == true ? "nginx" : "stopped",
                               sub: web?.running == true ? "\(web?.shortVersion ?? "") · \(siteCount) sites" : "\(siteCount) sites",
                               on: web?.running == true)
                    StatusCard(title: "PHP", icon: "chevron.left.forwardslash.chevron.right",
                               value: phpRunning.isEmpty ? "stopped" : phpRunning.joined(separator: ", "),
                               sub: "\(state.services(role: .php).filter { $0.installed }.count) installed",
                               on: !phpRunning.isEmpty)
                    StatusCard(title: "Database", icon: "cylinder.split.1x2",
                               value: db?.running == true ? "MariaDB" : "stopped",
                               sub: db?.shortVersion ?? "—",
                               on: db?.running == true)
                    StatusCard(title: "Cache", icon: "bolt.horizontal",
                               value: cache?.running == true ? "Redis" : (cache?.installed == true ? "stopped" : "not installed"),
                               sub: cache?.shortVersion ?? "—",
                               on: cache?.running == true)
                }

                // System metrics (isolated so 2s ticks don't re-render the rest)
                SystemMetricsGrid(cols: cols)

                // Websites
                WebsitesPanel()
            }
            .padding(20)
        }
        .navigationTitle("Dashboard")
        .toolbar {
            Button { Task { await state.reload() } } label: { Image(systemName: "arrow.clockwise") }
        }
    }
}

/// Owns the Metrics dependency so the live 2s sampling only re-renders these
/// three cards — not the websites list (which was making the search box "dance").
struct SystemMetricsGrid: View {
    @Environment(Metrics.self) private var metrics
    let cols: [GridItem]
    var body: some View {
        LazyVGrid(columns: cols, spacing: 14) {
            CPUCard()
            MetricCard(title: "Memory", icon: "memorychip",
                       percent: metrics.memPercent,
                       detail: "\(ByteFmt.giB(metrics.memUsed)) / \(ByteFmt.giB(metrics.memTotal)) GB",
                       tint: .blue)
            MetricCard(title: "Storage", icon: "internaldrive",
                       percent: metrics.diskPercent,
                       detail: "\(ByteFmt.gB(metrics.diskUsed)) / \(ByteFmt.gB(metrics.diskTotal))",
                       tint: .green)
        }
        .task { metrics.startSampling() }
    }
}

struct StatusCard: View {
    let title: String, icon: String, value: String, sub: String, on: Bool
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: icon).foregroundStyle(.secondary)
                Text(title).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                Spacer()
                Circle().fill(on ? Color.green : Color.secondary.opacity(0.4)).frame(width: 9, height: 9)
            }
            Text(value).font(.title3.weight(.semibold)).lineLimit(1).minimumScaleFactor(0.7)
            Text(sub).font(.caption).foregroundStyle(.secondary).lineLimit(1)
        }
        .padding(14)
        .frame(maxWidth: .infinity, minHeight: 92, alignment: .leading)
        .background(.quaternary.opacity(0.4))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }
}

struct CPUCard: View {
    @Environment(Metrics.self) private var metrics
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: "cpu").foregroundStyle(.secondary)
                Text("CPU").font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                Spacer()
                Text(String(format: "%.0f%%", metrics.cpu)).font(.headline).monospacedDigit()
            }
            Sparkline(values: metrics.cpuHistory, maxValue: 100)
                .frame(height: 40)
                .foregroundStyle(cpuColor)
        }
        .padding(14)
        .frame(maxWidth: .infinity, minHeight: 92, alignment: .leading)
        .background(.quaternary.opacity(0.4))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }
    private var cpuColor: Color { metrics.cpu > 80 ? .red : metrics.cpu > 50 ? .orange : .green }
}

struct MetricCard: View {
    let title: String, icon: String, percent: Double, detail: String, tint: Color
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: icon).foregroundStyle(.secondary)
                Text(title).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                Spacer()
                Text(String(format: "%.0f%%", percent)).font(.headline).monospacedDigit()
            }
            ProgressView(value: min(max(percent, 0), 100), total: 100)
                .tint(tint)
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }
        .padding(14)
        .frame(maxWidth: .infinity, minHeight: 92, alignment: .leading)
        .background(.quaternary.opacity(0.4))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }
}

/// Lightweight line sparkline (no Charts dependency).
struct Sparkline: View {
    let values: [Double]
    let maxValue: Double

    var body: some View {
        GeometryReader { geo in
            let pts = points(in: geo.size)
            ZStack {
                if pts.count > 1 {
                    Path { p in
                        p.move(to: pts[0])
                        for pt in pts.dropFirst() { p.addLine(to: pt) }
                    }
                    .stroke(style: StrokeStyle(lineWidth: 1.8, lineJoin: .round))
                    Path { p in
                        p.move(to: CGPoint(x: pts[0].x, y: geo.size.height))
                        for pt in pts { p.addLine(to: pt) }
                        p.addLine(to: CGPoint(x: pts.last!.x, y: geo.size.height))
                        p.closeSubpath()
                    }
                    .fill(LinearGradient(colors: [.primary.opacity(0.18), .clear], startPoint: .top, endPoint: .bottom))
                } else {
                    Text("collecting…").font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func points(in size: CGSize) -> [CGPoint] {
        guard values.count > 1 else { return [] }
        let stepX = size.width / CGFloat(values.count - 1)
        return values.enumerated().map { i, v in
            let y = size.height * (1 - CGFloat(min(max(v, 0), maxValue) / maxValue))
            return CGPoint(x: CGFloat(i) * stepX, y: y)
        }
    }
}
