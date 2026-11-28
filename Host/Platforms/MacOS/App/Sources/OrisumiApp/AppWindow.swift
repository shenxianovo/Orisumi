import AppKit
import SwiftUI

struct AppWindow: View {
    /// Nil until the user opens or closes a row; until then the sources with problems are open, stopped ones aren't.
    @State private var expanded: Set<String>?
    @State private var confirmingStop = false
    @State private var window = WindowIdentity()
    @Bindable var model: AppModel
    let startsInMenuBar: Bool

    init(model: AppModel, startsInMenuBar: Bool, initialSelection source: String? = nil) {
        self.model = model
        self.startsInMenuBar = startsInMenuBar
        _expanded = State(initialValue: source.map { [$0] })
    }

    var body: some View {
        Group {
            if let error = model.queryError {
                ContentUnavailableView {
                    Label {
                        Text("Can't Read Orisumi's Status")
                    } icon: {
                        AppIconView(size: 72).grayscale(1).opacity(0.6)
                    }
                } description: {
                    VStack(spacing: 6) {
                        Text(error)
                        if let path = model.cliPath {
                            Text(path).font(.caption.monospaced()).foregroundStyle(.secondary)
                        }
                    }
                    .textSelection(.enabled)
                } actions: {
                    Button("Try Again") { Task { await model.refresh() } }
                        .disabled(model.busy)
                }
            } else if let status = model.status {
                if status.collectors.isEmpty {
                    ContentUnavailableView {
                        Label { Text("No Sources Installed") } icon: { AppIconView(size: 72) }
                    } description: {
                        Text("Deploy Orisumi with `orisumi-dev deploy`, then start capture.")
                    }
                } else {
                    Overview(status: status, coverage: model.coverage, expanded: Binding(
                        get: { expanded ?? Set(status.collectors.filter { $0.severity > .stopped }.map(\.id)) },
                        set: { expanded = $0 }
                    ), busy: model.busy) {
                        Task { await model.perform(.start) }
                    }
                }
            } else {
                ProgressView("Reading capture status…")
            }
        }
        .frame(minWidth: 680, minHeight: 440)
        .toolbar { toolbar }
        .background(WindowReference { attached in
            guard window.value !== attached else { return }
            window.value = attached
            if startsInMenuBar && !model.windowRequested { attached.close() }
            else {
                windowOpened(attached)
                NSApp.activate()
            }
        })
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didBecomeKeyNotification)) { notification in
            guard let attached = window.value, notification.object as? NSWindow === attached else { return }
            if !startsInMenuBar || model.windowRequested { windowOpened(attached) }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.willCloseNotification)) { notification in
            guard let window = window.value, notification.object as? NSWindow === window else { return }
            model.windowChanged(open: false, visible: false)
            NSApp.setActivationPolicy(.accessory)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didMiniaturizeNotification)) { notification in
            guard let window = window.value, notification.object as? NSWindow === window else { return }
            model.windowChanged(open: true, visible: false)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didDeminiaturizeNotification)) { notification in
            guard let window = window.value, notification.object as? NSWindow === window else { return }
            model.windowChanged(open: true, visible: true)
        }
        .confirmationDialog("Stop capture?", isPresented: $confirmingStop, titleVisibility: .visible) {
            Button("Stop Capture", role: .destructive) { Task { await model.perform(.stop) } }
        } message: {
            Text(stopExplanation)
        }
        .alert("Capture command failed", isPresented: Binding(
            get: { model.actionError != nil },
            set: { if !$0 { model.actionError = nil } }
        ), presenting: model.actionError) { _ in
            Button("OK", role: .cancel) {}
        } message: { message in
            Text(message)
        }
    }

    private func windowOpened(_ window: NSWindow) {
        if NSApp.activationPolicy() != .regular { NSApp.setActivationPolicy(.regular) }
        model.windowChanged(open: true, visible: !window.isMiniaturized)
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItemGroup {
            if model.busy {
                ProgressView().controlSize(.small)
                    .help("Running capture command")
                    .accessibilityLabel("Running capture command")
            }
            Menu {
                Button(model.startTitle) { Task { await model.perform(.start) } }
                    .disabled(!model.mayStart)
                Button("Stop Capture…") { confirmingStop = true }
                    .disabled(!model.mayStop)
            } label: {
                Label("Capture", systemImage: "ellipsis.circle")
            }
            .help("Start, restart or stop every collector")
        }
    }
}

private final class WindowIdentity {
    weak var value: NSWindow?
}

private struct WindowReference: NSViewRepresentable {
    let attached: (NSWindow) -> Void

    func makeNSView(context: Context) -> ReferenceView { ReferenceView(attached) }
    func updateNSView(_ nsView: ReferenceView, context: Context) {}

    final class ReferenceView: NSView {
        private let attached: (NSWindow) -> Void

        init(_ attached: @escaping (NSWindow) -> Void) {
            self.attached = attached
            super.init(frame: .zero)
        }

        required init?(coder: NSCoder) { nil }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            DispatchQueue.main.async { [weak self] in
                if let self, let window = self.window { self.attached(window) }
            }
        }
    }
}
