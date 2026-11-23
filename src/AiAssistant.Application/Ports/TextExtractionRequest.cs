using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record TextExtractionRequest(
    nint WindowHandle,
    string AppName,
    string WindowTitle,
    byte[]? ScreenshotPngBytes = null);
