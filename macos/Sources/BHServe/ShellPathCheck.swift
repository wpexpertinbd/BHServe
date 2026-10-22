import Foundation

/// Detects another local server's tools (XAMPP, MAMP…) sitting in the user's Terminal PATH ahead of
/// macOS's own. Homebrew's installer takes the FIRST `git`/`curl` it finds, so XAMPP's bundled copies
/// broke it with "Failed to query latest Homebrew/brew Git tag" (real user, 2026-10-22, M3 + macOS 27).
enum ShellPathCheck {
    struct Shadow: Equatable, Identifiable {
        let tool: String       // "git" / "curl"
        let dir: String        // the folder whose copy wins, e.g. /Applications/XAMPP/xamppfiles/bin
        var id: String { tool }
    }

    /// Bundled-stack servers whose own old git/curl break other tools. Matched against the winning
    /// folder AND the executable's symlink target, so `/usr/local/bin/git -> …/XAMPP/…/git` is caught.
    /// Deliberately NOT a "trusted folders" allow-list: MacPorts, Nix, asdf, mise… are legitimate and
    /// must never be told to leave PATH.
    private static let localServerMarkers = ["/xampp/", "/mamp/", "/ampps/", "/lampp/", "/bitnami"]

    private static func isLocalServer(_ path: String) -> Bool {
        let p = path.lowercased() + "/"
        return localServerMarkers.contains { p.contains($0) }
    }

    /// Pure check: for each tool, the FIRST folder in `path` holding an executable copy — reported
    /// when that copy belongs to another local server stack.
    static func shadows(path: String,
                        isExecutable: (String) -> Bool = { FileManager.default.isExecutableFile(atPath: $0) },
                        resolve: (String) -> String = { ($0 as NSString).resolvingSymlinksInPath })
        -> [Shadow] {
        let dirs = path.split(separator: ":").map(String.init).filter { !$0.isEmpty }
        var out: [Shadow] = []
        for tool in ["git", "curl"] {
            guard let first = dirs.first(where: { isExecutable("\($0)/\(tool)") }) else { continue }
            let clean = first.hasSuffix("/") && first.count > 1 ? String(first.dropLast()) : first
            if isLocalServer(clean) || isLocalServer(resolve("\(clean)/\(tool)")) {
                out.append(Shadow(tool: tool, dir: clean))
            }
        }
        return out
    }

    /// The PATH the user's Terminal really has. A GUI app only gets launchd's minimal PATH, so ask the
    /// user's own shell as an INTERACTIVE login shell (that is what reads ~/.zshrc, where XAMPP-style
    /// PATH lines usually live). Bounded: output is read AS IT ARRIVES and we stop at the end marker or
    /// the deadline — never wait for EOF, because a background job started by the user's shell config
    /// can hold the pipe open forever. A shell that ignores SIGTERM is killed. Returns nil on any
    /// trouble, which simply means "no warning" — the installer itself is protected regardless.
    static func loginShellPATH(shell: String? = nil, arguments: [String]? = nil,
                               timeout: TimeInterval = 5) -> String? {
        let sh = shell ?? ProcessInfo.processInfo.environment["SHELL"].flatMap { $0.isEmpty ? nil : $0 } ?? "/bin/zsh"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: sh)
        p.arguments = arguments ?? ["-l", "-i", "-c", "printf '__BHPATH__%s__END__' \"$PATH\""]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        p.standardInput = FileHandle.nullDevice

        let buffer = LockedBuffer()
        let reader = pipe.fileHandleForReading
        reader.readabilityHandler = { h in buffer.append(h.availableData) }
        defer { reader.readabilityHandler = nil; try? reader.close() }
        do { try p.run() } catch { return nil }

        func extract() -> String? {
            let text = String(decoding: buffer.snapshot(), as: UTF8.self)
            guard let a = text.range(of: "__BHPATH__"),
                  let b = text.range(of: "__END__", range: a.upperBound..<text.endIndex) else { return nil }
            return String(text[a.upperBound..<b.lowerBound])
        }

        let deadline = Date().addingTimeInterval(timeout)
        var result: String?
        while Date() < deadline {
            if let r = extract() { result = r; break }
            if !p.isRunning { Thread.sleep(forTimeInterval: 0.05); result = extract(); break }
            Thread.sleep(forTimeInterval: 0.05)
        }
        if p.isRunning {
            p.terminate()
            let killBy = Date().addingTimeInterval(0.5)
            while p.isRunning && Date() < killBy { Thread.sleep(forTimeInterval: 0.02) }
            if p.isRunning { kill(p.processIdentifier, SIGKILL) }
        }
        return result
    }

    /// Output collected from the pipe's background reader (capped at 1 MB, keeps the newest bytes).
    private final class LockedBuffer: @unchecked Sendable {
        private let lock = NSLock()
        private var data = Data()
        func append(_ chunk: Data) {
            lock.lock(); defer { lock.unlock() }
            data.append(chunk)
            if data.count > 1_048_576 { data.removeFirst(data.count - 1_048_576) }
        }
        func snapshot() -> Data { lock.lock(); defer { lock.unlock() }; return data }
    }
}
