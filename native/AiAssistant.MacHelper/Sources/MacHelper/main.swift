import Foundation

// S0 Skeleton for macOS Native Helper (ADR-0005).
// S3 will implement versioned Unix Domain Socket IPC + ScreenCaptureKit / Vision / CoreAudio / AX adapters.
struct HandshakeResponse: Codable {
    let protocolVersion: Int
    let status: String
}

let response = HandshakeResponse(protocolVersion: 1, status: "stub-ready")
if let data = try? JSONEncoder().encode(response),
   let json = String(data: data, encoding: .utf8) {
    print(json)
}
