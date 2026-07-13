import Foundation
import Darwin
import Observation

/// Live macOS system metrics (CPU %, memory, disk), sampled on a timer.
@MainActor
@Observable
final class Metrics {
    @MainActor static let shared = Metrics()
    var cpu: Double = 0                 // 0…100 busy %
    var cpuHistory: [Double] = []       // recent samples for the sparkline
    var memUsed: UInt64 = 0
    var memTotal: UInt64 = 0
    var diskUsed: Int64 = 0
    var diskTotal: Int64 = 0
    var netDownRate: Double = 0   // bytes/sec
    var netUpRate: Double = 0

    private var lastTicks: (Double, Double, Double, Double)?
    private var lastNetIn: UInt64?
    private var lastNetOut: UInt64?
    private var lastNetTime: Date?
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
        if let n = Metrics.netBytes() {
            let now = Date()
            if let li = lastNetIn, let lo = lastNetOut, let lt = lastNetTime {
                let dt = now.timeIntervalSince(lt)
                if dt > 0 {
                    netDownRate = n.inB >= li ? Double(n.inB - li) / dt : 0
                    netUpRate   = n.outB >= lo ? Double(n.outB - lo) / dt : 0
                }
            }
            lastNetIn = n.inB; lastNetOut = n.outB; lastNetTime = now
        }
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

    // Sum rx/tx bytes across non-loopback link-layer interfaces.
    static func netBytes() -> (inB: UInt64, outB: UInt64)? {
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0 else { return nil }
        defer { freeifaddrs(ifaddr) }
        var inB: UInt64 = 0, outB: UInt64 = 0
        var ptr = ifaddr
        while let p = ptr {
            let ifa = p.pointee
            if let addr = ifa.ifa_addr, Int32(addr.pointee.sa_family) == AF_LINK,
               let data = ifa.ifa_data {
                let name = String(cString: ifa.ifa_name)
                if !name.hasPrefix("lo") {
                    let d = data.assumingMemoryBound(to: if_data.self).pointee
                    inB += UInt64(d.ifi_ibytes)
                    outB += UInt64(d.ifi_obytes)
                }
            }
            ptr = ifa.ifa_next
        }
        return (inB, outB)
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
    /// network throughput
    static func rate(_ bps: Double) -> String {
        if bps >= 1_000_000 { return String(format: "%.1f MB/s", bps / 1_000_000) }
        if bps >= 1_000 { return String(format: "%.0f KB/s", bps / 1_000) }
        return String(format: "%.0f B/s", bps)
    }
}
