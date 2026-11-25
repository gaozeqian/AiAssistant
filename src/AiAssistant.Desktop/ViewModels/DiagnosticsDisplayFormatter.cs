using System.Collections.ObjectModel;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>Stateless formatting/mapping for the diagnostics tab.</summary>
internal static class DiagnosticsDisplayFormatter
{
    public static string FormatEndpoint(ModelEndpointCapability endpoint)
    {
        // Show only the capabilities the role actually serves; a chat endpoint does not advertise
        // embeddings and an embedding endpoint does not advertise chat/streaming/structured/tools.
        var capabilities = endpoint.Role switch
        {
            ModelEndpointRole.Chat => string.Join(
                " · ",
                $"对话={(endpoint.ChatAvailable ? "可用" : "不可用")}",
                $"流式={(endpoint.StreamingAvailable ? "可用" : "不可用")}",
                $"结构化={(endpoint.StructuredOutputAvailable ? "可用" : "不可用")}",
                $"工具={(endpoint.ToolCallingAvailable ? "可用" : "不可用")}"),
            ModelEndpointRole.Embedding => $"向量={(endpoint.EmbeddingsAvailable ? "可用" : "不可用")}",
            _ => $"对话={(endpoint.ChatAvailable ? "可用" : "不可用")}"
        };

        return string.IsNullOrWhiteSpace(endpoint.Diagnostic)
            ? $"{RoleLabel(endpoint.Role)} · {TierLabel(endpoint.Tier)}：{capabilities}"
            : $"{RoleLabel(endpoint.Role)} · {TierLabel(endpoint.Tier)}：{capabilities} · {endpoint.Diagnostic}";
    }

    public static string FormatMcpStatus(LocalMcpSnapshot snapshot) => snapshot switch
    {
        { Enabled: false } => "默认关闭（仅监听 127.0.0.1）",
        { Running: false } => "已启用但监听未启动",
        _ => $"运行中 · {snapshot.Endpoint} · 请求 {snapshot.RequestCount} · 未授权拒绝 {snapshot.UnauthorizedCount} · 限流 {snapshot.RateLimitedCount} · 工具调用 {snapshot.ToolCallCount}"
    };

    public static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string RoleLabel(ModelEndpointRole role) => role switch
    {
        ModelEndpointRole.Chat => "对话端点",
        ModelEndpointRole.Embedding => "向量端点",
        _ => role.ToString()
    };

    private static string TierLabel(ModelEndpointTier tier) => tier switch
    {
        ModelEndpointTier.LocalOffline => "本机",
        ModelEndpointTier.CorporateIntranet => "企业内网",
        ModelEndpointTier.PublicCloud => "公网",
        _ => tier.ToString()
    };
}
