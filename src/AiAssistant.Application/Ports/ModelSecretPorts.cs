namespace AiAssistant.Application.Ports;

/// <summary>
/// Local administration of encrypted model endpoint secrets. Values are write-only: only names are listed.
/// </summary>
public interface IModelSecretAdmin
{
    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct);

    Task SetAsync(string name, string value, CancellationToken ct);

    Task<bool> DeleteAsync(string name, CancellationToken ct);
}
