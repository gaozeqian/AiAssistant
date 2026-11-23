import Foundation
import AppKit
import ApplicationServices
import AVFoundation
import CoreGraphics
import CoreServices
import ImageIO
import ScreenCaptureKit
import UniformTypeIdentifiers
import Vision

final class AsyncBox<T: Sendable>: @unchecked Sendable {
    var value: Result<T, any Error>?
}

/// Lock-protected TCC snapshot shared by the IPC loop and its background refresher.
final class PermissionCache: @unchecked Sendable {
    private let lock = NSLock()
    private var snapshot = MacPermissionsDto(
        screenRecording: "unknown",
        accessibility: "unknown",
        microphone: "unknown"
    )
    private var refreshing = false

    func current() -> MacPermissionsDto {
        lock.lock()
        defer { lock.unlock() }
        return snapshot
    }

    func store(_ value: MacPermissionsDto) {
        lock.lock()
        snapshot = value
        lock.unlock()
    }

    func tryBeginRefresh() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if refreshing {
            return false
        }
        refreshing = true
        return true
    }

    func finishRefresh(_ value: MacPermissionsDto) {
        lock.lock()
        snapshot = value
        refreshing = false
        lock.unlock()
    }
}

// MARK: - TCC Permissions & Native macOS Adapters

/// Fires the macOS TCC prompts the first time a capability is actually used.
///
/// Without this the helper only preflights, so a fresh install would show "permission required"
/// forever and never tell the user that a dialog is waiting (S3-R1). Requests run off the IPC
/// thread because `CGRequestScreenCaptureAccess` blocks until the user answers the dialog.
final class PermissionPrompter: @unchecked Sendable {
    static let shared = PermissionPrompter()

    private let lock = NSLock()
    private let queue = DispatchQueue(label: "aiassistant.helper.tcc.prompts", qos: .userInitiated)
    private var requestedScreenRecording = false
    private var requestedMicrophone = false
    private var requestedAccessibility = false

    func requestScreenRecordingIfNeeded() {
        guard markScreenRecording() else { return }
        queue.async {
            _ = CGRequestScreenCaptureAccess()
        }
    }

    func requestMicrophoneIfNeeded() {
        guard markMicrophone() else { return }
        queue.async {
            AVCaptureDevice.requestAccess(for: .audio) { _ in }
        }
    }

    func requestAccessibilityIfNeeded() {
        guard markAccessibility() else { return }
        queue.async {
            // Literal key avoids Swift 6 concurrency complaints about the kAXTrustedCheckOptionPrompt global.
            let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
        }
    }

    private func markScreenRecording() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if requestedScreenRecording { return false }
        requestedScreenRecording = true
        return true
    }

    private func markMicrophone() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if requestedMicrophone { return false }
        requestedMicrophone = true
        return true
    }

    private func markAccessibility() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if requestedAccessibility { return false }
        requestedAccessibility = true
        return true
    }
}
