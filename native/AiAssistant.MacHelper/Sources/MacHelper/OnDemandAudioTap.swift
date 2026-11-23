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
            PermissionPrompter.shared.requestMicrophoneIfNeeded()
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
