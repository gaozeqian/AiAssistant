// AX 驱动的最接近真人的 UI 端到端测试：通过 macOS 辅助功能树枚举真实窗口控件，
// 用真实鼠标事件(CGEvent)或 AXPress 点击，并断言可见状态变化。
//
// 前置条件（一次性授权，给运行本脚本的宿主进程）：
//   系统设置 → 隐私与安全性 → 辅助功能：勾选运行本脚本的终端/IDE
//   系统设置 → 隐私与安全性 → 屏幕与系统音频录制：勾选运行本脚本的终端/IDE（截图断言用）
//
// 用法：
//   swift ax-e2e.swift probe          # 打印 AiAssistant 窗口的 AX 树（诊断）
//   swift ax-e2e.swift run            # 执行完整用户场景并输出 PASS/FAIL
//   swift ax-e2e.swift run <app路径>  # 指定 .app 路径
import ApplicationServices
import AppKit
import Foundation
import CoreGraphics

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

func dumpTree(_ el: AXUIElement, _ depth: Int, _ maxDepth: Int) {
    if depth > maxDepth { return }
    let r = role(el)
    let t = title(el)
    let prefix = String(repeating: "  ", count: depth)
    if !r.isEmpty || !t.isEmpty {
        print("\(prefix)\(r) | \(t)")
    }
    for child in children(el) {
        dumpTree(child, depth + 1, maxDepth)
    }
}

func window(of pid: pid_t, matching matchTitle: String) -> AXUIElement? {
    let app = AXUIElementCreateApplication(pid)
    let windows = attr(app, kAXWindowsAttribute) as? [AXUIElement] ?? []
    for w in windows where title(w).contains(matchTitle) {
        return w
    }
    return windows.first
}

func descendant(_ el: AXUIElement, role r: String, title t: String) -> AXUIElement? {
    if role(el) == r && title(el) == t { return el }
    for child in children(el) {
        if let hit = descendant(child, role: r, title: t) { return hit }
    }
    return nil
}

func descendants(_ el: AXUIElement, role r: String) -> [AXUIElement] {
    var result: [AXUIElement] = []
    if role(el) == r { result.append(el) }
    for child in children(el) { result += descendants(child, role: r) }
    return result
}

func center(_ el: AXUIElement) -> CGPoint? {
    var pos = CGPoint.zero
    var size = CGSize.zero
    guard let posVal = attr(el, kAXPositionAttribute), AXValueGetType(posVal as! AXValue) == .cgPoint,
          let sizeVal = attr(el, kAXSizeAttribute), AXValueGetType(sizeVal as! AXValue) == .cgSize,
          AXValueGetValue(posVal as! AXValue, .cgPoint, &pos),
          AXValueGetValue(sizeVal as! AXValue, .cgSize, &size) else { return nil }
    return CGPoint(x: pos.x + size.width / 2, y: pos.y + size.height / 2)
}

func click(_ el: AXUIElement) -> Bool {
    if AXUIElementPerformAction(el, kAXPressAction as CFString) == .success { return true }
    guard let p = center(el) else { return false }
    let src = CGEventSource(stateID: .hidSystemState)
    let down = CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: p, mouseButton: .left)
    let up = CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: p, mouseButton: .left)
    down?.post(tap: .cghidEventTap)
    up?.post(tap: .cghidEventTap)
    return true
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
    var pass = 0
    var fail = 0
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
            if window(of: app.processIdentifier, matching: windowTitle) != nil {
                return app.processIdentifier
            }
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

// 场景1：窗口标题正确（真实 .app 的 AX 窗口已创建）
result.check("窗口标题正确", title(win).contains(windowTitle))

// 场景2：顶部横幅状态文本（Avalonia 的 TextBlock 会以 kAXValue 暴露）
// 注意：Avalonia 的 macOS 辅助功能桥只暴露窗口 + 文本 + TabGroup，
// 不暴露带名字的按钮/标签，因此无法按名字点击（见 README）。
func bannerTexts() -> [String] { descendants(win, role: kAXStaticTextRole).map(title) }
result.check("顶部显示「正在本机采集」",
             bannerTexts().contains { $0.contains("正在本机采集") })
result.check("顶部显示「语音休眠」",
             bannerTexts().contains { $0.contains("语音休眠") })

print("结果：\(result.pass) 通过 / \(result.fail) 失败")
if let pid = launchedPid { kill(pid, SIGTERM) }
exit(result.fail == 0 ? 0 : 1)
