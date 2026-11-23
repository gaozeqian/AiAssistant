namespace AiAssistant.Processing.Ocr;

/// <summary>
/// Simplified DB post-processing: connected components over the probability map with an axis-aligned
/// bounding box and proportional padding instead of polygon unclip. Screen text is predominantly
/// horizontal, so this keeps the dependency-free path accurate enough while staying unit-testable.
/// </summary>
internal static class RapidOcrDetPostProcessor
{
    private const int MinComponentPixels = 8;
    private const int MinBoxSide = 3;

    public static IReadOnlyList<RapidOcrBox> DetectBoxes(
        ReadOnlySpan<float> probabilityMap,
        int mapWidth,
        int mapHeight,
        float pixelThreshold,
        float boxThreshold,
        float scaleX,
        float scaleY,
        int imageWidth,
        int imageHeight,
        float unclipRatio)
    {
        if (mapWidth <= 0 || mapHeight <= 0 || probabilityMap.Length < mapWidth * mapHeight)
        {
            return [];
        }

        var visited = new bool[mapWidth * mapHeight];
        var queue = new int[mapWidth * mapHeight];
        var boxes = new List<RapidOcrBox>();

        for (var start = 0; start < probabilityMap.Length; start++)
        {
            if (visited[start] || probabilityMap[start] < pixelThreshold)
            {
                continue;
            }

            var component = CollectComponent(probabilityMap, mapWidth, mapHeight, start, pixelThreshold, visited, queue);
            if (component.Pixels < MinComponentPixels)
            {
                continue;
            }

            var mapBoxWidth = component.MaxX - component.MinX + 1;
            var mapBoxHeight = component.MaxY - component.MinY + 1;
            if (mapBoxWidth < MinBoxSide || mapBoxHeight < MinBoxSide || component.Score < boxThreshold)
            {
                continue;
            }

            boxes.Add(MapToImage(
                component,
                scaleX,
                scaleY,
                imageWidth,
                imageHeight,
                unclipRatio));
        }

        boxes.Sort(static (left, right) =>
        {
            var rowCompare = left.Y.CompareTo(right.Y);
            return rowCompare != 0 ? rowCompare : left.X.CompareTo(right.X);
        });
        return boxes;
    }

    private static Component CollectComponent(
        ReadOnlySpan<float> probabilityMap,
        int mapWidth,
        int mapHeight,
        int start,
        float pixelThreshold,
        bool[] visited,
        int[] queue)
    {
        var component = new Component { MinX = start % mapWidth, MaxX = start % mapWidth, MinY = start / mapWidth, MaxY = start / mapWidth };
        var head = 0;
        var tail = 0;
        queue[tail++] = start;
        visited[start] = true;

        while (head < tail)
        {
            var index = queue[head++];
            var x = index % mapWidth;
            var y = index / mapWidth;
            component.MinX = Math.Min(component.MinX, x);
            component.MaxX = Math.Max(component.MaxX, x);
            component.MinY = Math.Min(component.MinY, y);
            component.MaxY = Math.Max(component.MaxY, y);
            component.Pixels++;
            component.ProbabilitySum += probabilityMap[index];

            if (x > 0)
            {
                Enqueue(index - 1, probabilityMap, pixelThreshold, visited, queue, ref tail);
            }

            if (x + 1 < mapWidth)
            {
                Enqueue(index + 1, probabilityMap, pixelThreshold, visited, queue, ref tail);
            }

            if (y > 0)
            {
                Enqueue(index - mapWidth, probabilityMap, pixelThreshold, visited, queue, ref tail);
            }

            if (y + 1 < mapHeight)
            {
                Enqueue(index + mapWidth, probabilityMap, pixelThreshold, visited, queue, ref tail);
            }
        }

        return component;
    }

    private static void Enqueue(
        int neighbour,
        ReadOnlySpan<float> probabilityMap,
        float pixelThreshold,
        bool[] visited,
        int[] queue,
        ref int tail)
    {
        if (!visited[neighbour] && probabilityMap[neighbour] >= pixelThreshold)
        {
            visited[neighbour] = true;
            queue[tail++] = neighbour;
        }
    }

    private static RapidOcrBox MapToImage(
        Component component,
        float scaleX,
        float scaleY,
        int imageWidth,
        int imageHeight,
        float unclipRatio)
    {
        var safeScaleX = scaleX <= 0 ? 1 : scaleX;
        var safeScaleY = scaleY <= 0 ? 1 : scaleY;
        var x = (int)Math.Round(component.MinX / safeScaleX);
        var y = (int)Math.Round(component.MinY / safeScaleY);
        var right = (int)Math.Round((component.MaxX + 1) / safeScaleX);
        var bottom = (int)Math.Round((component.MaxY + 1) / safeScaleY);
        var width = Math.Max(1, right - x);
        var height = Math.Max(1, bottom - y);

        var padding = (int)Math.Round(Math.Max(1, (unclipRatio - 1f) / 2f * Math.Min(width, height)));
        var paddedX = Math.Clamp(x - padding, 0, Math.Max(0, imageWidth - 1));
        var paddedY = Math.Clamp(y - padding, 0, Math.Max(0, imageHeight - 1));
        var paddedRight = Math.Clamp(right + padding, paddedX + 1, imageWidth);
        var paddedBottom = Math.Clamp(bottom + padding, paddedY + 1, imageHeight);

        return new RapidOcrBox(
            paddedX,
            paddedY,
            paddedRight - paddedX,
            paddedBottom - paddedY,
            component.ProbabilitySum / component.Pixels);
    }

    private struct Component
    {
        public int MinX;
        public int MaxX;
        public int MinY;
        public int MaxY;
        public int Pixels;
        public double ProbabilitySum;

        public readonly double Score => ProbabilitySum / Pixels;
    }
}
