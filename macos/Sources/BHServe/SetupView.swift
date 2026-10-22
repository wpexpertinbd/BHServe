import SwiftUI

/// First-run onboarding: ensures Homebrew + the core stack are present before the
/// main UI is useful.
struct SetupView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        // ScrollView so a small window scrolls the steps instead of clipping (or, worse,
        // demanding a window taller than a MacBook screen — see DashboardWindow.show()).
        ScrollView {
        VStack(spacing: 22) {
            VStack(spacing: 8) {
                Image(systemName: "server.rack").font(.system(size: 44)).foregroundStyle(.blue)
                Text("Welcome to BHServe").font(.largeTitle.bold())
                Text("A free, self-controlled local dev server for macOS.")
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 16) {
                SetupStep(number: 1, title: "Homebrew",
                          done: state.brewInstalled,
                          detail: state.brewInstalled
                            ? "Installed — BHServe uses it to manage every service."
                            : "BHServe installs PHP, nginx, MariaDB, etc. via Homebrew. Let's install it first.") {
                    if !state.brewInstalled {
                        Button {
                            state.openHomebrewInstaller()
                        } label: { Label("Install Homebrew", systemImage: "terminal") }
                        Button("Re-check") { Task { await state.reload(); await state.checkShellPath() } }
                            .buttonStyle(.bordered)
                    }
                }

                if !state.pathShadows.isEmpty {
                    PathShadowCard(shadows: state.pathShadows)
                }

                SetupStep(number: 2, title: "Core services",
                          done: state.coreInstalled,
                          detail: state.coreInstalled
                            ? "nginx, PHP, MariaDB, mkcert and dnsmasq are installed."
                            : "Install the core stack: nginx, PHP 8.4, MariaDB, mkcert, dnsmasq, Mailpit.") {
                    if state.brewInstalled && !state.coreInstalled {
                        Button {
                            Task { await state.installCoreServices() }
                        } label: { Label("Install core services", systemImage: "square.and.arrow.down") }
                        .disabled(state.busy)
                        if state.busy { ProgressView().controlSize(.small) }
                    } else if !state.brewInstalled {
                        Text("Finish step 1 first.").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .frame(maxWidth: 460)

            if let note = state.lastAction {
                Label(note, systemImage: "gearshape").font(.caption).foregroundStyle(.secondary)
            }
            if let err = state.errorText {
                Text(err).font(.caption).foregroundStyle(.red).lineLimit(3)
            }
            Text("BHServe v\(state.appVersion) · BiswasHost").font(.caption2).foregroundStyle(.tertiary)
        }
        .padding(36)
        .frame(maxWidth: .infinity)
        }
        .task {
            await state.reload()
            await state.checkShellPath()
            while !Task.isCancelled {           // poll so it advances after Terminal finishes
                try? await Task.sleep(for: .seconds(3))
                if !state.busy { await state.reload() }
            }
        }
    }
}

/// Another local server (XAMPP, MAMP…) has its own git/curl ahead of macOS's in the Terminal PATH.
struct PathShadowCard: View {
    let shadows: [ShellPathCheck.Shadow]

    private var dirs: [String] { Array(Set(shadows.map(\.dir))).sorted() }
    private var serverName: String {
        let all = dirs.joined(separator: " ").lowercased()
        if all.contains("xampp") { return "XAMPP" }
        if all.contains("mamp") { return "MAMP" }
        return "another app"
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.title3)
            VStack(alignment: .leading, spacing: 6) {
                Text("\(serverName)'s tools come before macOS's own in your Terminal").font(.headline)
                Text("Your Terminal uses \(shadows.map(\.tool).joined(separator: " and ")) from:")
                    .font(.callout).foregroundStyle(.secondary)
                ForEach(dirs, id: \.self) { d in
                    Text(d).font(.callout.monospaced()).textSelection(.enabled)
                }
                Text("BHServe's Install Homebrew button already works around this. But please stop \(serverName)'s Apache and MySQL before continuing: BHServe uses the same ports (80, 443, 3306). To fix your Terminal for good, remove that folder from the PATH line in ~/.zshrc (or ~/.zprofile), then open a new Terminal window.")
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.orange.opacity(0.35)))
    }
}

struct SetupStep<Actions: View>: View {
    let number: Int
    let title: String
    let done: Bool
    let detail: String
    @ViewBuilder var actions: Actions

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            ZStack {
                Circle().fill(done ? Color.green : Color.secondary.opacity(0.25)).frame(width: 30, height: 30)
                if done { Image(systemName: "checkmark").foregroundStyle(.white).font(.subheadline.bold()) }
                else { Text("\(number)").foregroundStyle(.secondary).font(.subheadline.bold()) }
            }
            VStack(alignment: .leading, spacing: 8) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack { actions }
            }
            Spacer()
        }
        .padding(14)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
    }
}
