namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>A single BGRA32 screencast frame delivered by the PipeWire capture stream.</summary>
public sealed record PipeWireFrame(int Width, int Height, byte[] Bgra32, DateTimeOffset CapturedAtUtc);
