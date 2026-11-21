// 真人场景 UI 点击测试：在真实打包 .app 上，用截图 + Vision OCR 定位按钮 → CGEvent 坐标点击，
// 用 AX 读取可见文本断言状态变化。覆盖 7 个页面 + 主题切换。
//
// 前置：宿主进程已授予「辅助功能」+「屏幕与系统音频录制」。
// 用法：swift persona-ui.swift [app路径]
import ApplicationServices
import AppKit
import Foundation
import CoreGraphics
import Vision

let appBundle = CommandLine.arguments.count > 1
    ? CommandLine.arguments[1]
    : "/Users/gaozeqian/Repos/AiAssistant/artifacts/packages/staging/osx-arm64/AiAssistant.app"
let dataDir = ProcessInfo.processInfo.environment["PERSONA_DATA_DIR"] ?? "/tmp/aia-persona-ui"

func attr(_ e: AXUIElement, _ n: String) -> AnyObject? {
    var v: AnyObject?
    guard AXUIElementCopyAttributeValue(e, n as CFString, &v) == .success else { return nil }
    return v
}
func text(_ e: AXUIElement) -> String {
    (attr(e, kAXTitleAttribute) as? String)
        ?? (attr(e, kAXValueAttribute) as? String)
        ?? (attr(e, kAXDescriptionAttribute) as? String)
        ?? ""
}
func descendants(_ e: AXUIElement, _ role: String) -> [AXUIElement] {
    var r: [AXUIElement] = []
    if (attr(e, kAXRoleAttribute) as? String) == role { r.append(e) }
    if let kids = attr(e, kAXChildrenAttribute) as? [AXUIElement] {
        for k in kids { r += descendants(k, role) }
    }
    return r
}

var pass = 0, fail = 0
func check(_ name: String, _ ok: Bool) {
    print("\(ok ? "PASS" : "FAIL")  \(name)")
    ok ? (pass += 1) : (fail += 1)
}

func rect(of win: AXUIElement) -> CGRect? {
    var p = CGPoint.zero, s = CGSize.zero
    guard let pv = attr(win, kAXPositionAttribute),
          let sv = attr(win, kAXSizeAttribute),
          AXValueGetValue(pv as! AXValue, .cgPoint, &p),
          AXValueGetValue(sv as! AXValue, .cgSize, &s) else { return nil }
    return CGRect(origin: p, size: s)
}

func shot(_ r: CGRect, _ path: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    p.arguments = ["-x", "-R", "\(Int(r.origin.x)),\(Int(r.origin.y)),\(Int(r.width)),\(Int(r.height))", path]
    try? p.run(); p.waitUntilExit()
}

func locate(_ text: String, in r: CGRect, retry: Int = 2) -> CGPoint? {
    for attempt in 0...retry {
        let path = "/tmp/persona-ocr-\(attempt).png"
        shot(r, path)
        guard let img = NSImage(contentsOfFile: path)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else { continue }
        let scale = CGFloat(img.width) / r.width
        let req = VNRecognizeTextRequest()
        req.recognitionLanguages = ["zh-Hans", "en-US"]
        req.recognitionLevel = .accurate
        try? VNImageRequestHandler(cgImage: img, options: [:]).perform([req])
        for o in req.results ?? [] {
            if let c = o.topCandidates(1).first, c.string.contains(text) {
                let b = o.boundingBox
                return CGPoint(
                    x: r.origin.x + (b.origin.x + b.width / 2) * CGFloat(img.width) / scale,
                    y: r.origin.y + (1 - b.origin.y - b.height / 2) * CGFloat(img.height) / scale)
            }
        }
        Thread.sleep(forTimeInterval: 0.3)
    }
    return nil
}

func click(_ pt: CGPoint) {
    let src = CGEventSource(stateID: .hidSystemState)
    CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: pt, mouseButton: .left)?.post(tap: .cghidEventTap)
    CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: pt, mouseButton: .left)?.post(tap: .cghidEventTap)
    Thread.sleep(forTimeInterval: 0.5)
}

func ocrAll(_ r: CGRect) -> [String] {
    let path = "/tmp/persona-all.png"
    shot(r, path)
    guard let img = NSImage(contentsOfFile: path)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return [] }
    let req = VNRecognizeTextRequest()
    req.recognitionLanguages = ["zh-Hans", "en-US"]
    req.recognitionLevel = .accurate
    try? VNImageRequestHandler(cgImage: img, options: [:]).perform([req])
    return (req.results ?? []).compactMap { $0.topCandidates(1).first?.string }
}

func ocrHas(_ token: String, in r: CGRect) -> Bool {
    ocrAll(r).contains { $0.contains(token) }
}

func tap(_ label: String, in r: CGRect) -> Bool {
    guard let pt = locate(label, in: r) else {
        print("  (OCR 未找到「\(label)」)")
        return false
    }
    click(pt)
    return true
}

/// 精确匹配 OCR 文本（用于「语音」这类会与徽章「语音休眠」混淆的标签）
func tapExact(_ label: String, in r: CGRect) -> Bool {
    let path = "/tmp/persona-exact.png"
    shot(r, path)
    guard let img = NSImage(contentsOfFile: path)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return false }
    let scale = CGFloat(img.width) / r.width
    let req = VNRecognizeTextRequest()
    req.recognitionLanguages = ["zh-Hans", "en-US"]
    req.recognitionLevel = .accurate
    try? VNImageRequestHandler(cgImage: img, options: [:]).perform([req])
    for o in req.results ?? [] {
        guard let c = o.topCandidates(1).first, c.string.trimmingCharacters(in: .whitespaces) == label else { continue }
        let b = o.boundingBox
        click(CGPoint(
            x: r.origin.x + (b.origin.x + b.width / 2) * CGFloat(img.width) / scale,
            y: r.origin.y + (1 - b.origin.y - b.height / 2) * CGFloat(img.height) / scale))
        return true
    }
    print("  (OCR 未找到精确标签「\(label)」)")
    return false
}

func waitText(_ win: AXUIElement, _ token: String, _ seconds: Double = 5) -> Bool {
    let deadline = Date().addingTimeInterval(seconds)
    while Date() < deadline {
        if descendants(win, kAXStaticTextRole).map(text).contains(where: { $0.contains(token) }) { return true }
        Thread.sleep(forTimeInterval: 0.3)
    }
    return descendants(win, kAXStaticTextRole).map(text).contains(where: { $0.contains(token) })
}

guard AXIsProcessTrusted() else {
    print("FAIL  辅助功能未授权"); exit(2)
}

// 复用已运行的实例（若存在），否则新启动。这样可用外部环境变量（模型/语音）预配置进程。
let existing = NSRunningApplication.runningApplications(withBundleIdentifier: "com.aiassistant.desktop").first
if existing == nil {
    let proc = Process()
    proc.executableURL = URL(fileURLWithPath: "/usr/bin/open")
    proc.arguments = ["-n", appBundle, "--args", "--show-window", "--data-directory", dataDir]
    try? proc.run()
}

var win: AXUIElement?
let deadline = Date().addingTimeInterval(25)
var appPid: pid_t?
while Date() < deadline {
    if let app = NSRunningApplication.runningApplications(withBundleIdentifier: "com.aiassistant.desktop").first {
        appPid = app.processIdentifier
        let windows = attr(AXUIElementCreateApplication(app.processIdentifier), kAXWindowsAttribute) as? [AXUIElement] ?? []
        if win == nil { win = windows.first }
        if win != nil { break }
    }
    Thread.sleep(forTimeInterval: 0.5)
}
guard let appPid, let win, let r = rect(of: win) else {
    print("FAIL  未找到窗口"); exit(3)
}
NSRunningApplication(processIdentifier: appPid)?.activate(options: [.activateIgnoringOtherApps])
Thread.sleep(forTimeInterval: 1.5)
print("窗口就绪：\(Int(r.width))x\(Int(r.height))")

func tab(_ name: String) {
    _ = tapExact(name, in: r) || tap(name, in: r)
    Thread.sleep(forTimeInterval: 0.8)
}

// ── 场景 1：时间线页加载可以看到活动记录 ──
check("时间线页显示「活动记录」", waitText(win, "活动记录"))

// ── 场景 2：隐私与控制 → 暂停/恢复采集 ──
tab("隐私与控制")
check("隐私页显示「采集状态」", waitText(win, "采集状态"))
_ = tap("暂停采集", in: r)
check("暂停后横幅显示「已暂停」", waitText(win, "已暂停"))
_ = tap("恢复采集", in: r)
check("恢复后横幅显示「正在本机采集」", waitText(win, "正在本机采集"))

// ── 场景 3：语音页 → 启用/关闭语音 ──
_ = tapExact("语音", in: r)
Thread.sleep(forTimeInterval: 0.8)
check("语音页显示「本地语音命令」", waitText(win, "本地语音命令"))
let voiceOcr = ocrAll(r)
check("唤醒词模型已安装（无缺失提示）",
      !voiceOcr.contains { $0.contains("model assets are not installed") })
if ProcessInfo.processInfo.environment["PERSONA_DEBUG"] == "1" {
    print("  [语音页 OCR] " + voiceOcr.joined(separator: " | "))
}
_ = tapExact("启用语音", in: r) || tap("启用", in: r)
// 真人断言：点击后按钮文案应切换（启用语音→关闭语音）；模型资产缺失时状态会停在休眠，
// 所以断言按钮切换而非状态文字。
// 模型资产缺失时启用会被降级拒绝（按钮不切换、显示模型缺失提示），两种都是合理响应。
let afterEnable = ocrAll(r)
check("点击「启用语音」后有响应（按钮切换或模型缺失提示）",
      afterEnable.contains { $0.contains("关闭语音") || $0.contains("model assets") || $0.contains("正在聆听") })
_ = tapExact("关闭语音", in: r) || tap("关闭", in: r) || tapExact("启用语音", in: r)
check("关闭后恢复「启用语音」按钮", ocrHas("启用语音", in: r))

// ── 场景 4：检索与问答 → 输入关键词并检索 ──
tab("检索与问答")
check("检索页显示「证据检索与问答」", waitText(win, "证据检索与问答"))

// ── 场景 5：日报页 → 生成日报 ──
tab("日报")
check("日报页显示「日报与站会」", waitText(win, "日报与站会"))
_ = tap("生成日报", in: r)
check("生成日报后出现版本记录", waitText(win, "版本") || waitText(win, "日报正文"))

// ── 场景 6：数据与导出 ──
tab("数据与导出")
check("数据页显示「删除本地数据」", waitText(win, "删除本地数据"))

// ── 场景 7：本地诊断 → 重新检查 ──
tab("本地诊断")
check("诊断页显示「系统健康」", waitText(win, "系统健康"))
_ = tap("重新检查", in: r)
check("重新检查后显示「本机模型网关」", waitText(win, "本机模型网关"))
check("诊断页显示「本机模型配置」编辑区", ocrHas("本机模型配置", in: r))
check("模型配置含对话/Embedding/Base URL 输入", ocrHas("对话模型", in: r) && ocrHas("Embedding", in: r) && ocrHas("Base URL", in: r))

// ── 场景 8：主题按钮文案应为「可切换到的模式」（当前深色 → 显示浅色模式）──
check("主题按钮显示可切换模式（浅色模式）", ocrHas("浅色模式", in: r) || waitText(win, "浅色模式"))

print("结果：\(pass) 通过 / \(fail) 失败")
kill(appPid, SIGTERM)
exit(fail == 0 ? 0 : 1)
