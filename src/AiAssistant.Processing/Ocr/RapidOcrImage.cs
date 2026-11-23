using AiAssistant.Application.Ports;

namespace AiAssistant.Processing.Ocr;

/// <summary>BGRA32 frame helper shared by the ONNX pipeline; decoding stays in the platform adapter.</summary>
internal sealed record RapidOcrImage(byte[] Pixels, int Width, int Height)
{
    public static bool TryCreate(ImageFrameBuffer frame, out RapidOcrImage? image)
    {
        image = null;
        if (frame.Width <= 0 ||
            frame.Height <= 0 ||
            frame.Bgra32Pixels.Length < frame.Width * frame.Height * 4)
        {
            return false;
        }

        image = new RapidOcrImage(frame.Bgra32Pixels, frame.Width, frame.Height);
        return true;
    }

    /// <summary>Resizes to a multiple of 32 while keeping the aspect ratio, then normalizes to RGB planar float.</summary>
    public (float[] Tensor, int Width, int Height, float ScaleX, float ScaleY) ToDetectionTensor(int maxSideLength)
    {
        var scale = Math.Min(1.0, (double)maxSideLength / Math.Max(Width, Height));
        var targetWidth = Math.Max(32, (int)Math.Round(Width * scale / 32.0) * 32);
        var targetHeight = Math.Max(32, (int)Math.Round(Height * scale / 32.0) * 32);
        var tensor = new float[3 * targetWidth * targetHeight];
        var planeSize = targetWidth * targetHeight;

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(Height - 1, (int)(y / (double)targetHeight * Height));
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(Width - 1, (int)(x / (double)targetWidth * Width));
                var pixel = ((sourceY * Width) + sourceX) * 4;
                var index = (y * targetWidth) + x;
                tensor[index] = Normalize(Pixels[pixel + 2], 0.485f, 0.229f);
                tensor[planeSize + index] = Normalize(Pixels[pixel + 1], 0.456f, 0.224f);
                tensor[(2 * planeSize) + index] = Normalize(Pixels[pixel], 0.406f, 0.225f);
            }
        }

        return (tensor, targetWidth, targetHeight, targetWidth / (float)Width, targetHeight / (float)Height);
    }

    /// <summary>Crops a padded box and resizes it to the recognition height; returns planar RGB in [-1, 1].</summary>
    public (float[] Tensor, int Width) ToRecognitionTensor(RapidOcrBox box, int recognitionHeight, int maxWidth)
    {
        var x = Math.Clamp(box.X, 0, Width - 1);
        var y = Math.Clamp(box.Y, 0, Height - 1);
        var cropWidth = Math.Clamp(box.Width, 1, Width - x);
        var cropHeight = Math.Clamp(box.Height, 1, Height - y);
        var targetWidth = Math.Clamp(
            (int)Math.Round(cropWidth * (double)recognitionHeight / cropHeight),
            16,
            maxWidth);
        var tensor = new float[3 * targetWidth * recognitionHeight];
        var planeSize = targetWidth * recognitionHeight;

        for (var ty = 0; ty < recognitionHeight; ty++)
        {
            var sourceY = y + Math.Min(cropHeight - 1, (int)(ty / (double)recognitionHeight * cropHeight));
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var sourceX = x + Math.Min(cropWidth - 1, (int)(tx / (double)targetWidth * cropWidth));
                var pixel = ((sourceY * Width) + sourceX) * 4;
                var index = (ty * targetWidth) + tx;
                tensor[index] = Normalize(Pixels[pixel + 2], 0.5f, 0.5f);
                tensor[planeSize + index] = Normalize(Pixels[pixel + 1], 0.5f, 0.5f);
                tensor[(2 * planeSize) + index] = Normalize(Pixels[pixel], 0.5f, 0.5f);
            }
        }

        return (tensor, targetWidth);
    }

    private static float Normalize(byte value, float mean, float std) => ((value / 255f) - mean) / std;
}
