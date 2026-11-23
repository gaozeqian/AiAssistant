using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Decoded BGRA32 frame handed to the OCR pipeline; decoding stays in the platform adapter.</summary>
public sealed record ImageFrameBuffer(byte[] Bgra32Pixels, int Width, int Height);
