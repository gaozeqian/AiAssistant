// AX + 截图/OCR + 坐标点击：最接近真人的 UI 端到端测试。
// - 用 AXUIElement 找窗口、读可见文本（Avalonia 只暴露窗口 + TextBlock 文本）。
// - 用 Vision OCR 在窗口截图中定位按钮文本 → 真实 CGEvent 坐标点击。
//
// 前置条件（一次性授权给运行本脚本的宿主进程）：
//   系统设置 → 隐私与安全性 → 辅助功能：勾选
//   系统设置 → 隐私与安全性 → 屏幕与系统音频录制：勾选（截图/OCR 用）
//
// 用法：
//   swift ax-e2e.swift probe          # 打印窗口 AX 树
//   swift ax-e2e.swift run            # 窗口文本断言 + OCR 定位点击「暂停采集/恢复采集」
import ApplicationServices
import AppKit
import Foundation
import CoreGraphics
import Vision

let appBundlePath = CommandLine.arguments.count > 2
    ? CommandLine.arguments[2]
    : "/Users/gaozeqian/Repos/AiAssistant/artifacts/packages/staging/osx-arm64/AiAssistant.app"
let bundleId = "com.aiassistant.desktop"
let dataDir = "/tmp/aia-ax-e2e"
let windowTitle = "时光 · AI 个人活动追踪助手"

func attr(_ el: AXUIElement, _ name: String) -> AnyObject? {
    var value: AnyObject?
    guard AXUIElementCopyAttributeValue(el, name as CFString, &value) == .success else { return nil }
    return value
}
func role(_ el: AXUIElement) -> String { attr(el, kAXRoleAttribute) as? String ?? "" }
func title(_ el: AXUIElement) -> String {
    (attr(el, kAXTitleAttribute) as? String)
        ?? (attr(el, kAXDescriptionAttribute) as? String)
        ?? (attr(el, kAXValueAttribute) as? String)
        ?? ""
}
func children(_ el: AXUIElement) -> [AXUIElement] { attr(el, kAXChildrenAttribute) as? [AXUIElement] ?? [] }
func descendants(_ el: AXUIElement, role r: String) -> [AXUIElement] {
    var result: [AXUIElement] = []
    if role(el) == r { result.append(el) }
    for child in children(el) { result += descendants(child, role: r) }
    return result
}

func dumpTree(_ el: AXUIElement, _ depth: Int, _ maxDepth: Int) {
    if depth > maxDepth { return }
    let r = role(el); let t = title(el)
    if !r.isEmpty || !t.isEmpty { print("\(String(repeating: "  ", count: depth))\(r) | \(t)") }
    for child in children(el) { dumpTree(child, depth + 1, maxDepth) }
}

func window(of pid: pid_t, matching matchTitle: String) -> AXUIElement? {
    let app = AXUIElementCreateApplication(pid)
    let windows = attr(app, kAXWindowsAttribute) as? [AXUIElement] ?? []
    for w in windows where title(w).contains(matchTitle) { return w }
    return windows.first
}

func windowRect(_ win: AXUIElement) -> CGRect? {
    var pos = CGPoint.zero; var size = CGSize.zero
    guard let posVal = attr(win, kAXPositionAttribute), AXValueGetType(posVal as! AXValue) == .cgPoint,
          let sizeVal = attr(win, kAXSizeAttribute), AXValueGetType(sizeVal as! AXValue) == .cgSize,
          AXValueGetValue(posVal as! AXValue, .cgPoint, &pos),
          AXValueGetValue(sizeVal as! AXValue, .cgSize, &size) else { return nil }
    return CGRect(origin: pos, size: size)
}

func clickAt(_ point: CGPoint) {
    let src = CGEventSource(stateID: .hidSystemState)
    let down = CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: point, mouseButton: .left)
    let up = CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: point, mouseButton: .left)
    down?.post(tap: .cghidEventTap)
    up?.post(tap: .cghidEventTap)
}

/// 在窗口区域内截图（screencapture 按点坐标，Retina 下按像素缩放），用 Vision OCR 定位文本中心（屏幕坐标）。
func locateText(_ text: String, in rect: CGRect) -> CGPoint? {
    let path = "/tmp/aia-ocr.png"
    let rectArg = "\(Int(rect.origin.x)),\(Int(rect.origin.y)),\(Int(rect.width)),\(Int(rect.height))"
    let proc = Process()
    proc.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    proc.arguments = ["-x", "-R", rectArg, path]
    try? proc.run(); proc.waitUntilExit()

    guard let image = NSImage(contentsOfFile: path)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
        return nil
    }
    let scale = CGFloat(image.width) / rect.width   // Retina: image.width > rect.width
    let request = VNRecognizeTextRequest()
    request.recognitionLanguages = ["zh-Hans", "en-US"]
    request.recognitionLevel = .accurate
    let handler = VNImageRequestHandler(cgImage: image, options: [:])
    try? handler.perform([request])
    for obs in request.results ?? [] {
        guard let candidate = obs.topCandidates(1).first, candidate.string.contains(text) else { continue }
        let box = obs.boundingBox   // normalized, origin bottom-left
        let cx = (box.origin.x + box.width / 2) * CGFloat(image.width) / scale
        let cy = (1 - box.origin.y - box.height / 2) * CGFloat(image.height) / scale
        return CGPoint(x: rect.origin.x + cx, y: rect.origin.y + cy)
    }
    return nil
}

func waitUntil(_ timeout: TimeInterval, _ predicate: () -> Bool) -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if predicate() { return true }
        Thread.sleep(forTimeInterval: 0.3)
    }
    return predicate()
}

struct Result {
    var pass = 0; var fail = 0
    mutating func check(_ name: String, _ ok: Bool) {
        print("\(ok ? "PASS" : "FAIL")  \(name)")
        if ok { pass += 1 } else { fail += 1 }
    }
}

var launchedPid: pid_t?

func launchApp() -> pid_t? {
    let proc = Process()
    proc.executableURL = URL(fileURLWithPath: "/usr/bin/open")
    proc.arguments = ["-n", appBundlePath, "--args", "--show-window", "--data-directory", dataDir]
    try? proc.run()
    let deadline = Date().addingTimeInterval(25)
    while Date() < deadline {
        if let app = NSRunningApplication.runningApplications(withBundleIdentifier: bundleId).first {
            launchedPid = app.processIdentifier
            if window(of: app.processIdentifier, matching: windowTitle) != nil { return app.processIdentifier }
        }
        Thread.sleep(forTimeInterval: 0.5)
    }
    return launchedPid
}

let mode = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "probe"

guard AXIsProcessTrusted() else {
    print("FAIL  辅助功能未授权：请在 系统设置 → 隐私与安全性 → 辅助功能 中勾选运行本脚本的宿主进程后重试。")
    exit(2)
}

guard let pid = launchApp(), let win = window(of: pid, matching: windowTitle) else {
    print("FAIL  未找到 AiAssistant 窗口（appBundle=\(appBundlePath)）")
    if let pid = launchedPid { kill(pid, SIGTERM) }
    exit(3)
}
print("找到窗口 pid=\(pid) title=\(title(win))")

if mode == "probe" {
    dumpTree(win, 0, 6)
    if let pid = launchedPid { kill(pid, SIGTERM) }
    exit(0)
}

var result = Result()
func bannerTexts() -> [String] { descendants(win, role: kAXStaticTextRole).map(title) }

result.check("窗口标题正确", title(win).contains(windowTitle))
result.check("顶部显示「正在本机采集」", bannerTexts().contains { $0.contains("正在本机采集") })
result.check("顶部显示「语音休眠」", bannerTexts().contains { $0.contains("语音休眠") })

// OCR + 坐标点击：暂停采集 → 横幅变「已暂停」→ 恢复采集
guard let rect = windowRect(win) else {
    print("FAIL  无法读取窗口边界")
    if let pid = launchedPid { kill(pid, SIGTERM) }
    exit(4)
}
NSRunningApplication(processIdentifier: pid)?.activate(options: [.activateIgnoringOtherApps])
Thread.sleep(forTimeInterval: 1.0)

// 切到「隐私与控制」标签（OCR 定位标签文字 → 坐标点击）
if let privacyTab = locateText("隐私与控制", in: rect) {
    clickAt(privacyTab)
    Thread.sleep(forTimeInterval: 1.0)
}

if let pausePoint = locateText("暂停采集", in: rect) {
    clickAt(pausePoint)
    result.check("点击「暂停采集」后横幅显示已暂停",
                 waitUntil(4) { bannerTexts().contains { $0.contains("已暂停") } })
    if let resumePoint = locateText("恢复采集", in: rect) {
        clickAt(resumePoint)
        result.check("点击「恢复采集」后恢复采集中",
                     waitUntil(4) { bannerTexts().contains { $0.contains("正在本机采集") } })
    } else {
        result.check("定位到「恢复采集」", false)
    }
} else {
    result.check("OCR 定位到「暂停采集」", false)
}

print("结果：\(result.pass) 通过 / \(result.fail) 失败")
if let pid = launchedPid { kill(pid, SIGTERM) }
exit(result.fail == 0 ? 0 : 1)
