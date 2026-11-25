using System.Collections.ObjectModel;
using AiAssistant.Application.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>
/// Model secret administration: write-only values, list/delete by name, never echoes a stored value.
/// Extracted from DiagnosticsViewModel so secret handling has one auditable owner.
/// </summary>
public sealed class ModelSecretAdminViewModel : ObservableObject
{
    private readonly IModelSecretAdmin? _secrets;
    private string _secretName = string.Empty;
    private string _secretValue = string.Empty;
    private string? _selectedSecret;
    private string _secretStatus = "密钥值写入后不可读取，仅供对应端点引用。";

    public ModelSecretAdminViewModel(IModelSecretAdmin? secrets)
    {
        _secrets = secrets;
        SaveSecretCommand = new AsyncRelayCommand(SaveSecretAsync);
        RefreshSecretsCommand = new AsyncRelayCommand(() => RefreshAsync(updateStatus: true));
        DeleteSecretCommand = new AsyncRelayCommand(DeleteSecretAsync);
    }

    public bool HasSecretAdmin => _secrets is not null;

    public ObservableCollection<string> SecretNames { get; } = [];

    public IAsyncRelayCommand SaveSecretCommand { get; }

    public IAsyncRelayCommand RefreshSecretsCommand { get; }

    public IAsyncRelayCommand DeleteSecretCommand { get; }

    public string SecretName
    {
        get => _secretName;
        set => SetProperty(ref _secretName, value);
    }

    public string SecretValue
    {
        get => _secretValue;
        set => SetProperty(ref _secretValue, value);
    }

    public string? SelectedSecret
    {
        get => _selectedSecret;
        set => SetProperty(ref _selectedSecret, value);
    }

    public string SecretStatus
    {
        get => _secretStatus;
        private set => SetProperty(ref _secretStatus, value);
    }

    public async Task RefreshAsync(bool updateStatus)
    {
        if (_secrets is null)
        {
            return;
        }

        try
        {
            var names = await _secrets.ListNamesAsync(CancellationToken.None);
            SecretNames.Clear();
            foreach (var name in names)
            {
                SecretNames.Add(name);
            }

            if (updateStatus)
            {
                SecretStatus = names.Count == 0
                    ? "尚未保存模型密钥。"
                    : $"已保存 {names.Count} 个密钥（值不可读取）。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            if (updateStatus)
            {
                SecretStatus = "无法读取密钥列表，请检查系统密钥库权限。";
            }
        }
    }

    private async Task SaveSecretAsync()
    {
        if (_secrets is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SecretName) || string.IsNullOrWhiteSpace(SecretValue))
        {
            SecretStatus = "请填写密钥名称和值。";
            return;
        }

        try
        {
            var name = SecretName.Trim();
            await _secrets.SetAsync(name, SecretValue, CancellationToken.None);
            SecretValue = string.Empty;
            await RefreshAsync(updateStatus: false);
            SecretStatus = $"已加密保存 {name}；值不可再读取。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            SecretStatus = "保存失败，请检查系统密钥库权限。";
        }
    }

    private async Task DeleteSecretAsync()
    {
        if (_secrets is null || string.IsNullOrWhiteSpace(SelectedSecret))
        {
            SecretStatus = "请选择要删除的密钥。";
            return;
        }

        try
        {
            var name = SelectedSecret;
            var deleted = await _secrets.DeleteAsync(name, CancellationToken.None);
            await RefreshAsync(updateStatus: false);
            SecretStatus = deleted ? $"已删除 {name}。" : $"{name} 不存在。";
            SelectedSecret = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            SecretStatus = "删除失败，请检查系统密钥库权限。";
        }
    }
}
