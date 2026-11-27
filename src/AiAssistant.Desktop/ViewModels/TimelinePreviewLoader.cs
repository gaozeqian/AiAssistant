using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.Exceptions;
using Avalonia.Media.Imaging;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>
/// Decrypts and decodes one media preview. Owns all integrity/decoding error mapping so
/// <see cref="TimelineViewModel"/> only coordinates selection and cancellation.
/// </summary>
internal sealed class TimelinePreviewLoader(TimelineUseCase timeline)
{
    public async Task<TimelinePreviewResult> LoadAsync(ActivityEvent activityEvent, CancellationToken ct)
    {
        if (activityEvent.MediaRef is null)
        {
            return new TimelinePreviewResult(null, string.Empty);
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = await timeline.LoadDecryptedMediaAsync(activityEvent.MediaRef.ContentId, ct);
            ct.ThrowIfCancellationRequested();
            if (plaintext is null)
            {
                return new TimelinePreviewResult(null, "媒体已按保留策略清理，事件元数据仍可查看。");
            }

            using var stream = new MemoryStream(plaintext, writable: false);
            // Decode at the stored width. The previous 960px clamp threw away a quarter of a 1280px
            // frame before the panel ever rendered it; the panel is ~890 DIP wide (over 1100 physical
            // pixels at 125%+ scaling), so it was upscaling an already-downscaled bitmap.
            return new TimelinePreviewResult(new Bitmap(stream), string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StorageIntegrityException)
        {
            return new TimelinePreviewResult(null, "媒体完整性校验失败或已损坏，未展示任何内容。");
        }
        catch (StorageAuthenticationException)
        {
            return new TimelinePreviewResult(null, "媒体密钥不可用，请检查系统密钥库权限。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new TimelinePreviewResult(null, "媒体无法解码或读取。原始事件未受影响。");
        }
        finally
        {
            if (plaintext is not null)
            {
                Array.Clear(plaintext);
            }
        }
    }
}

internal sealed record TimelinePreviewResult(Bitmap? Image, string Error);
