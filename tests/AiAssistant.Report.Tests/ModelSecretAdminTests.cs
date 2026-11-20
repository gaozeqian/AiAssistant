using System.Security.Cryptography;
using System.Text;
using AiAssistant.Models;
using AiAssistant.Storage.KeyStores;

namespace AiAssistant.Report.Tests;

/// <summary>Encrypted model secret administration used by the CLI and diagnostics panel.</summary>
public sealed class ModelSecretAdminTests : IDisposable
{
    private readonly string _tempRoot;

    public ModelSecretAdminTests()
    {
        _tempRoot = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup locks
        }
    }

    [Fact]
    public async Task Admin_StoresListsDeletes_WithoutExposingPlaintext()
    {
        using var store = new ProtectedModelSecretStore(_tempRoot, new StaticKeyStore(RandomNumberGenerator.GetBytes(32)));
        var admin = new ModelSecretAdmin(store);

        await admin.SetAsync("corp-api-key", "super-secret-value", CancellationToken.None);
        await admin.SetAsync("second-key", "another-secret", CancellationToken.None);

        var names = await admin.ListNamesAsync(CancellationToken.None);
        Assert.Equal(new[] { "corp-api-key", "second-key" }, names);

        var resolver = new ModelSecretResolver(store);
        Assert.Equal("super-secret-value", await resolver.ResolveAsync("corp-api-key", CancellationToken.None));

        var encryptedPath = Path.Combine(_tempRoot, "model-secrets.enc");
        var raw = await File.ReadAllBytesAsync(encryptedPath);
        Assert.DoesNotContain("super-secret-value", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

        Assert.True(await admin.DeleteAsync("corp-api-key", CancellationToken.None));
        Assert.False(await admin.DeleteAsync("corp-api-key", CancellationToken.None));
        Assert.Single(await admin.ListNamesAsync(CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("corp-api-key", CancellationToken.None));
    }
}
