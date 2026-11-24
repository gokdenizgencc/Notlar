using Notlar.Core;

namespace Notlar.Core.Tests;

public class AudioPreferencesTests
{
    [Theory]
    [InlineData("auto", TranscriptionProcessor.Auto)]
    [InlineData("GPU", TranscriptionProcessor.Gpu)]
    [InlineData(" cpu ", TranscriptionProcessor.Cpu)]
    [InlineData(null, TranscriptionProcessor.Auto)]
    [InlineData("", TranscriptionProcessor.Auto)]
    [InlineData("tpu", TranscriptionProcessor.Auto)]
    public void ParseProcessor_falls_back_to_auto(string? value, TranscriptionProcessor expected) =>
        Assert.Equal(expected, AudioPreferences.ParseProcessor(value));

    [Theory]
    [InlineData(TranscriptionProcessor.Auto)]
    [InlineData(TranscriptionProcessor.Gpu)]
    [InlineData(TranscriptionProcessor.Cpu)]
    public void Processor_round_trips(TranscriptionProcessor processor) =>
        Assert.Equal(processor, AudioPreferences.ParseProcessor(AudioPreferences.FormatProcessor(processor)));

    [Fact]
    public void ResolveDevice_uses_default_when_nothing_chosen()
    {
        Assert.Null(AudioPreferences.ResolveDevice(null, ["a", "b"], out var missing));
        Assert.False(missing);
        Assert.Null(AudioPreferences.ResolveDevice("  ", ["a"], out missing));
        Assert.False(missing);
    }

    [Fact]
    public void ResolveDevice_finds_chosen_device_case_insensitively()
    {
        Assert.Equal("{0.0.1}.{ABC}", AudioPreferences.ResolveDevice("{0.0.1}.{abc}", ["x", "{0.0.1}.{ABC}"], out var missing));
        Assert.False(missing);
    }

    [Fact]
    public void ResolveDevice_falls_back_when_device_is_gone()
    {
        Assert.Null(AudioPreferences.ResolveDevice("gone", ["a", "b"], out var missing));
        Assert.True(missing);
        Assert.Null(AudioPreferences.ResolveDevice("gone", [], out missing));
        Assert.True(missing);
    }

    [Fact]
    public void Peak_is_max_absolute_clamped()
    {
        Assert.Equal(0f, AudioPreferences.Peak([]));
        Assert.Equal(0.7f, AudioPreferences.Peak([0.1f, -0.7f, 0.3f]));
        Assert.Equal(1f, AudioPreferences.Peak([2f]));
        Assert.Equal(0.2f, AudioPreferences.Peak([float.NaN, 0.2f]));
    }

    [Fact]
    public void MeterFraction_maps_decibels()
    {
        Assert.Equal(0, AudioPreferences.MeterFraction(0));
        Assert.Equal(0, AudioPreferences.MeterFraction(float.NaN));
        Assert.Equal(1, AudioPreferences.MeterFraction(1f), 6);
        Assert.Equal(0.5, AudioPreferences.MeterFraction(0.0316228f), 3); // -30 dBFS → çubuğun ortası
        Assert.Equal(0, AudioPreferences.MeterFraction(0.0001f));          // -80 dBFS → taban altı
    }

    [Fact]
    public void Smooth_rises_immediately_and_falls_slowly()
    {
        Assert.Equal(0.8, AudioPreferences.Smooth(0.2, 0.8));
        Assert.Equal(0.76, AudioPreferences.Smooth(0.8, 0.1), 6);
        Assert.Equal(0.1, AudioPreferences.Smooth(0.12, 0.1), 6);
    }
}
