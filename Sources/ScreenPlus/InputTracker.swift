import AppKit
import QuartzCore

/// Logs mouse position, clicks and key presses with host timestamps while recording.
///
/// Position is polled (no permission needed); clicks and keys come from global event monitors.
/// Global monitors don't see events in our own windows, which conveniently keeps the toolbar's
/// Stop click out of the recording. Key monitoring only works once ScreenPlus has Accessibility
/// access. Only *when* a key was pressed and its rough kind are stored, never which key.
@MainActor
final class InputTracker {
    enum Kind {
        case move
        case click
        case key(KeyKind)
    }

    struct RawEvent {
        var time: Double  // CACurrentMediaTime()
        var point: CGPoint  // global AppKit coordinates
        var kind: Kind
    }

    private var events: [RawEvent] = []
    private var timer: Timer?
    private var clickMonitor: Any?
    private var keyMonitor: Any?

    func start() {
        events = []
        sample()
        let timer = Timer(timeInterval: 1.0 / 120.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sample() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer

        clickMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.events.append(RawEvent(time: CACurrentMediaTime(), point: NSEvent.mouseLocation, kind: .click))
            }
        }

        keyMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard !event.isARepeat else { return }
            let kind = KeyKind(keyCode: event.keyCode)
            MainActor.assumeIsolated {
                self?.events.append(RawEvent(time: CACurrentMediaTime(), point: NSEvent.mouseLocation, kind: .key(kind)))
            }
        }
    }

    func stop() -> [RawEvent] {
        timer?.invalidate()
        timer = nil
        for monitor in [clickMonitor, keyMonitor].compactMap({ $0 }) { NSEvent.removeMonitor(monitor) }
        clickMonitor = nil
        keyMonitor = nil
        return events
    }

    /// Samples every tick, even when still, so interpolation never smears a move across a pause.
    private func sample() {
        events.append(RawEvent(time: CACurrentMediaTime(), point: NSEvent.mouseLocation, kind: .move))
    }

    /// Typing can only be captured with Accessibility access; asks macOS once per launch.
    static func requestKeyboardAccessIfNeeded() {
        guard !AXIsProcessTrusted(), !askedForAccess else { return }
        requestKeyboardAccess()
    }

    /// Shows the system prompt, which also adds ScreenPlus to the Accessibility list in System Settings.
    static func requestKeyboardAccess() {
        askedForAccess = true
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }

    private static var askedForAccess = false
}

extension KeyKind {
    init(keyCode: UInt16) {
        switch keyCode {
        case 49: self = .space
        case 36, 76: self = .enter  // return, keypad enter
        case 51, 117: self = .delete  // delete, forward delete
        default: self = .regular
        }
    }
}
