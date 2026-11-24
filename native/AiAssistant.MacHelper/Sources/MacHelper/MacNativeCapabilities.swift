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

enum MacNativeCapabilities {
    private static let permissionRefreshQueue = DispatchQueue(label: "aiassistant.helper.tcc.permissions")
    private static let permissionCache = PermissionCache()

    /// Non-blocking snapshot for the IPC loop.
    ///
    /// `AVCaptureDevice.authorizationStatus` alone costs 1–1.6 s whenever tccd is cold, so a
    /// synchronous TCC read on every `ping` stalled all other requests (measured on macOS 26,
    /// S7-R2). The cached value is returned immediately and refreshed on a background queue.
    static func queryPermissions() -> MacPermissionsDto {
        schedulePermissionRefresh()
        return permissionCache.current()
    }

    /// Blocking refresh for the handshake and explicit diagnostics requests.
    static func queryPermissionsBlocking() -> MacPermissionsDto {
        let snapshot = readPermissions()
        permissionCache.store(snapshot)
        return snapshot
    }

    private static func schedulePermissionRefresh() {
        guard permissionCache.tryBeginRefresh() else {
            return
        }

        permissionRefreshQueue.async {
            let snapshot = readPermissions()
            permissionCache.finishRefresh(snapshot)
        }
    }

    private static func readPermissions() -> MacPermissionsDto {
        let screenGranted = CGPreflightScreenCaptureAccess()
        let axGranted = AXIsProcessTrusted()
        let micStatus: String
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            micStatus = "granted"
        case .denied, .restricted:
            micStatus = "denied"
        case .notDetermined:
            micStatus = "not_determined"
        @unknown default:
            micStatus = "denied"
        }

        return MacPermissionsDto(
            screenRecording: screenGranted ? "granted" : "denied",
            accessibility: axGranted ? "granted" : "denied",
            microphone: micStatus
        )
    }

    static func inspectForegroundWindow() -> MacForegroundWindowDto? {
        guard let frontApp = NSWorkspace.shared.frontmostApplication else {
            return nil
        }

        let appName = frontApp.localizedName ?? frontApp.bundleIdentifier ?? "Unknown"
        let bundleId = frontApp.bundleIdentifier
        let pid = frontApp.processIdentifier
        var windowTitle = appName

        if AXIsProcessTrusted() {
            let appElement = AXUIElementCreateApplication(pid)
            var focusedWinRef: CFTypeRef?
            if AXUIElementCopyAttributeValue(appElement, kAXFocusedWindowAttribute as CFString, &focusedWinRef) == .success,
               let winObj = focusedWinRef {
                // CFTypeRef returned by kAXFocusedWindowAttribute is an AXUIElement
                let winElement = unsafeBitCast(winObj, to: AXUIElement.self)
                var titleRef: CFTypeRef?
                if AXUIElementCopyAttributeValue(winElement, kAXTitleAttribute as CFString, &titleRef) == .success,
                   let title = titleRef as? String,
                   !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    windowTitle = title.trimmingCharacters(in: .whitespacesAndNewlines)
                }
            }
        } else {
            // Window titles stay degraded (app name only) until the user grants Accessibility;
            // opening the settings pane once is the only actionable hint we can give.
            PermissionPrompter.shared.requestAccessibilityIfNeeded()
        }

        return MacForegroundWindowDto(
            appName: appName,
            windowTitle: windowTitle,
            bundleId: bundleId,
            windowId: Int64(pid)
        )
    }

    /// Captures every connected display. The display hosting the frontmost window is flagged so the
    /// host can keep window context on that record and store the other monitors as desktop snapshots.
    static func captureScreens(maxWidth: Int) -> Result<[MacScreenFrameDto], MacAdapterError> {
        guard CGPreflightScreenCaptureAccess() else {
            PermissionPrompter.shared.requestScreenRecordingIfNeeded()
            return .failure(.permissionDenied("Screen Recording permission has not been granted; approve the system prompt or enable it in System Settings."))
        }

        let clampedMaxWidth = max(320, min(maxWidth, 2560))
        let semaphore = DispatchSemaphore(value: 0)
        let box = AsyncBox<[MacScreenFrameDto]>()

        Task.detached {
            do {
                let shareable = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard !shareable.displays.isEmpty else {
                    throw MacAdapterError.internalFailure("No active display available.")
                }

                let foregroundBounds = foregroundWindowBounds()
                var foregroundDisplayId: CGDirectDisplayID? = nil
                if let bounds = foregroundBounds {
                    var bestArea: CGFloat = 0
                    for display in shareable.displays {
                        let area = intersectionArea(display.frame, bounds)
                        if area > bestArea {
                            bestArea = area
                            foregroundDisplayId = display.displayID
                        }
                    }
                }
                if foregroundDisplayId == nil {
                    foregroundDisplayId = CGMainDisplayID()
                }

                var frames: [MacScreenFrameDto] = []
                for (index, display) in shareable.displays.enumerated() {
                    let srcWidth = max(1, display.width)
                    let srcHeight = max(1, display.height)
                    let targetWidth = min(srcWidth, clampedMaxWidth)
                    let targetHeight = max(1, Int((Double(srcHeight) * Double(targetWidth) / Double(srcWidth)).rounded()))

                    let filter = SCContentFilter(display: display, excludingWindows: [])
                    let config = SCStreamConfiguration()
                    config.width = targetWidth
                    config.height = targetHeight
                    config.showsCursor = false

                    let cgImage = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
                    let phash = computeDifferenceHash64(from: cgImage)
                    guard let pngData = encodePng(from: cgImage) else {
                        throw MacAdapterError.internalFailure("Failed encoding CGImage as PNG.")
                    }

                    let frame = display.frame
                    frames.append(MacScreenFrameDto(
                        width: cgImage.width,
                        height: cgImage.height,
                        perceptualHash: phash,
                        pngBase64: pngData.base64EncodedString(),
                        displayId: "display-\(display.displayID)",
                        displayName: "\(index + 1)",
                        displayLeft: Int(frame.origin.x.rounded()),
                        displayTop: Int(frame.origin.y.rounded()),
                        displayWidth: Int(frame.width.rounded()),
                        displayHeight: Int(frame.height.rounded()),
                        isPrimary: display.displayID == CGMainDisplayID(),
                        isForegroundDisplay: display.displayID == (foregroundDisplayId ?? 0)
                    ))
                }

                box.value = .success(frames)
            } catch {
                box.value = .failure(error)
            }
            semaphore.signal()
        }

        if semaphore.wait(timeout: .now() + .seconds(2)) == .timedOut {
            return .failure(.internalFailure("ScreenCaptureKit timed out."))
        }

        switch box.value {
        case .success(let frames):
            return .success(frames)
        case .failure(let err as MacAdapterError):
            return .failure(err)
        case .failure(let err):
            return .failure(.internalFailure(String(describing: type(of: err))))
        case .none:
            return .failure(.internalFailure("No capture result produced."))
        }
    }

    /// Bounds of the frontmost application's first on-screen window, in global display coordinates.
    private static func foregroundWindowBounds() -> CGRect? {
        guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier else {
            return nil
        }

        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let windows = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
            return nil
        }

        for window in windows {
            guard let owner = window[kCGWindowOwnerPID as String] as? pid_t, owner == pid else {
                continue
            }
            guard let boundsDictionary = window[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsDictionary),
                  bounds.width > 1,
                  bounds.height > 1 else {
                continue
            }
            return bounds
        }

        return nil
    }

    private static func intersectionArea(_ lhs: CGRect, _ rhs: CGRect) -> CGFloat {
        let rect = lhs.intersection(rhs)
        if rect.isNull || rect.isEmpty {
            return 0
        }
        return rect.width * rect.height
    }

    static func extractText(pidOrWindowId: Int64, pngBase64: String?) -> Result<MacExtractedTextDto, MacAdapterError> {
        // 1. Accessibility-first (strictly excluding AXSecureTextField password inputs!)
        if AXIsProcessTrusted() && pidOrWindowId > 0 {
            let appElement = AXUIElementCreateApplication(pid_t(pidOrWindowId))
            var focusedWinRef: CFTypeRef?
            if AXUIElementCopyAttributeValue(appElement, kAXFocusedWindowAttribute as CFString, &focusedWinRef) == .success,
               let winObj = focusedWinRef {
                let winElement = unsafeBitCast(winObj, to: AXUIElement.self)
                var fragments: [String] = []
                var visited = 0
                collectSafeAccessibleStrings(element: winElement, depth: 0, visited: &visited, output: &fragments)
                let joined = fragments.joined(separator: " | ")
                if !joined.isEmpty {
                    return .success(MacExtractedTextDto(text: joined, source: "accessibility", confidence: 0.95))
                }
            }
        }

        // 2. Vision OCR fallback when screenshot bytes are supplied
        if let b64 = pngBase64, !b64.isEmpty, let imageData = Data(base64Encoded: b64) {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            request.recognitionLanguages = ["zh-Hans", "zh-Hant", "en-US"]

            let handler = VNImageRequestHandler(data: imageData, options: [:])
            do {
                try handler.perform([request])
                if let observations = request.results, !observations.isEmpty {
                    var lines: [String] = []
                    var confidenceSum = 0.0
                    for obs in observations {
                        if let top = obs.topCandidates(1).first {
                            let trimmed = top.string.trimmingCharacters(in: .whitespacesAndNewlines)
                            if !trimmed.isEmpty {
                                lines.append(trimmed)
                                confidenceSum += Double(top.confidence)
                            }
                        }
                    }
                    if !lines.isEmpty {
                        let avgConf = confidenceSum / Double(lines.count)
                        return .success(MacExtractedTextDto(
                            text: String(lines.joined(separator: "\n").prefix(2048)),
                            source: "ocr",
                            confidence: avgConf
                        ))
                    }
                }
            } catch {
                return .failure(.internalFailure("Vision OCR failed."))
            }
        }

        return .success(MacExtractedTextDto(text: "", source: "none", confidence: 0.0))
    }

    private static func collectSafeAccessibleStrings(
        element: AXUIElement,
        depth: Int,
        visited: inout Int,
        output: inout [String]
    ) {
        if depth > 6 || visited >= 140 {
            return
        }
        visited += 1

        var roleRef: CFTypeRef?
        let role = (AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &roleRef) == .success)
            ? (roleRef as? String ?? "")
            : ""

        var subroleRef: CFTypeRef?
        let subrole = (AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subroleRef) == .success)
            ? (subroleRef as? String ?? "")
            : ""

        // Privacy boundary: NEVER inspect password or secure text fields!
        if role == "AXSecureTextField" || subrole == (kAXSecureTextFieldSubrole as String) {
            return
        }

        for attr in [kAXTitleAttribute as CFString, kAXValueAttribute as CFString] {
            var valRef: CFTypeRef?
            if AXUIElementCopyAttributeValue(element, attr, &valRef) == .success,
               let text = valRef as? String {
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                if trimmed.count > 1 && trimmed.count <= 300 && !output.contains(trimmed) {
                    output.append(trimmed)
                }
            }
        }

        var childrenRef: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXChildrenAttribute as CFString, &childrenRef) == .success,
           let rawChildren = childrenRef as? [AnyObject] {
            for childObj in rawChildren {
                if visited >= 140 { break }
                if CFGetTypeID(childObj) == AXUIElementGetTypeID() {
                    let childElement = unsafeBitCast(childObj, to: AXUIElement.self)
                    collectSafeAccessibleStrings(element: childElement, depth: depth + 1, visited: &visited, output: &output)
                }
            }
        }
    }

    private static func computeDifferenceHash64(from image: CGImage) -> UInt64 {
        let width = 9
        let height = 8
        var pixels = [UInt8](repeating: 0, count: width * height)
        let colorSpace = CGColorSpaceCreateDeviceGray()
        guard let ctx = CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else {
            return 0
        }

        ctx.interpolationQuality = .low
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))

        var hash: UInt64 = 0
        var bit = 0
        for y in 0..<height {
            let rowOffset = y * width
            for x in 0..<(width - 1) {
                if pixels[rowOffset + x] > pixels[rowOffset + x + 1] {
                    hash |= (UInt64(1) << UInt64(bit))
                }
                bit += 1
            }
        }
        return hash
    }

    private static func encodePng(from image: CGImage) -> Data? {
        let mutableData = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(
            mutableData as CFMutableData,
            UTType.png.identifier as CFString,
            1,
            nil
        ) else {
            return nil
        }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest) else {
            return nil
        }
        return mutableData as Data
    }
}
