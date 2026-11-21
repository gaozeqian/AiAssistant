using AiAssistant.Voice;

namespace AiAssistant.Voice.Tests;

/// <summary>
/// Guards the tuned wake-word defaults: the score was raised to 3.0 after the 13-positive /
/// 2.12 h golden set showed 84.6% at 1.0 and zero false alarms either way (S7-R1).
/// </summary>
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
}
