using Avalonia.Platform.Storage;

namespace AiAssistant.Desktop;

/// <summary>One save-file type offered in the export dialog.</summary>
public sealed record SaveFileType(string Label, string Extension);

/// <summary>The chosen path plus the file type the user actually selected in the dialog.</summary>
public sealed record SaveFileSelection(string Path, string Extension);

/// <summary>Abstraction over the platform save-file dialog so ViewModels can prompt without a visual reference.</summary>
public interface ISaveFilePicker
{
    /// <summary>Returns the chosen path and type, or <c>null</c> when the user cancels.</summary>
    Task<SaveFileSelection?> PickSaveFileAsync(string suggestedFileName, IReadOnlyList<SaveFileType> types);
}

/// <summary>Wraps Avalonia's storage provider. Created in the shell once the window (and thus <c>TopLevel</c>) exists.</summary>
public sealed class AvaloniaSaveFilePicker(IStorageProvider storageProvider) : ISaveFilePicker
{
    public async Task<SaveFileSelection?> PickSaveFileAsync(
        string suggestedFileName,
        IReadOnlyList<SaveFileType> types)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentNullException.ThrowIfNull(types);
        if (types.Count == 0)
        {
            throw new ArgumentException("At least one save-file type is required.", nameof(types));
        }

        var choices = types
            .Select(type => new FilePickerFileType(type.Label) { Patterns = [$"*.{type.Extension}"] })
            .ToList();
        var result = await storageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            SuggestedFileType = choices[0],
            DefaultExtension = types[0].Extension.TrimStart('.'),
            FileTypeChoices = choices,
            ShowOverwritePrompt = true
        });
        var file = result.File;
        if (file is null)
        {
            return null;
        }

        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var selected = result.SelectedFileType is null
            ? null
            : types.FirstOrDefault(type =>
                string.Equals(type.Label, result.SelectedFileType.Name, StringComparison.Ordinal));
        var extension = selected?.Extension;
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(path);
        }

        extension = string.IsNullOrWhiteSpace(extension)
            ? types[0].Extension.TrimStart('.')
            : extension.TrimStart('.').ToLowerInvariant();
        if (!path.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
        {
            // Some platforms keep the suggested .md name when the user switches filters;
            // the selected type is authoritative so the file content and extension agree.
            path = Path.ChangeExtension(path, extension);
        }

        return new SaveFileSelection(path, extension);
    }
}
