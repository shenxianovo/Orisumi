import AppKit
import SwiftUI

@main
struct OrisumiApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    private var model: AppModel { delegate.model }

    var body: some Scene {
        Window("Orisumi", id: "main") {
            AppWindow(model: model, startsInMenuBar: delegate.startsInMenuBar)
        }
        .defaultSize(width: 940, height: 610)
        .windowResizability(.contentMinSize)
        .commands {
            CommandGroup(after: .newItem) {
                Button("Refresh") { Task { await model.refresh() } }
                    .keyboardShortcut("r")
                    .disabled(model.busy)
            }
        }

        MenuBarExtra {
            AppMenu(model: model)
        } label: {
            Image(systemName: model.health.symbol)
                .renderingMode(.template)
                .accessibilityLabel(Text(model.health.title))
        }
        .menuBarExtraStyle(.menu)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let startsInMenuBar: Bool
    let model: AppModel

    override init() {
        var arguments = Array(CommandLine.arguments.dropFirst())
        startsInMenuBar = arguments.first == "--menu-bar"
        if startsInMenuBar { arguments.removeFirst() }
        model = AppModel(arguments: arguments, bundle: Bundle.main.bundleURL)
        super.init()
    }

    func applicationWillFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        model.startPolling()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}

private struct AppMenu: View {
    @Environment(\.openWindow) private var openWindow
    let model: AppModel

    var body: some View {
        Text(model.status?.summary ?? Health.unavailable.title)
        if let status = model.status {
            ForEach(status.collectors) { collector in
                Text("\(collector.displayName) — \(collector.menuState(now: status.generatedAt))")
            }
        }
        Divider()
        Button("Open Orisumi…") {
            model.windowRequested = true
            NSApp.setActivationPolicy(.regular)
            openWindow(id: "main")
            NSApp.activate()
        }
        .keyboardShortcut("o")
        Button(model.startTitle) { perform(.start) }
            .disabled(!model.mayStart)
        Button("Stop Capture") {
            NSApp.activate()
            let alert = NSAlert()
            alert.messageText = "Stop capture?"
            alert.informativeText = stopExplanation
            alert.addButton(withTitle: "Cancel")
            alert.addButton(withTitle: "Stop Capture").hasDestructiveAction = true
            if alert.runModal() == .alertSecondButtonReturn { perform(.stop) }
        }
        .disabled(!model.mayStop)
        Divider()
        Button("Quit Orisumi") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
            .help("Capture keeps running.")
    }

    private func perform(_ action: CaptureAction) {
        Task {
            await model.perform(action)
            if !model.windowOpen, let message = model.actionError {
                NSApp.activate()
                let alert = NSAlert()
                alert.messageText = "Capture command failed"
                alert.informativeText = message
                alert.alertStyle = .warning
                alert.runModal()
                model.actionError = nil
            }
        }
    }
}

let stopExplanation = "Collectors stop until you start capture again."
