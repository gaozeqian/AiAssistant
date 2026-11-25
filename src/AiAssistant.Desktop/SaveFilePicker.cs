using Avalonia.Platform.Storage;

namespace AiAssistant.Desktop;

/// <summary>One save-file type offered in the export dialog.</summary>
public sealed record SaveFileType(string Label, string Extension);

/// <summary>Abstraction over the platform save-file dialog so ViewModels can prompt without a visual reference.</summary>
public interface ISaveFilePicker
{
    /// <summary>Returns the chosen absolute path, or <c>null</c> when the user cancels.</summary>
    Task<string?> PickSavePathAsync(string suggestedFileName, IReadOnlyList<SaveFileType> types);
}

/// <summary>Wraps Avalonia's storage provider. Created in the shell once the window (and thus <c>TopLevel</c>) exists.</summary>
public sealed class AvaloniaSaveFilePicker(IStorageProvider storageProvider) : ISaveFilePicker
{
    public async Task<string?> PickSavePathAsync(string suggestedFileName, IReadOnlyList<SaveFileType> types)
    {
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = types
                .Select(type => new FilePickerFileType(type.Label) { Patterns = [$"*.{type.Extension}"] })
                .ToList(),
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }
}
