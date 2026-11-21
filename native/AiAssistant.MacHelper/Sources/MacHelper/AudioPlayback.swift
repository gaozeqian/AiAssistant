import Foundation
import AVFoundation

// MARK: - On-demand TTS feedback playback (S7-R2)
//
// Plays short float32 mono PCM buffers produced by the local TTS engine.
// Privacy discipline: samples arrive over the authenticated AF_UNIX channel and
// are rendered straight from memory; nothing is written to disk.
// The AVAudioEngine is created per utterance and stopped afterwards so no audio
// device stays open while the app is idle.

final class OnDemandAudioPlayback: @unchecked Sendable {
    private let stateLock = NSLock()
    private let renderQueue = DispatchQueue(label: "aiassistant.helper.tts.playback")
    private var activeEngine: AVAudioEngine?
    private var activePlayer: AVAudioPlayerNode?

    /// Validates the payload and enqueues playback. Returns immediately so the IPC loop
    /// (and its heartbeat) never blocks for the duration of the utterance.
    func play(sampleRateHz: Int?, float32PcmBase64: String?) -> Result<Void, MacAdapterError> {
        guard let sampleRateHz, sampleRateHz > 0, sampleRateHz <= 192_000 else {
            return .failure(.internalFailure("Invalid TTS sample rate."))
        }

        guard let float32PcmBase64,
              let data = Data(base64Encoded: float32PcmBase64),
              !data.isEmpty,
              data.count % MemoryLayout<Float>.size == 0 else {
            return .failure(.internalFailure("Invalid TTS PCM payload."))
        }

        let frameCount = data.count / MemoryLayout<Float>.size
        guard frameCount <= sampleRateHz * 30 else {
            return .failure(.internalFailure("TTS PCM payload exceeds the 30 second feedback limit."))
        }

        guard let format = AVAudioFormat(
                commonFormat: .pcmFormatFloat32,
                sampleRate: Double(sampleRateHz),
                channels: 1,
                interleaved: false),
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frameCount)),
              let channel = buffer.floatChannelData?[0] else {
            return .failure(.internalFailure("Unsupported TTS audio format."))
        }

        buffer.frameLength = AVAudioFrameCount(frameCount)
        data.withUnsafeBytes { raw in
            guard let base = raw.baseAddress?.assumingMemoryBound(to: Float.self) else { return }
            channel.update(from: base, count: frameCount)
        }

        renderQueue.async { [weak self] in
            self?.render(buffer)
        }

        return .success(())
    }

    /// Stops any in-flight utterance; used on helper shutdown and when speech is cancelled.
    func stop() {
        renderQueue.sync {
            stopActive()
        }
    }

    private func render(_ buffer: AVAudioPCMBuffer) {
        // A newer feedback utterance supersedes the previous one.
        stopActive()

        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: buffer.format)

        do {
            try engine.start()
        } catch {
            return
        }

        stateLock.lock()
        activeEngine = engine
        activePlayer = player
        stateLock.unlock()

        let finished = DispatchSemaphore(value: 0)
        player.scheduleBuffer(buffer, at: nil, options: []) {
            finished.signal()
        }
        player.play()
        _ = finished.wait(timeout: .now() + .seconds(30))

        stopActive()
    }

    private func stopActive() {
        stateLock.lock()
        let player = activePlayer
        let engine = activeEngine
        activePlayer = nil
        activeEngine = nil
        stateLock.unlock()

        player?.stop()
        engine?.stop()
    }
}
