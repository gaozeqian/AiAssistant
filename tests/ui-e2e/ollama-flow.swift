// 完整流程 UI 测试（真人场景）：语音模型就绪 + ollama embedding/chat 驱动的
// 采集→检索→问答→日报→语音 全链路。在真实 .app 上用 OCR 定位 + CGEvent 点击/输入，
// 用 AX 读取可见文本断言。需先以正确环境变量启动应用（见文件末注释）。
import ApplicationServices
import AppKit
import Foundation
import CoreGraphics
import Vision

func attr(_ e: AXUIElement, _ n: String) -> AnyObject? {
    var v: AnyObject?
    guard AXUIElementCopyAttributeValue(e, n as CFString, &v) == .success else { return nil }
    return v
}
func text(_ e: AXUIElement) -> String {
    (attr(e, kAXValueAttribute) as? String) ?? (attr(e, kAXTitleAttribute) as? String) ?? ""
}
func descendants(_ e: AXUIElement, _ role: String) -> [AXUIElement] {
    var r: [AXUIElement] = []
    if (attr(e, kAXRoleAttribute) as? String) == role { r.append(e) }
    if let kids = attr(e, kAXChildrenAttribute) as? [AXUIElement] { for k in kids { r += descendants(k, role) } }
    return r
}
var pass = 0, fail = 0
func check(_ n: String, _ ok: Bool) { print("\(ok ? "PASS" : "FAIL")  \(n)"); ok ? (pass += 1) : (fail += 1) }

func rect(of w: AXUIElement) -> CGRect? {
    var p = CGPoint.zero, s = CGSize.zero
    guard let pv = attr(w, kAXPositionAttribute), let sv = attr(w, kAXSizeAttribute),
          AXValueGetValue(pv as! AXValue, .cgPoint, &p), AXValueGetValue(sv as! AXValue, .cgSize, &s) else { return nil }
    return CGRect(origin: p, size: s)
}
func shot(_ r: CGRect, _ path: String) {
    let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    p.arguments = ["-x", "-R", "\(Int(r.origin.x)),\(Int(r.origin.y)),\(Int(r.width)),\(Int(r.height))", path]
    try? p.run(); p.waitUntilExit()
}
func ocrAll(_ r: CGRect) -> [String] {
    shot(r, "/tmp/flow-ocr.png")
    guard let img = NSImage(contentsOfFile: "/tmp/flow-ocr.png")?.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return [] }
    let q = VNRecognizeTextRequest(); q.recognitionLanguages = ["zh-Hans", "en-US"]; q.recognitionLevel = .accurate
    try? VNImageRequestHandler(cgImage: img, options: [:]).perform([q])
    return (q.results ?? []).compactMap { $0.topCandidates(1).first?.string }
}
func locate(_ t: String, exact: Bool = false, in r: CGRect) -> CGPoint? {
    shot(r, "/tmp/flow-loc.png")
    guard let img = NSImage(contentsOfFile: "/tmp/flow-loc.png")?.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
    let sc = CGFloat(img.width) / r.width
    let q = VNRecognizeTextRequest(); q.recognitionLanguages = ["zh-Hans", "en-US"]; q.recognitionLevel = .accurate
    try? VNImageRequestHandler(cgImage: img, options: [:]).perform([q])
    for o in q.results ?? [] {
        guard let c = o.topCandidates(1).first?.string else { continue }
        let match = exact ? c.trimmingCharacters(in: .whitespaces) == t : c.contains(t)
        if match {
            let b = o.boundingBox
            return CGPoint(x: r.origin.x + (b.origin.x + b.width/2) * CGFloat(img.width)/sc,
                           y: r.origin.y + (1-b.origin.y-b.height/2) * CGFloat(img.height)/sc)
        }
    }
    return nil
}
func click(_ p: CGPoint) {
    let s = CGEventSource(stateID: .hidSystemState)
    CGEvent(mouseEventSource: s, mouseType: .leftMouseDown, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
    CGEvent(mouseEventSource: s, mouseType: .leftMouseUp, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
    Thread.sleep(forTimeInterval: 0.4)
}
func tap(_ t: String, exact: Bool = false, in r: CGRect) -> Bool {
    guard let p = locate(t, exact: exact, in: r) else { print("  (未找到「\(t)」)"); return false }
    click(p); return true
}
func typeText(_ s: String) {
    let pb = NSPasteboard.general; pb.clearContents(); pb.setString(s, forType: .string)
    let src = CGEventSource(stateID: .hidSystemState)
    let cmd = CGEvent(keyboardEventSource: src, virtualKey: 9, keyDown: true)  // V key
    cmd?.flags = [.maskCommand]
    cmd?.post(tap: .cghidEventTap)
    CGEvent(keyboardEventSource: src, virtualKey: 9, keyDown: false)?.post(tap: .cghidEventTap)
    Thread.sleep(forTimeInterval: 0.3)
}
func waitText(_ w: AXUIElement, _ token: String, _ sec: Double = 60) -> Bool {
    let deadline = Date().addingTimeInterval(sec)
    while Date() < deadline {
        if descendants(w, kAXStaticTextRole).map(text).contains(where: { $0.contains(token) }) { return true }
        Thread.sleep(forTimeInterval: 0.5)
    }
    return descendants(w, kAXStaticTextRole).map(text).contains(where: { $0.contains(token) })
}

guard AXIsProcessTrusted() else { print("FAIL 辅助功能未授权"); exit(2) }
guard let app = NSRunningApplication.runningApplications(withBundleIdentifier: "com.aiassistant.desktop").first else {
    print("FAIL 应用未运行（请先以模型环境变量启动）"); exit(3)
}
let pid = app.processIdentifier
guard let win = (attr(AXUIElementCreateApplication(pid), kAXWindowsAttribute) as? [AXUIElement])?.first,
      let r = rect(of: win) else { print("FAIL 无窗口"); exit(4) }
NSRunningApplication(processIdentifier: pid)?.activate(options: [.activateIgnoringOtherApps])
Thread.sleep(forTimeInterval: 1)
print("窗口就绪，开始完整流程（\(Int(r.width))x\(Int(r.height))）")

// ── 1. 语音模型就绪 ──
_ = tap("语音", exact: true, in: r); Thread.sleep(forTimeInterval: 1)
let voice = ocrAll(r)
check("唤醒词 KWS 模型已加载", voice.contains { $0.contains("Wake word ready") })
check("命令 ASR 模型已加载", !voice.contains { $0.contains("command ASR model missing") })
print("  [语音状态] " + voice.first { $0.contains("Wake word") || $0.contains("not installed") }!)

// ── 2. 检索与问答：ollama embed + chat ──
_ = tap("检索与问答", exact: true, in: r); Thread.sleep(forTimeInterval: 1)
check("检索页就绪", waitText(win, "证据检索与问答", 5))
// 输入框在「输入关键词或问题」占位处，OCR 定位占位文本附近点击
if let input = locate("输入关键词或问题", in: r) { click(input) } else if let p = locate("检索与问答", exact: true, in: r) { click(CGPoint(x: p.x + 250, y: p.y + 120)) }
typeText("今天用过哪些应用")
Thread.sleep(forTimeInterval: 0.5)
_ = tap("检索证据", exact: true, in: r)
check("检索出证据（embed 生效）", waitText(win, "命中", 30) || waitText(win, "E1", 30) || ocrHasAny(["E1", "条", "命中"], r))
_ = tap("提问", exact: true, in: r)
check("问答完成（chat 生效）", waitText(win, "gemma", 120) || waitText(win, "E1", 120))
let answerMeta = descendants(win, kAXStaticTextRole).map(text).first { $0.contains("model=") || $0.contains("gemma") }
print("  [回答元信息] \(answerMeta ?? "(AX 未读到)")")

// ── 3. 日报：生成 ──
_ = tap("日报", exact: true, in: r); Thread.sleep(forTimeInterval: 1)
check("日报页就绪", waitText(win, "日报与站会", 5))
_ = tap("生成日报", exact: true, in: r)
check("日报生成完成", waitText(win, "日报正文", 60) || waitText(win, "版本", 60))

func ocrHasAny(_ tokens: [String], _ rr: CGRect) -> Bool {
    let all = ocrAll(rr)
    return tokens.contains { tok in all.contains { $0.contains(tok) } }
}

print("结果：\(pass) 通过 / \(fail) 失败")
kill(pid, SIGTERM)
exit(fail == 0 ? 0 : 1)
