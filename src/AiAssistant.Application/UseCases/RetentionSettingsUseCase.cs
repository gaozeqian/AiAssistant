using AiAssistant.Application.Ports;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// Reads and updates the retention policy. Validation stays in the application layer so the desktop
/// UI, the diagnostics page and future callers share the same bounds.
/// </summary>
public sealed class RetentionSettingsUseCase(
    IRetentionSettingsStore store,
    IRetentionPolicyApplier? applier = null)
{
    public RetentionSettings Get() => store.Load();

    /// <summary>Applies the persisted policy to the live options; call once during startup.</summary>
    public RetentionSettings ApplyCurrent()
    {
        var settings = store.Load();
        applier?.Apply(settings);
        return settings;
    }

    public async Task<RetentionSettings> SaveAsync(
        int mediaRetentionDays,
        int commandAudioRetentionDays,
        int maxStorageGigabytes,
        CancellationToken ct = default)
    {
        var settings = RetentionSettings.Normalize(
            mediaRetentionDays,
            commandAudioRetentionDays,
            maxStorageGigabytes);
        await store.SaveAsync(settings, ct);
        applier?.Apply(settings);
        return settings;
    }
}
