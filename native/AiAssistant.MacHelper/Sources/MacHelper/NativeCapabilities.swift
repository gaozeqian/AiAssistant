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

private final class AsyncBox<T: Sendable>: @unchecked Sendable {
    var value: Result<T, any Error>?
}

// MARK: - TCC Permissions & Native macOS Adapters

enum MacNativeCapabilities {

    static func queryPermissions() -> MacPermissionsDto {
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
        }

        return MacForegroundWindowDto(
            appName: appName,
            windowTitle: windowTitle,
            bundleId: bundleId,
            windowId: Int64(pid)
        )
    }

    static func captureScreen(maxWidth: Int) -> Result<MacScreenFrameDto, MacAdapterError> {
        guard CGPreflightScreenCaptureAccess() else {
            return .failure(.permissionDenied("Screen Recording permission has not been granted."))
        }

        let clampedMaxWidth = max(320, min(maxWidth, 2560))
        let semaphore = DispatchSemaphore(value: 0)
        let box = AsyncBox<MacScreenFrameDto>()

        Task.detached {
            do {
                let shareable = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = shareable.displays.first else {
                    throw MacAdapterError.internalFailure("No active display available.")
                }

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

                let dto = MacScreenFrameDto(
                    width: cgImage.width,
                    height: cgImage.height,
                    perceptualHash: phash,
                    pngBase64: pngData.base64EncodedString()
                )
                box.value = .success(dto)
            } catch {
                box.value = .failure(error)
            }
            semaphore.signal()
        }

        if semaphore.wait(timeout: .now() + .seconds(2)) == .timedOut {
            return .failure(.internalFailure("ScreenCaptureKit timed out."))
        }

        switch box.value {
        case .success(let dto):
            return .success(dto)
        case .failure(let err as MacAdapterError):
            return .failure(err)
        case .failure(let err):
            return .failure(.internalFailure(String(describing: type(of: err))))
        case .none:
            return .failure(.internalFailure("No capture result produced."))
        }
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

enum MacAdapterError: Error, Sendable {
    case permissionDenied(String)
    case internalFailure(String)
}

// MARK: - Native FSEvents Whitelisted Directory Tracker & On-Demand Audio Tap

final class WhitelistedDirectoryTracker: @unchecked Sendable {
    private let lock = NSLock()
    private let eventQueue = DispatchQueue(label: "com.aiassistant.helper.fsevents")
    private var streams: [String: FSEventStreamRef] = [:]
    private var pendingChanges: [MacFileChangeDto] = []
    private let maxPendingChanges = 256

    func pollChanges(directories: [String]) -> [MacFileChangeDto] {
        synchronizeStreams(with: directories)
        lock.lock()
        let result = Array(pendingChanges.prefix(32))
        pendingChanges.removeFirst(result.count)
        lock.unlock()
        return result
    }

    func stop() {
        lock.lock()
        let activeStreams = Array(streams.values)
        streams.removeAll()
        pendingChanges.removeAll()
        lock.unlock()

        for stream in activeStreams {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
        }
    }

    private func synchronizeStreams(with directories: [String]) {
        let fm = FileManager.default
        let validDirectories = Set(directories.filter { path in
            var isDirectory: ObjCBool = false
            return fm.fileExists(atPath: path, isDirectory: &isDirectory) && isDirectory.boolValue
        })

        lock.lock()
        let obsolete = streams.keys.filter { !validDirectories.contains($0) }
        let obsoleteStreams = obsolete.compactMap { streams.removeValue(forKey: $0) }
        lock.unlock()
        for stream in obsoleteStreams {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
        }

        for directory in validDirectories {
            lock.lock()
            let alreadyTracked = streams[directory] != nil
            lock.unlock()
            if alreadyTracked {
                continue
            }

            var context = FSEventStreamContext()
            context.version = 0
            context.info = Unmanaged.passUnretained(self).toOpaque()

            let createFlags = FSEventStreamCreateFlags(
                kFSEventStreamCreateFlagUseCFTypes |
                kFSEventStreamCreateFlagFileEvents |
                kFSEventStreamCreateFlagNoDefer
            )

            guard let stream = FSEventStreamCreate(
                nil,
                { _, callbackInfo, eventCount, eventPaths, eventFlags, _ in
                    guard let callbackInfo else { return }
                    let tracker = Unmanaged<WhitelistedDirectoryTracker>
                        .fromOpaque(callbackInfo)
                        .takeUnretainedValue()
                    let paths = Unmanaged<CFArray>
                        .fromOpaque(eventPaths)
                        .takeUnretainedValue() as? [String] ?? []

                    for index in 0..<eventCount where index < paths.count {
                        tracker.enqueue(path: paths[index], flags: eventFlags[index])
                    }
                },
                &context,
                [directory] as CFArray,
                FSEventStreamEventId(kFSEventStreamEventIdSinceNow),
                0.35,
                createFlags
            ) else {
                continue
            }

            FSEventStreamSetDispatchQueue(stream, eventQueue)
            guard FSEventStreamStart(stream) else {
                FSEventStreamInvalidate(stream)
                FSEventStreamRelease(stream)
                continue
            }

            lock.lock()
            streams[directory] = stream
            lock.unlock()
        }
    }

    private func enqueue(path: String, flags: FSEventStreamEventFlags) {
        let fileName = URL(fileURLWithPath: path).lastPathComponent
        guard !fileName.isEmpty, !fileName.hasPrefix("."),
              (flags & FSEventStreamEventFlags(kFSEventStreamEventFlagItemIsFile)) != 0 else {
            return
        }

        let operation: String
        if (flags & FSEventStreamEventFlags(kFSEventStreamEventFlagItemRemoved)) != 0 {
            operation = "Deleted"
        } else if (flags & FSEventStreamEventFlags(kFSEventStreamEventFlagItemRenamed)) != 0 {
            operation = "Renamed"
        } else if (flags & FSEventStreamEventFlags(kFSEventStreamEventFlagItemCreated)) != 0 {
            operation = "Created"
        } else {
            operation = "Modified"
        }

        lock.lock()
        if pendingChanges.count >= maxPendingChanges {
            pendingChanges.removeFirst()
        }
        pendingChanges.append(MacFileChangeDto(fullPath: path, operation: operation))
        lock.unlock()
    }
}

private final class AudioTapConversionContext: @unchecked Sendable {
    let sampleRateRatio: Double
    let targetFormat: AVAudioFormat
    let converter: AVAudioConverter

    init(sampleRateRatio: Double, targetFormat: AVAudioFormat, converter: AVAudioConverter) {
        self.sampleRateRatio = sampleRateRatio
        self.targetFormat = targetFormat
        self.converter = converter
    }
}

private final class ConsumedPacketFlag: @unchecked Sendable {
    var value: Bool = false
}

private final class InputBufferBox: @unchecked Sendable {
    let buffer: AVAudioPCMBuffer
    init(_ buffer: AVAudioPCMBuffer) {
        self.buffer = buffer
    }
}

final class OnDemandAudioTap: @unchecked Sendable {
    private let lock = NSLock()
    private var engine: AVAudioEngine?
    private var bufferedSamples: [Float] = []
    private let maxBufferedSamples = 16_000 * 3 // Max 3s in-memory ring cap

    func pollChunk() -> Result<MacAudioChunkDto?, MacAdapterError> {
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .authorized else {
            stop()
            return .failure(.permissionDenied("Microphone permission has not been granted."))
        }

        if engine == nil {
            do {
                try startEngine()
            } catch {
                return .failure(.internalFailure("Failed starting AVAudioEngine."))
            }
        }

        lock.lock()
        let chunk = bufferedSamples
        bufferedSamples.removeAll(keepingCapacity: true)
        lock.unlock()

        guard !chunk.isEmpty else {
            return .success(nil)
        }

        let pcmData = chunk.withUnsafeBufferPointer { ptr in
            Data(buffer: ptr)
        }

        return .success(MacAudioChunkDto(
            sampleRateHz: 16_000,
            channels: 1,
            float32PcmBase64: pcmData.base64EncodedString()
        ))
    }

    func stop() {
        if let running = engine {
            running.inputNode.removeTap(onBus: 0)
            running.stop()
            engine = nil
        }
        lock.lock()
        bufferedSamples.removeAll()
        lock.unlock()
    }

    private func startEngine() throws {
        let newEngine = AVAudioEngine()
        let inputNode = newEngine.inputNode
        let hwFormat = inputNode.outputFormat(forBus: 0)
        guard let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: hwFormat, to: targetFormat) else {
            throw MacAdapterError.internalFailure("Unsupported microphone audio format.")
        }

        let ctx = AudioTapConversionContext(
            sampleRateRatio: 16_000.0 / max(1.0, hwFormat.sampleRate),
            targetFormat: targetFormat,
            converter: converter
        )

        inputNode.installTap(onBus: 0, bufferSize: 1024, format: hwFormat) { [weak self] pcmBuffer, _ in
            guard let self else { return }
            let outCapacity = AVAudioFrameCount(max(64, Int(Double(pcmBuffer.frameLength) * ctx.sampleRateRatio) + 32))
            guard let outBuffer = AVAudioPCMBuffer(pcmFormat: ctx.targetFormat, frameCapacity: outCapacity) else {
                return
            }

            var error: NSError?
            let consumed = ConsumedPacketFlag()
            let inBox = InputBufferBox(pcmBuffer)
            ctx.converter.convert(to: outBuffer, error: &error) { _, outStatus in
                if consumed.value {
                    outStatus.pointee = .noDataNow
                    return nil
                }
                consumed.value = true
                outStatus.pointee = .haveData
                return inBox.buffer
            }

            guard error == nil,
                  outBuffer.frameLength > 0,
                  let chPtr = outBuffer.floatChannelData?[0] else {
                return
            }

            let count = Int(outBuffer.frameLength)
            let slice = Array(UnsafeBufferPointer(start: chPtr, count: count))
            self.lock.lock()
            if self.bufferedSamples.count + slice.count > self.maxBufferedSamples {
                let excess = (self.bufferedSamples.count + slice.count) - self.maxBufferedSamples
                if excess < self.bufferedSamples.count {
                    self.bufferedSamples.removeFirst(excess)
                } else {
                    self.bufferedSamples.removeAll(keepingCapacity: true)
                }
            }
            self.bufferedSamples.append(contentsOf: slice)
            self.lock.unlock()
        }

        newEngine.prepare()
        try newEngine.start()
        engine = newEngine
    }
}
