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
