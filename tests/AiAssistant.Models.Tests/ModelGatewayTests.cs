using System.Net;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Models;
using AiAssistant.Processing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Models.Tests;

[Trait("Category", "Unit")]
public sealed class ModelGatewayTests
{
    [Fact]
    public async Task RedactionAndAuditDecorators_SanitizePromptAndEmbeddings_WithoutLoggingBodies()
    {
        var inner = new RecordingModelGateway();
        var audit = new InMemoryModelAuditSink();
        var gateway = new AuditedModelGateway(
            new RedactingModelGateway(inner, new SensitiveDataRedactor()),
            audit);

        var result = await gateway.CompleteAsync(
            new ModelCompletionRequest("system", "password=SuperSecret! 联系 test@example.com，今天完成架构评审"),
            CancellationToken.None);

        Assert.DoesNotContain("SuperSecret", inner.LastRequest!.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("test@example.com", inner.LastRequest.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("今天完成架构评审", inner.LastRequest.UserPrompt, StringComparison.Ordinal);
        Assert.True(result.RedactionApplied);

        _ = await gateway.EmbedAsync(
            new ModelEmbeddingRequest(["api_key=SecretToken123 普通文本"]),
            CancellationToken.None);
        Assert.DoesNotContain("SecretToken123", inner.LastEmbedding!.Inputs[0], StringComparison.Ordinal);
        Assert.Equal(2, audit.GetRecent().Count);
        Assert.All(audit.GetRecent(), entry => Assert.True(entry.Succeeded));
    }

    [Fact]
    public async Task OllamaHttpBackend_ParsesChatEmbeddingsAndProbe()
    {
        var handler = new OllamaHandler();
        var options = new ModelGatewayOptions
        {
            ChatModel = "gemma4:12b",
            EmbeddingModel = "embeddinggemma-2:740m"
        };
        using var http = new HttpClient(handler);
        var backend = new HttpModelBackend(http, options);

        var probe = await backend.ProbeAsync(CancellationToken.None);
        Assert.True(probe.ChatAvailable);
        Assert.True(probe.EmbeddingsAvailable);

        var response = await backend.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "生成日报")],
            new ChatOptions { ModelId = "gemma4:12b" },
            CancellationToken.None);
        Assert.Equal("测试回复", response.Messages[0].Text);

        var embeddings = await backend.GenerateAsync(["活动一"], null, CancellationToken.None);
        Assert.Single(embeddings);
        Assert.Equal([0.1f, 0.2f, 0.3f], embeddings[0].Vector.ToArray());
        Assert.Contains("\"stream\":false", handler.LastChatBody, StringComparison.Ordinal);
        // Thinking is off by default so reasoning cannot exhaust the output budget.
        Assert.Contains("\"think\":false", handler.LastChatBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaHttpBackend_SendsThinkingLevelWhenEnabled()
    {
        var handler = new OllamaHandler();
        var options = new ModelGatewayOptions
        {
            ChatModel = "gemma4:12b",
            EmbeddingModel = "embeddinggemma-2:740m",
            Think = true,
            ThinkingLevel = "high"
        };
        using var http = new HttpClient(handler);
        var backend = new HttpModelBackend(http, options);

        _ = await backend.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "生成日报")],
            null,
            CancellationToken.None);

        Assert.Contains("\"think\":\"high\"", handler.LastChatBody, StringComparison.Ordinal);
    }

    private sealed class RecordingModelGateway : IModelGateway
    {
        public ModelCompletionRequest? LastRequest { get; private set; }
        public ModelEmbeddingRequest? LastEmbedding { get; private set; }
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AccessibilityText, "fake");
        public string ChatModelId => "fake-chat";
        public string EmbeddingModelId => "fake-embedding";

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new ModelCompletionResult("完成", "fake-chat", ModelEndpointTier.LocalOffline, false, TimeSpan.Zero));
        }

        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
        {
            LastEmbedding = request;
            return Task.FromResult(new ModelEmbeddingResult(
                request.Inputs.Select(_ => new ReadOnlyMemory<float>([0.1f])).ToArray(),
                "fake-embedding",
                1));
        }

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, "fake-chat", "fake-embedding", ModelEndpointTier.LocalOffline, "ok"));
    }

    private sealed class OllamaHandler : HttpMessageHandler
    {
        public string LastChatBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tags")
            {
                return Json("""{"models":[{"name":"gemma4:12b"},{"name":"embeddinggemma-2:740m"}]}""");
            }

            if (path == "/api/chat")
            {
                LastChatBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("""{"message":{"role":"assistant","content":"测试回复"},"done":true}""");
            }

            if (path == "/api/embed")
            {
                return Json("""{"embeddings":[[0.1,0.2,0.3]]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

