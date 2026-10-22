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

    /// Folders whose git/curl are fine to use first.
    private static let trusted: Set<String> = [
        "/usr/bin", "/bin", "/usr/sbin", "/sbin",
        "/opt/homebrew/bin", "/usr/local/bin",
        "/Library/Developer/CommandLineTools/usr/bin",
    ]

    private static func isTrusted(_ dir: String) -> Bool {
        trusted.contains(dir) || dir.hasPrefix("/Applications/Xcode")
    }

    /// Pure check: for each tool, the FIRST folder in `path` holding an executable copy — reported
    /// when that folder is not one of macOS's / Homebrew's own.
    static func shadows(path: String,
                        isExecutable: (String) -> Bool = { FileManager.default.isExecutableFile(atPath: $0) })
        -> [Shadow] {
        let dirs = path.split(separator: ":").map(String.init).filter { !$0.isEmpty }
        var out: [Shadow] = []
        for tool in ["git", "curl"] {
            guard let first = dirs.first(where: { isExecutable("\($0)/\(tool)") }) else { continue }
            let clean = first.hasSuffix("/") && first.count > 1 ? String(first.dropLast()) : first
            if !isTrusted(clean) { out.append(Shadow(tool: tool, dir: clean)) }
        }
        return out
    }

    /// The PATH the user's Terminal really has. A GUI app only gets launchd's minimal PATH, so ask the
    /// user's own shell as an interactive login shell (that is what reads ~/.zshrc / ~/.zprofile).
    /// Bounded: a slow or odd shell config must never hang the setup screen.
    static func loginShellPATH(timeout: TimeInterval = 5) -> String? {
        let shell = ProcessInfo.processInfo.environment["SHELL"].flatMap { $0.isEmpty ? nil : $0 } ?? "/bin/zsh"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: shell)
        p.arguments = ["-l", "-i", "-c", "printf '__BHPATH__%s__END__' \"$PATH\""]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        p.standardInput = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let deadline = Date().addingTimeInterval(timeout)
        while p.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        if p.isRunning { p.terminate(); return nil }
        let text = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        guard let a = text.range(of: "__BHPATH__"),
              let b = text.range(of: "__END__", range: a.upperBound..<text.endIndex) else { return nil }
        return String(text[a.upperBound..<b.lowerBound])
    }
}
