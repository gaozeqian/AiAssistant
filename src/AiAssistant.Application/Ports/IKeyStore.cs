using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IKeyStore
{
    CapabilityState Capability { get; }
    Task<byte[]> GetOrCreateMasterKeyAsync(string keyId, CancellationToken ct);
    Task RotateWrapperKeyAsync(string keyId, CancellationToken ct);

    /// <summary>
    /// Returns a key store bound to a different data directory. Used by backup restore to verify the
    /// bundled key envelope against the backup's own keystore before touching the live data directory.
    /// </summary>
    IKeyStore CreateForDirectory(string dataDirectory);
}
