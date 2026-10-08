import SwiftUI

/// Shown when macOS is blocking BHServe's start-at-login (see AppState.loginItemBlocked).
struct LoginBlockedBanner: View {
    @Environment(AppState.self) private var state

    var body: some View {
        if state.loginItemBlocked {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.title3)
                VStack(alignment: .leading, spacing: 4) {
                    Text("macOS is blocking BHServe from starting at login").font(.headline)
                    Text("Your sites won't come up after a restart until you allow it: System Settings › General › Login Items & Extensions › Allow in the Background → turn on the BHServe row marked \u{201C}Not running in background\u{201D} with the magnifier (\u{1F50D}) icon. If two BHServe rows appear, the other one is a harmless leftover from older versions \u{2014} switching it does nothing.")
                        .font(.callout).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                Button("Open Login Items") { state.openLoginItemsSettings() }
            }
            .padding(14)
            .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.orange.opacity(0.35)))
        }
    }
}
