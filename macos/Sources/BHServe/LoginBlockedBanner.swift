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
                    Text("Your sites won't come up after a restart until you allow it: System Settings › General › Login Items & Extensions › Allow in the Background → turn BHServe on (it may be listed as \u{201C}open\u{201D}).")
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
