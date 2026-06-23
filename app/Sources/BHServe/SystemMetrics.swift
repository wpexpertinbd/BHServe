import Foundation
import Darwin
import Observation

/// Live macOS system metrics (CPU %, memory, disk), sampled on a timer.
@MainActor
@Observable
final class Metrics {
    var cpu: Double = 0                 // 0…100 busy %
    var cpuHistory: [Double] = []       // recent samples for the sparkline
    var memUsed: UInt64 = 0
    var memTotal: UInt64 = 0
    var diskUsed: Int64 = 0
    var diskTotal: Int64 = 0

    private var lastTicks: (Double, Double, Double, Double)?
    private var timer: Timer?

    var memPercent: Double { memTotal > 0 ? Double(memUsed) / Double(memTotal) * 100 : 0 }
    var diskPercent: Double { diskTotal > 0 ? Double(diskUsed) / Double(diskTotal) * 100 : 0 }

    func startSampling(interval: TimeInterval = 2) {
        guard timer == nil else { return }
        sample()
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.sample() }
        }
    }

    func sample() {
        if let t = Metrics.cpuTicks() {
            if let p = lastTicks {
                let dUser = t.0 - p.0, dSys = t.1 - p.1, dIdle = t.2 - p.2, dNice = t.3 - p.3
                let busy = dUser + dSys + dNice
                let total = busy + dIdle
                if total > 0 { cpu = max(0, min(100, busy / total * 100)) }
            }
            lastTicks = t
        }
        if let m = Metrics.memInfo() { memUsed = m.used; memTotal = m.total }
        if let d = Metrics.diskInfo() { diskUsed = d.used; diskTotal = d.total }
        cpuHistory.append(cpu)
        if cpuHistory.count > 60 { cpuHistory.removeFirst(cpuHistory.count - 60) }
    }

    // ── Darwin sampling ──────────────────────────────────────────────────────
    static func cpuTicks() -> (Double, Double, Double, Double)? {
        var info = host_cpu_load_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info_data_t>.stride / MemoryLayout<integer_t>.stride)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return nil }
        // CPU_STATE_USER=0, SYSTEM=1, IDLE=2, NICE=3
        return (Double(info.cpu_ticks.0), Double(info.cpu_ticks.1), Double(info.cpu_ticks.2), Double(info.cpu_ticks.3))
    }

    static func memInfo() -> (used: UInt64, total: UInt64)? {
        let total = ProcessInfo.processInfo.physicalMemory
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.stride / MemoryLayout<integer_t>.stride)
        let kr = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return nil }
        let page = UInt64(getpagesize())
        // Activity-Monitor-style "used" ≈ active + wired + compressed
        let used = (UInt64(stats.active_count) + UInt64(stats.wire_count) + UInt64(stats.compressor_page_count)) * page
        return (used, total)
    }

    static func diskInfo() -> (used: Int64, total: Int64)? {
        let url = URL(fileURLWithPath: "/")
        guard let v = try? url.resourceValues(forKeys: [.volumeTotalCapacityKey, .volumeAvailableCapacityForImportantUsageKey]),
              let total = v.volumeTotalCapacity,
              let avail = v.volumeAvailableCapacityForImportantUsage else { return nil }
        return (Int64(total) - avail, Int64(total))
    }
}

enum ByteFmt {
    /// memory in GiB (matches "24.0 GB" RAM convention)
    static func giB(_ bytes: UInt64) -> String { String(format: "%.1f", Double(bytes) / 1_073_741_824) }
    /// disk in GB (decimal, matches Finder)
    static func gB(_ bytes: Int64) -> String {
        let gb = Double(bytes) / 1_000_000_000
        return gb >= 1000 ? String(format: "%.2f TB", gb / 1000) : String(format: "%.0f GB", gb)
    }
}
