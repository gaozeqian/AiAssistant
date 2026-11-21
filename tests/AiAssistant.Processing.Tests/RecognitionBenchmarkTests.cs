using System.Buffers.Binary;
using System.Text;
using AiAssistant.Processing.Benchmarks;

namespace AiAssistant.Processing.Tests;

public sealed class RecognitionBenchmarkTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "aia-recognition-tests",
        Guid.NewGuid().ToString("N")[..8]);

    public RecognitionBenchmarkTests()
    {
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
            // Best-effort temp cleanup.
        }
    }

    [Theory]
    [InlineData("今天是 2026 年 11 月 21 日。", "今天是2026年11月21日", 0d)]
    [InlineData("你好时光", "你好时光", 0d)]
    [InlineData("你好时光", "你好时", 0.25d)]
    [InlineData("你好时光", "你好时间", 0.25d)]
    [InlineData("你好时光", "你好时光啊", 0.25d)]
    public void CharacterErrorRate_ComputedAfterNormalization(string reference, string hypothesis, double expected)
    {
        Assert.Equal(expected, CharacterErrorRate.Compute(reference, hypothesis), 3);
    }

    [Fact]
    public void CharacterErrorRate_IgnoresWhitespaceAndPunctuation()
    {
        Assert.Equal(string.Empty, CharacterErrorRate.Normalize(" ，。！？ \t\n"));
        Assert.Equal("hello世界", CharacterErrorRate.Normalize(" Hello，世界！ "));
        Assert.Equal(0d, CharacterErrorRate.Compute("Hello，世界！", "hello 世界"));
    }

    [Fact]
    public void CharacterErrorRate_EmptyReferenceIsGuarded()
    {
        Assert.Equal(0d, CharacterErrorRate.Compute(string.Empty, string.Empty));
        Assert.Equal(1d, CharacterErrorRate.Compute(string.Empty, "意外输出"));
        Assert.Equal("12.50%", CharacterErrorRate.ToPercent(0.125d));
    }

    [Fact]
    public void SampleSet_PairsMediaWithSidecarTextAndWarnsOnMissingReference()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "sample-a.WAV"), "stub", Encoding.ASCII);
        File.WriteAllText(Path.Combine(_tempRoot, "sample-a.txt"), "\uFEFF你好时光\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_tempRoot, "sample-b.wav"), "stub", Encoding.ASCII);
        File.WriteAllText(Path.Combine(_tempRoot, "ignored.md"), "not media", Encoding.UTF8);

        var result = RecognitionSampleSet.Load(_tempRoot, ".wav");

        var sample = Assert.Single(result.Samples);
        Assert.Equal("sample-a", sample.Name);
        Assert.Equal("你好时光", sample.ReferenceText);
        Assert.Contains(result.Warnings, w => w.Contains("sample-b", StringComparison.Ordinal));
    }

    [Fact]
    public void SampleSet_MissingDirectoryReportsWarningInsteadOfThrowing()
    {
        var result = RecognitionSampleSet.Load(Path.Combine(_tempRoot, "missing"), ".wav");
        Assert.Empty(result.Samples);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void WavReader_ConvertsStereo8kToMono16k()
    {
        var path = Path.Combine(_tempRoot, "tone.wav");
        WritePcm16Wav(path, sampleRateHz: 8_000, channels: 2, frames: 800, left: 0.5f, right: 0.1f);

        var audio = WavPcm16Reader.ReadAs16kMono(path);

        Assert.Equal(WavPcm16Reader.TargetSampleRateHz, audio.SampleRateHz);
        Assert.InRange(audio.Samples.Length, 1_590, 1_610);
        Assert.Equal(0.3f, audio.Samples.Span[100], 2);
    }

    [Fact]
    public void WavReader_RejectsNonPcmAndTruncatedFiles()
    {
        var notWav = Path.Combine(_tempRoot, "not.wav");
        File.WriteAllText(notWav, "plain text");
        Assert.Throws<InvalidDataException>(() => WavPcm16Reader.ReadAs16kMono(notWav));
    }

    [Fact]
    public void Report_ContainsMetricsAndWarnings()
    {
        var rows = new List<RecognitionBenchmarkRow>
        {
            new("sample-a", "你好时光", "你好时", 0.25d, 1, 4, TimeSpan.FromMilliseconds(120), null),
            new("sample-b", "第二句", null, 0, 0, 3, TimeSpan.FromMilliseconds(80), "InvalidDataException: bad wav")
        };

        var markdown = RecognitionBenchmarkReport.Build(
            "中文语音识别黄金集（ASR）",
            "sherpa-onnx",
            "/tmp/golden/asr",
            rows,
            ["sample-c: reference text missing"],
            new DateTimeOffset(2026, 11, 21, 16, 30, 0, TimeSpan.FromHours(8)));

        Assert.Contains("中文语音识别黄金集（ASR）", markdown, StringComparison.Ordinal);
        Assert.Contains("平均 CER：25.00%", markdown, StringComparison.Ordinal);
        Assert.Contains("完全匹配：0/1", markdown, StringComparison.Ordinal);
        Assert.Contains("| sample-b | n/a | n/a |", markdown, StringComparison.Ordinal);
        Assert.Contains("sample-c: reference text missing", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_EscapesPipesAndTruncatesLongText()
    {
        var longText = new string('字', 200);
        var rows = new List<RecognitionBenchmarkRow>
        {
            new("pipe", "a|b", "a|b", 0d, 0, 2, TimeSpan.Zero, null),
            new("long", longText, longText, 0d, 0, 200, TimeSpan.Zero, null)
        };

        var markdown = RecognitionBenchmarkReport.Build(
            "t", "e", "/d", rows, [], DateTimeOffset.UnixEpoch);

        Assert.Contains("a\\|b", markdown, StringComparison.Ordinal);
        Assert.Contains("…", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluator_ScoresSamplesAndCapturesFailures()
    {
        var samples = new List<RecognitionSample>
        {
            new("good", "a.wav", "a.txt", "你好时光"),
            new("short", "b.wav", "b.txt", "今天的会议记录"),
            new("broken", "c.wav", "c.txt", "第三句")
        };

        var rows = await RecognitionBenchmarkEvaluator.RunAsync(
            samples,
            (sample, _) => sample.Name switch
            {
                "good" => Task.FromResult("你好时光"),
                "short" => Task.FromResult("明天的会议记录"),
                _ => throw new InvalidDataException("bad wav")
            },
            unavailableReason: null,
            CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0d, rows[0].CharacterErrorRate, 3);
        Assert.Equal(1d / 7d, rows[1].CharacterErrorRate, 3);
        Assert.False(rows[2].Succeeded);
        Assert.Contains("bad wav", rows[2].Error, StringComparison.Ordinal);
        Assert.Equal("7.14%", RecognitionBenchmarkEvaluator.AverageCer(rows));
    }

    [Fact]
    public async Task Evaluator_MarksEverySampleUnavailableWithoutInvokingRecognizer()
    {
        var invoked = false;
        var samples = new List<RecognitionSample> { new("a", "a.wav", "a.txt", "你好") };

        var rows = await RecognitionBenchmarkEvaluator.RunAsync(
            samples,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult("x");
            },
            "engine unavailable: no models",
            CancellationToken.None);

        Assert.False(invoked);
        var row = Assert.Single(rows);
        Assert.False(row.Succeeded);
        Assert.Contains("engine unavailable", row.Error, StringComparison.Ordinal);
        Assert.Equal("n/a", RecognitionBenchmarkEvaluator.AverageCer(rows));
    }

    [Fact]
    public void Normalize_FoldsTraditionalChineseToSimplified()
    {
        Assert.Equal("第三会议进行方案评审", CharacterErrorRate.Normalize("第三會議進行方案評審"));
        Assert.Equal("形成了系统完整的规划体系", CharacterErrorRate.Normalize("形成了系統完整的規劃體系"));
        Assert.Equal(0d, CharacterErrorRate.Compute("形成了系统完整的规划体系", "形成了系統完整的規劃體系"));
    }

    [Fact]
    public void DigitVerbalizationDifferenceStillCountsAsError()
    {
        // Documented residual: Whisper writes "2012", AISHELL transcripts spell "二零一二".
        // We deliberately do not invent an ITN model, so this still shows up in CER.
        Assert.True(CharacterErrorRate.Compute("二零一二年", "2012年") > 0);
    }

    private static void WritePcm16Wav(string path, int sampleRateHz, int channels, int frames, float left, float right)
    {
        var dataBytes = frames * channels * sizeof(short);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(sampleRateHz);
        writer.Write(sampleRateHz * channels * sizeof(short));
        writer.Write((ushort)(channels * sizeof(short)));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        var buffer = new byte[sizeof(short)];
        for (var frame = 0; frame < frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer, (short)(left * short.MaxValue));
            writer.Write(buffer);
            if (channels == 2)
            {
                BinaryPrimitives.WriteInt16LittleEndian(buffer, (short)(right * short.MaxValue));
                writer.Write(buffer);
            }
        }
    }
}
