import SwiftUI
import AppKit

/// Success/failure notice after a long action (add site, install service) — shows the
/// engine's steps with green checks, a clickable URL, and Open/Done. (Windows parity.)
struct ResultSheet: View {
    @Environment(\.dismiss) private var dismiss
    let result: AppState.ActionResult

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: result.success ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .font(.largeTitle).foregroundStyle(result.success ? .green : .red)
                Text(result.title).font(.title2.bold())
            }
            if let url = result.url, let u = URL(string: url) {
                Link(url, destination: u).font(.callout)
            }
            if !result.steps.isEmpty {
                VStack(alignment: .leading, spacing: 7) {
                    ForEach(result.steps) { s in
                        HStack(alignment: .top, spacing: 8) {
                            Image(systemName: s.done ? "checkmark" : "circle.fill")
                                .font(s.done ? .caption.weight(.bold) : .system(size: 5))
                                .foregroundStyle(s.done ? .green : .secondary)
                                .frame(width: 13).padding(.top, s.done ? 2 : 6)
                            Text(s.text).font(.callout).foregroundStyle(s.done ? .primary : .secondary)
                                .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }
                .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
            }
            HStack {
                Spacer()
                if let url = result.url, let u = URL(string: url) {
                    Button("Open site") { NSWorkspace.shared.open(u); dismiss() }
                        .keyboardShortcut(.defaultAction)
                }
                Button("Done") { dismiss() }
                    .keyboardShortcut(result.url == nil ? .defaultAction : .cancelAction)
            }
        }
        .padding(20).frame(width: 500)
    }
}

/// Remove-site dialog with the "also delete files + drop database" option. (Windows parity.)
struct RemoveSiteSheet: View {
    @Environment(AppState.self) private var state
    @Environment(\.dismiss) private var dismiss
    let site: Site
    @State private var purge = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Remove site").font(.title2.bold())
            Text("Remove ‘\(site.name)’? By default this removes only the site mapping and keeps your files and database.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Toggle("Also delete the site files and drop its database", isOn: $purge)
            if purge {
                Label("This permanently deletes \(site.root) and the ‘\(site.name)’ database.",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(role: .destructive) {
                    Task { await state.removeSite(site.name, purge: purge) }
                    dismiss()
                } label: { Text(purge ? "Delete everything" : "Remove") }
            }
        }
        .padding(20).frame(width: 470)
    }
}
