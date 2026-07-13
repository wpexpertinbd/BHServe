import SwiftUI

/// First-run onboarding: ensures Homebrew + the core stack are present before the
/// main UI is useful.
struct SetupView: View {
    @Environment(AppState.self) private var state

    var body: some View {
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
                        Button("Re-check") { Task { await state.reload() } }
                            .buttonStyle(.bordered)
                    }
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
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .task {
            await state.reload()
            while !Task.isCancelled {           // poll so it advances after Terminal finishes
                try? await Task.sleep(for: .seconds(3))
                if !state.busy { await state.reload() }
            }
        }
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
