import Foundation

struct EngineError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// Thin wrapper around the `bhserve` bash engine. Sendable so it can run off the
/// main actor (Process calls block). Holds only an immutable path.
final class Engine: Sendable {
    let enginePath: String
    init(enginePath: String) { self.enginePath = enginePath }

    /// Run the engine as the current user and return stdout.
    /// `env` entries are merged onto the inherited environment (used to pass a DB
    /// password via $BHSERVE_DB_PASSWORD so it never appears in `ps`/argv).
    @discardableResult
    func run(_ args: [String], env: [String: String] = [:]) throws -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = [enginePath] + args
        if !env.isEmpty {
            var merged = ProcessInfo.processInfo.environment
            for (k, v) in env { merged[k] = v }
            p.environment = merged
        }
        let out = Pipe(), err = Pipe()
        p.standardOutput = out
        p.standardError = err
        try p.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        let errData = err.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        if p.terminationStatus != 0 {
            let e = String(data: errData, encoding: .utf8) ?? ""
            throw EngineError(message: e.isEmpty ? "engine exited \(p.terminationStatus)" : e)
        }
        return String(data: data, encoding: .utf8) ?? ""
    }

    /// Run the engine elevated, via a GUI admin prompt (needed for :80/:443 + DNS).
    func runPrivileged(_ args: [String]) throws {
        let bashCmd = "/bin/bash " + ([enginePath] + args).map(Engine.shQuote).joined(separator: " ")
        let script = "do shell script \"\(Engine.asEscape(bashCmd))\" with administrator privileges"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", script]
        let err = Pipe()
        p.standardError = err
        try p.run()
        let errData = err.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        if p.terminationStatus != 0 {
            let e = String(data: errData, encoding: .utf8) ?? ""
            // user-cancelled the prompt = code 1 / "User canceled."
            throw EngineError(message: e.isEmpty ? "elevation failed (\(p.terminationStatus))" : e)
        }
    }

    func snapshot() throws -> Snapshot {
        let json = try run(["api"])
        guard let data = json.data(using: .utf8) else { throw EngineError(message: "empty engine output") }
        let dec = JSONDecoder()
        dec.keyDecodingStrategy = .convertFromSnakeCase
        return try dec.decode(Snapshot.self, from: data)
    }

    // ── escaping ─────────────────────────────────────────────────────────────
    // Single-quote for the shell (defuses every metachar), THEN escape for the
    // AppleScript string literal. Never interpolate raw paths into do-shell-script.
    static func shQuote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
    static func asEscape(_ s: String) -> String {
        s.replacingOccurrences(of: "\\", with: "\\\\")
         .replacingOccurrences(of: "\"", with: "\\\"")
    }
}
