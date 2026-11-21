import Foundation
import Darwin

// MARK: - Wire Protocol v1 DTOs (ADR-0005, S3.1)

enum ProtocolConstants {
    static let currentVersion: Int = 1
    static let maxFrameBytes: Int = 16 * 1024 * 1024
    static let authTokenEnvVar: String = "AIASSISTANT_IPC_TOKEN"
}

struct MacIpcRequest: Codable, Sendable {
    let requestId: String
    let method: String
    let protocolVersion: Int?
    let authToken: String?
    let maxWidth: Int?
    let windowId: Int64?
    let pngBase64: String?
    let directories: [String]?
    let sampleRateHz: Int?
    let float32PcmBase64: String?
}

struct MacPermissionsDto: Codable, Sendable {
    let screenRecording: String
    let accessibility: String
    let microphone: String
}

struct MacForegroundWindowDto: Codable, Sendable {
    let appName: String
    let windowTitle: String
    let bundleId: String?
    let windowId: Int64
}

struct MacScreenFrameDto: Codable, Sendable {
    let width: Int
    let height: Int
    let perceptualHash: UInt64
    let pngBase64: String
}

struct MacExtractedTextDto: Codable, Sendable {
    let text: String
    let source: String
    let confidence: Double
}

struct MacFileChangeDto: Codable, Sendable {
    let fullPath: String
    let operation: String
}

struct MacAudioChunkDto: Codable, Sendable {
    let sampleRateHz: Int
    let channels: Int
    let float32PcmBase64: String
}

struct MacIpcResponse: Codable, Sendable {
    let requestId: String
    let ok: Bool
    var protocolVersion: Int? = nil
    var errorCode: String? = nil
    var errorMessage: String? = nil
    var permissions: MacPermissionsDto? = nil
    var window: MacForegroundWindowDto? = nil
    var screenFrame: MacScreenFrameDto? = nil
    var extractedText: MacExtractedTextDto? = nil
    var fileChanges: [MacFileChangeDto]? = nil
    var audioChunk: MacAudioChunkDto? = nil
}

// MARK: - Framing & Constant-Time Token Verification

enum FrameIO {
    static func constantTimeEquals(_ lhs: String, _ rhs: String) -> Bool {
        let a = Array(lhs.utf8)
        let b = Array(rhs.utf8)
        guard a.count == b.count && !a.isEmpty else {
            return false
        }
        var diff: UInt8 = 0
        for i in 0..<a.count {
            diff |= (a[i] ^ b[i])
        }
        return diff == 0
    }

    static func readFrame(fd: Int32, decoder: JSONDecoder) -> MacIpcRequest? {
        var header = [UInt8](repeating: 0, count: 4)
        guard readExact(fd: fd, into: &header, count: 4) else {
            return nil
        }

        let length = (Int(header[0]) << 24)
                   | (Int(header[1]) << 16)
                   | (Int(header[2]) << 8)
                   | Int(header[3])

        guard length > 0 && length <= ProtocolConstants.maxFrameBytes else {
            return nil
        }

        var payload = [UInt8](repeating: 0, count: length)
        guard readExact(fd: fd, into: &payload, count: length) else {
            return nil
        }

        return try? decoder.decode(MacIpcRequest.self, from: Data(payload))
    }

    @discardableResult
    static func writeFrame(fd: Int32, response: MacIpcResponse, encoder: JSONEncoder) -> Bool {
        guard let data = try? encoder.encode(response),
              !data.isEmpty,
              data.count <= ProtocolConstants.maxFrameBytes else {
            return false
        }

        let len = UInt32(data.count)
        let header: [UInt8] = [
            UInt8((len >> 24) & 0xFF),
            UInt8((len >> 16) & 0xFF),
            UInt8((len >> 8) & 0xFF),
            UInt8(len & 0xFF)
        ]

        guard writeAll(fd: fd, bytes: header) else {
            return false
        }

        return writeAll(fd: fd, bytes: Array(data))
    }

    private static func readExact(fd: Int32, into buffer: inout [UInt8], count: Int) -> Bool {
        var offset = 0
        while offset < count {
            let n = buffer.withUnsafeMutableBytes { rawPtr -> Int in
                guard let base = rawPtr.baseAddress else { return -1 }
                return Darwin.read(fd, base.advanced(by: offset), count - offset)
            }
            if n < 0 {
                if errno == EINTR { continue }
                return false
            }
            if n == 0 {
                return false
            }
            offset += n
        }
        return true
    }

    private static func writeAll(fd: Int32, bytes: [UInt8]) -> Bool {
        var offset = 0
        while offset < bytes.count {
            let n = bytes.withUnsafeBytes { rawPtr -> Int in
                guard let base = rawPtr.baseAddress else { return -1 }
                return Darwin.write(fd, base.advanced(by: offset), bytes.count - offset)
            }
            if n < 0 {
                if errno == EINTR { continue }
                return false
            }
            if n == 0 {
                return false
            }
            offset += n
        }
        return true
    }
}
