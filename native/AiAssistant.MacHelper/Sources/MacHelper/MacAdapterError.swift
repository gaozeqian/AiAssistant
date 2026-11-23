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

enum MacAdapterError: Error, Sendable {
    case permissionDenied(String)
    case internalFailure(String)
}

