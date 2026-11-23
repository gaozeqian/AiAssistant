using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record VectorModelDescriptor(string ModelId, string ModelVersion, int Dimensions);
