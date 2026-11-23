using AiAssistant.Voice;

namespace AiAssistant.Voice.Tests;

/// <summary>
/// Guards the tuned wake-word defaults: the score was raised to 3.0 after the 13-positive /
/// 2.12 h golden set showed 84.6% at 1.0 and zero false alarms either way (S7-R1).
/// </summary>
[Trait("Category", "Unit")]
public sealed class SherpaVoiceOptionsTests
{
    [Fact]
    public void KeywordTuningDefaultsMatchGoldenSetResult()
    {
        var options = new SherpaVoiceOptions();

        Assert.Equal(3.0f, options.KeywordsScore);
        Assert.Equal(0.25f, options.KeywordsThreshold);
    }

    [Fact]
    public void KeywordTuningValuesCanBeOverridden()
    {
        var options = new SherpaVoiceOptions { KeywordsScore = 1.5f, KeywordsThreshold = 0.4f };

        Assert.Equal(1.5f, options.KeywordsScore);
        Assert.Equal(0.4f, options.KeywordsThreshold);
    }

    /// <summary>
    /// The bundled MeloTTS payload adds a jieba dictionary and four text-normalization FSTs.
    /// If the staging script or these defaults drift apart, TTS still starts but reads numbers,
    /// phone numbers and heteronyms incorrectly, so pin the layout here.
    /// </summary>
    [Fact]
    public void TtsDefaultsMatchBundledMeloTtsLayout()
    {
        var root = Path.Combine("models", "sherpa");
        var options = new SherpaVoiceOptions { ModelDirectory = root };

        Assert.Equal(Path.Combine(root, "tts", "model.onnx"), options.TtsModelPath);
        Assert.Equal(Path.Combine(root, "tts", "dict"), options.TtsDictDirPath);
        Assert.Collection(
            options.TtsRuleFstsPaths,
            path => Assert.Equal(Path.Combine(root, "tts", "date.fst"), path),
            path => Assert.Equal(Path.Combine(root, "tts", "number.fst"), path),
            path => Assert.Equal(Path.Combine(root, "tts", "phone.fst"), path),
            path => Assert.Equal(Path.Combine(root, "tts", "new_heteronym.fst"), path));
    }
}
