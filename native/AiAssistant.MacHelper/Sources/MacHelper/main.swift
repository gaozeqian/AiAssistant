import Foundation
import Darwin

// MARK: - macOS Swift Helper Entry Point & AF_UNIX IPC Server (ADR-0005, S3.1)

enum MacHelperServer {
    static func runCommandLine() -> Int32 {
        let args = CommandLine.arguments
        if args.contains("--smoke-check") || !args.contains("--socket-path") {
            let statusPayload: [String: String] = [
                "helper": "aiassistant-mac-helper",
                "protocolVersion": String(ProtocolConstants.currentVersion),
                "status": "ready"
            ]
            if let data = try? JSONSerialization.data(withJSONObject: statusPayload),
               let text = String(data: data, encoding: .utf8) {
                print(text)
            }
            return 0
        }

        guard let flagIdx = args.firstIndex(of: "--socket-path"),
              flagIdx + 1 < args.count else {
            fputs("Missing argument after --socket-path\n", stderr)
            return 2
        }

        let socketPath = args[flagIdx + 1]
        guard let expectedToken = ProcessInfo.processInfo.environment[ProtocolConstants.authTokenEnvVar],
              !expectedToken.isEmpty else {
            fputs("Missing one-time session token environment variable.\n", stderr)
            return 3
        }

        // Ignore SIGPIPE so closed client sockets return EPIPE cleanly instead of terminating process
        Darwin.signal(SIGPIPE, SIG_IGN)
        return serveSingleSession(socketPath: socketPath, expectedToken: expectedToken)
    }

    private static func serveSingleSession(socketPath: String, expectedToken: String) -> Int32 {
        Darwin.unlink(socketPath)
        let serverFd = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard serverFd >= 0 else {
            return 10
        }
        defer {
            Darwin.close(serverFd)
            Darwin.unlink(socketPath)
        }

        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let pathBytes = Array(socketPath.utf8CString)
        let pathCopied: Bool = withUnsafeMutableBytes(of: &addr.sun_path) { rawBuf in
            guard pathBytes.count <= rawBuf.count else {
                return false
            }
            for i in 0..<pathBytes.count {
                rawBuf[i] = UInt8(bitPattern: pathBytes[i])
            }
            return true
        }

        guard pathCopied else {
            fputs("Socket path exceeds AF_UNIX sun_path limit.\n", stderr)
            return 11
        }

        let bindRc = withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                Darwin.bind(serverFd, sockPtr, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard bindRc == 0 else {
            return 12
        }

        // Restrict socket file permissions strictly to the current user (0600)
        _ = Darwin.chmod(socketPath, mode_t(0o600))

        guard Darwin.listen(serverFd, 1) == 0 else {
            return 13
        }

        let clientFd = Darwin.accept(serverFd, nil, nil)
        guard clientFd >= 0 else {
            return 14
        }
        defer {
            Darwin.close(clientFd)
        }

        let decoder = JSONDecoder()
        let encoder = JSONEncoder()
        let fsTracker = WhitelistedDirectoryTracker()
        let audioTap = OnDemandAudioTap()
        defer {
            audioTap.stop()
            fsTracker.stop()
        }

        // First frame MUST be a valid handshake with matching version and one-time auth token
        guard let firstReq = FrameIO.readFrame(fd: clientFd, decoder: decoder),
              firstReq.method == "handshake" else {
            let reject = MacIpcResponse(requestId: "hs", ok: false, errorCode: "unauthorized", errorMessage: "Handshake required.")
            FrameIO.writeFrame(fd: clientFd, response: reject, encoder: encoder)
            return 20
        }

        guard let peerToken = firstReq.authToken,
              FrameIO.constantTimeEquals(peerToken, expectedToken) else {
            let reject = MacIpcResponse(requestId: firstReq.requestId, ok: false, errorCode: "unauthorized", errorMessage: "Invalid session token.")
            FrameIO.writeFrame(fd: clientFd, response: reject, encoder: encoder)
            return 21
        }

        if firstReq.protocolVersion != ProtocolConstants.currentVersion {
            let mismatch = MacIpcResponse(
                requestId: firstReq.requestId,
                ok: false,
                protocolVersion: ProtocolConstants.currentVersion,
                errorCode: "unsupported_version",
                errorMessage: "Incompatible IPC protocol version."
            )
            FrameIO.writeFrame(fd: clientFd, response: mismatch, encoder: encoder)
            return 22
        }

        let hsOk = MacIpcResponse(
            requestId: firstReq.requestId,
            ok: true,
            protocolVersion: ProtocolConstants.currentVersion,
            permissions: MacNativeCapabilities.queryPermissions()
        )
        guard FrameIO.writeFrame(fd: clientFd, response: hsOk, encoder: encoder) else {
            return 0
        }

        // Authenticated command loop
        while let req = FrameIO.readFrame(fd: clientFd, decoder: decoder) {
            if req.method == "shutdown" {
                let ack = MacIpcResponse(requestId: req.requestId, ok: true)
                FrameIO.writeFrame(fd: clientFd, response: ack, encoder: encoder)
                break
            }

            let resp = dispatchRequest(req, fsTracker: fsTracker, audioTap: audioTap)
            if !FrameIO.writeFrame(fd: clientFd, response: resp, encoder: encoder) {
                break
            }
        }

        return 0
    }

    private static func dispatchRequest(
        _ req: MacIpcRequest,
        fsTracker: WhitelistedDirectoryTracker,
        audioTap: OnDemandAudioTap
    ) -> MacIpcResponse {
        switch req.method {
        case "ping", "permissions.query":
            return MacIpcResponse(
                requestId: req.requestId,
                ok: true,
                protocolVersion: ProtocolConstants.currentVersion,
                permissions: MacNativeCapabilities.queryPermissions()
            )

        case "window.foreground":
            let win = MacNativeCapabilities.inspectForegroundWindow()
            return MacIpcResponse(
                requestId: req.requestId,
                ok: win != nil,
                permissions: MacNativeCapabilities.queryPermissions(),
                window: win
            )

        case "screen.capture":
            let result = MacNativeCapabilities.captureScreen(maxWidth: req.maxWidth ?? 1280)
            switch result {
            case .success(let frame):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: true,
                    permissions: MacNativeCapabilities.queryPermissions(),
                    screenFrame: frame
                )
            case .failure(.permissionDenied(let msg)):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: false,
                    errorCode: "permission_denied",
                    errorMessage: msg,
                    permissions: MacNativeCapabilities.queryPermissions()
                )
            case .failure(.internalFailure(let msg)):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: false,
                    errorCode: "internal_error",
                    errorMessage: msg
                )
            }

        case "text.extract":
            let result = MacNativeCapabilities.extractText(pidOrWindowId: req.windowId ?? 0, pngBase64: req.pngBase64)
            switch result {
            case .success(let extracted):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: true,
                    extractedText: extracted
                )
            case .failure(.permissionDenied(let msg)):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: false,
                    errorCode: "permission_denied",
                    errorMessage: msg
                )
            case .failure(.internalFailure(let msg)):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: false,
                    errorCode: "internal_error",
                    errorMessage: msg
                )
            }

        case "fs.poll":
            let changes = fsTracker.pollChanges(directories: req.directories ?? [])
            return MacIpcResponse(
                requestId: req.requestId,
                ok: true,
                fileChanges: changes
            )

        case "audio.poll":
            let result = audioTap.pollChunk()
            switch result {
            case .success(let chunk):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: true,
                    permissions: MacNativeCapabilities.queryPermissions(),
                    audioChunk: chunk
                )
            case .failure(.permissionDenied(let msg)):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: false,
                    errorCode: "permission_denied",
                    errorMessage: msg,
                    permissions: MacNativeCapabilities.queryPermissions()
                )
            case .failure(.internalFailure(let msg)):
                return MacIpcResponse(
                    requestId: req.requestId,
                    ok: false,
                    errorCode: "internal_error",
                    errorMessage: msg
                )
            }

        default:
            return MacIpcResponse(
                requestId: req.requestId,
                ok: false,
                errorCode: "internal_error",
                errorMessage: "Unknown IPC method."
            )
        }
    }
}

exit(MacHelperServer.runCommandLine())
