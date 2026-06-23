import SwiftUI

struct LogsView: View {
    @Environment(AppState.self) private var state
    @State private var selected = ""
    @State private var content = ""
    @State private var loading = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Picker("Log", selection: $selected) {
                    ForEach(state.logFiles, id: \.self) { Text($0).tag($0) }
                }
                .labelsHidden()
                .frame(maxWidth: 320)
                .disabled(state.logFiles.isEmpty)
                Spacer()
                Button { Task { await refresh() } } label: { Image(systemName: "arrow.clockwise") }
                    .help("Reload")
            }
            .padding(10)
            Divider()

            if state.logFiles.isEmpty {
                ContentUnavailableView("No logs yet", systemImage: "doc.plaintext",
                                       description: Text("Start services and hit a site — nginx/PHP/site logs will appear here."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    Text(content.isEmpty ? "(empty)" : content)
                        .font(.system(.caption, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                        .padding(10)
                }
                .background(Color(nsColor: .textBackgroundColor))
            }
        }
        .navigationTitle("Logs")
        .task {
            await state.listLogs()
            if selected.isEmpty { selected = state.logFiles.first ?? "" }
            await refresh()
        }
        .onChange(of: selected) { _, _ in Task { await refresh() } }
    }

    private func refresh() async {
        await state.listLogs()
        guard !selected.isEmpty else { content = ""; return }
        if !state.logFiles.contains(selected) { selected = state.logFiles.first ?? "" }
        loading = true
        content = await state.readLog(selected)
        loading = false
    }
}
