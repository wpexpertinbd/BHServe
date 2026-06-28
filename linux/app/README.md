# linux/app — the GTK GUI (to build)

GTK4 + libadwaita, following the macOS app design (BH blue #0d6efd, 8 sidebar panes).
Pick **Rust + gtk4-rs** (single self-contained binary, matches the no-runtime ethos)
or **Python + PyGObject** (fastest to build, ships as a .deb depending on python3-gi).

It drives `../../engine/bhserve` exactly like the SwiftUI app does on macOS: spawn the
CLI, parse the `api` JSON, render the panes. See ../README.md for the pane list and
../../docs/MAC-FEATURE-REFERENCE.md for the per-screen detail.
