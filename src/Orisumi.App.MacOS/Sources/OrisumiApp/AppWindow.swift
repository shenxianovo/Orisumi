import AppKit
import SwiftUI

private enum Screen: Hashable {
    case overview
    case source(String)
}

struct AppWindow: View {
    @State private var selection: Screen? = .overview
    @State private var confirmingStop = false
    @State private var window = WindowIdentity()
    @Bindable var model: AppModel
    let startsInMenuBar: Bool

    var body: some View {
        Group {
            if let error = model.queryError {
                ContentUnavailableView {
                    Label("Status unavailable", systemImage: Health.unavailable.symbol)
                } description: {
                    VStack(spacing: 8) {
                        Text(error)
                        if let path = model.cliPath { Text(path).font(.caption) }
                    }
                    .textSelection(.enabled)
                }
            } else if let status = model.status {
                if status.collectors.isEmpty {
                    ContentUnavailableView("No collectors installed", systemImage: "tray")
                } else {
                    splitView(status)
                }
            } else {
                ProgressView("Loading capture status…")
            }
        }
        .frame(minWidth: 820, minHeight: 520)
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

    private func splitView(_ status: CaptureStatus) -> some View {
        NavigationSplitView {
            List(selection: $selection) {
                Label("Overview", systemImage: "square.grid.2x2")
                    .tag(Screen.overview)
                Section("Sources") {
                    ForEach(status.collectors) { collector in
                        Label {
                            Text(collector.displayName)
                        } icon: {
                            Image(systemName: "circle.fill")
                                .font(.system(size: 7))
                                .foregroundStyle(collector.stateColor)
                        }
                        .accessibilityValue(collector.stateLabel)
                        .tag(Screen.source(collector.id))
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 155, ideal: 175, max: 225)
        } detail: {
            if case .source(let id) = selection, let collector = status.collectors.first(where: { $0.id == id }) {
                SourceDetail(collector: collector, channels: model.coverage?.channels(for: id) ?? [], store: storeURL(status), now: status.generatedAt)
                    .id(id)
            } else {
                Overview(status: status, coverage: model.coverage) { selection = .source($0) }
            }
        }
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItemGroup {
            Button {
                Task { await model.perform(.start) }
            } label: {
                Label(model.startTitle, systemImage: model.hasLoadedCollectors ? "arrow.triangle.2.circlepath" : "play.circle")
            }
            .help(model.startTitle)
            .accessibilityLabel(model.startTitle)
            .disabled(!model.mayStart)
            Button {
                confirmingStop = true
            } label: {
                Label("Stop Capture", systemImage: "stop.circle")
            }
            .help("Stop Capture")
            .accessibilityLabel("Stop Capture")
            .disabled(!model.mayStop)
            Button {
                if let status = model.status { NSWorkspace.shared.open(storeURL(status)) }
            } label: {
                Label("Open Store Folder", systemImage: "folder")
            }
            .help("Open Store Folder")
            .accessibilityLabel("Open Store Folder")
            .disabled(model.busy || model.status == nil)
            if model.busy {
                ProgressView().controlSize(.small)
                    .help("Running capture command")
                    .accessibilityLabel("Running capture command")
            }
        }
    }

    private func storeURL(_ status: CaptureStatus) -> URL {
        URL(fileURLWithPath: status.store, isDirectory: true)
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
