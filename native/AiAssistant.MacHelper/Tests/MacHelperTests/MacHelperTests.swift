import Foundation
import XCTest
@testable import MacHelper

final class MacHelperTests: XCTestCase {
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    func testProtocolVersionMatchesWireContract() {
        XCTAssertEqual(ProtocolConstants.currentVersion, 1)
        XCTAssertEqual(ProtocolConstants.maxFrameBytes, 16 * 1024 * 1024)
        XCTAssertEqual(ProtocolConstants.authTokenEnvVar, "AIASSISTANT_IPC_TOKEN")
    }

    func testHandshakeRequestRoundTrips() throws {
        let request = MacIpcRequest(
            requestId: "hs_1",
            method: "handshake",
            protocolVersion: 1,
            authToken: "token",
            maxWidth: nil,
            windowId: nil,
            pngBase64: nil,
            directories: nil,
            sampleRateHz: nil,
            float32PcmBase64: nil)

        let decoded = try decoder.decode(MacIpcRequest.self, from: encoder.encode(request))
        XCTAssertEqual(decoded.requestId, "hs_1")
        XCTAssertEqual(decoded.method, "handshake")
        XCTAssertEqual(decoded.protocolVersion, 1)
        XCTAssertEqual(decoded.authToken, "token")
    }

    func testResponseRoundTripsPermissionsAndFrame() throws {
        let response = MacIpcResponse(
            requestId: "1",
            ok: true,
            protocolVersion: 1,
            permissions: MacPermissionsDto(
                screenRecording: "granted",
                accessibility: "denied",
                microphone: "unknown"),
            screenFrame: MacScreenFrameDto(
                width: 1280,
                height: 720,
                perceptualHash: 42,
                pngBase64: "iVBORw0KGgo="))

        let decoded = try decoder.decode(MacIpcResponse.self, from: encoder.encode(response))
        XCTAssertTrue(decoded.ok)
        XCTAssertEqual(decoded.permissions?.screenRecording, "granted")
        XCTAssertEqual(decoded.permissions?.accessibility, "denied")
        XCTAssertEqual(decoded.screenFrame?.width, 1280)
        XCTAssertEqual(decoded.screenFrame?.perceptualHash, 42)
    }

    func testPermissionsSnapshotMapsEveryField() throws {
        let dto = MacPermissionsDto(
            screenRecording: "granted",
            accessibility: "unknown",
            microphone: "denied")
        let decoded = try decoder.decode(MacPermissionsDto.self, from: encoder.encode(dto))
        XCTAssertEqual(decoded.screenRecording, "granted")
        XCTAssertEqual(decoded.accessibility, "unknown")
        XCTAssertEqual(decoded.microphone, "denied")
    }

    func testAudioChunkPreservesParameters() throws {
        let chunk = MacAudioChunkDto(
            sampleRateHz: 24_000,
            channels: 1,
            float32PcmBase64: "AAAAAA==")
        let decoded = try decoder.decode(MacAudioChunkDto.self, from: encoder.encode(chunk))
        XCTAssertEqual(decoded.sampleRateHz, 24_000)
        XCTAssertEqual(decoded.channels, 1)
        XCTAssertEqual(decoded.float32PcmBase64, "AAAAAA==")
    }

    func testConstantTimeEqualsRejectsEmptyAndMismatchedTokens() {
        XCTAssertTrue(FrameIO.constantTimeEquals("abc", "abc"))
        XCTAssertFalse(FrameIO.constantTimeEquals("abc", "abd"))
        XCTAssertFalse(FrameIO.constantTimeEquals("abc", "abcd"))
        XCTAssertFalse(FrameIO.constantTimeEquals("", ""))
    }
}
