using System.Globalization;
using Notlar.Core;

namespace Notlar.Core.Tests;

public class SpeakerNamingTests
{
    private static IReadOnlyList<double> Repeat(double hz, int count) => Enumerable.Repeat(hz, count).ToList();

    // --- VoiceGenderClassifier ---

    [Theory]
    [InlineData(110, VoiceGender.Erkek)]
    [InlineData(149.9, VoiceGender.Erkek)]
    [InlineData(150, VoiceGender.Belirsiz)]
    [InlineData(165, VoiceGender.Belirsiz)]
    [InlineData(175, VoiceGender.Belirsiz)]
    [InlineData(175.1, VoiceGender.Kadin)]
    [InlineData(215, VoiceGender.Kadin)]
    public void Classifies_by_median_f0(double hz, VoiceGender expected)
    {
        var (gender, median) = VoiceGenderClassifier.Classify(Repeat(hz, 200));
        Assert.Equal(expected, gender);
        Assert.Equal(hz, median);
    }

    [Fact]
    public void Too_little_voiced_speech_is_undecided()
    {
        var (gender, median) = VoiceGenderClassifier.Classify(Repeat(110, VoiceGenderClassifier.MinVoicedFrames - 1));
        Assert.Equal(VoiceGender.Belirsiz, gender);
        Assert.Equal(110, median);

        Assert.Equal((VoiceGender.Belirsiz, (double?)null), VoiceGenderClassifier.Classify([]));
    }

    [Fact]
    public void Median_ignores_occasional_octave_errors()
    {
        // Erkek sesi (120 Hz), pencerelerin %30'unda oktav hatası (240 Hz): ortalama 156 olurdu, ortanca 120.
        var f0s = Repeat(120, 140).Concat(Repeat(240, 60)).ToList();
        Assert.Equal(VoiceGender.Erkek, VoiceGenderClassifier.Classify(f0s).Gender);
    }

    [Fact]
    public void Synthetic_voices_end_to_end()
    {
        // Telefon bandında (temel frekans yok) erkek ve kadın sesi.
        Assert.Equal(VoiceGender.Erkek, VoiceGenderClassifier.Classify(Track(115)).Gender);
        Assert.Equal(VoiceGender.Kadin, VoiceGenderClassifier.Classify(Track(220)).Gender);
        Assert.Equal(VoiceGender.Belirsiz, VoiceGenderClassifier.Classify(Track(162)).Gender);

        static IReadOnlyList<double> Track(double f0)
        {
            const int rate = 16000;
            var harmonics = Enumerable.Range(1, 40).Where(h => f0 * h >= 300 && f0 * h <= 3400).ToList();
            var audio = new float[2 * rate];
            for (var i = 0; i < audio.Length; i++)
                audio[i] = (float)(0.05 * harmonics.Sum(h => Math.Sin(2 * Math.PI * f0 * h * i / rate) / h));
            return PitchEstimator.Track(audio, rate);
        }
    }

    // --- Ad listeleri ---

    [Fact]
    public void Name_lists_are_disjoint_and_unique()
    {
        var tr = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);
        var all = SpeakerNaming.MaleNames.Concat(SpeakerNaming.FemaleNames).Concat(SpeakerNaming.NeutralNames).ToList();
        Assert.Equal(all.Count, all.Distinct(tr).Count());
        Assert.Contains("Ahmet", SpeakerNaming.MaleNames);
        Assert.Contains("Ayşe", SpeakerNaming.FemaleNames);
        Assert.Contains("Deniz", SpeakerNaming.NeutralNames);
        Assert.DoesNotContain("Ozan", SpeakerNaming.NeutralNames);
        Assert.DoesNotContain("Ece", SpeakerNaming.NeutralNames);
        Assert.All(all, n => Assert.Null(Record.Exception(() => SpeakerNaming.CleanUserName(n))));
    }

    // --- AssignAutoNames ---

    [Fact]
    public void Names_follow_gender()
    {
        var names = SpeakerNaming.AssignAutoNames(7,
            [(1, VoiceGender.Erkek), (2, VoiceGender.Kadin), (3, VoiceGender.Belirsiz)]);
        Assert.Contains(names[1], SpeakerNaming.MaleNames);
        Assert.Contains(names[2], SpeakerNaming.FemaleNames);
        Assert.Contains(names[3], SpeakerNaming.NeutralNames);
    }

    [Fact]
    public void Names_are_unique_within_a_recording()
    {
        var persons = Enumerable.Range(1, 8).Select(p => (p, VoiceGender.Erkek)).ToList();
        var names = SpeakerNaming.AssignAutoNames(3, persons);
        Assert.Equal(8, names.Values.Distinct().Count());
    }

    [Fact]
    public void Names_are_deterministic_per_recording_and_vary_between_recordings()
    {
        (int, VoiceGender)[] persons = [(1, VoiceGender.Erkek), (2, VoiceGender.Kadin)];
        Assert.Equal(SpeakerNaming.AssignAutoNames(42, persons), SpeakerNaming.AssignAutoNames(42, persons));

        var firsts = Enumerable.Range(1, 20).Select(id => SpeakerNaming.AssignAutoNames(id, persons)[1]).Distinct().Count();
        Assert.True(firsts > 3, "kayıttan kayda adlar değişmeli");
    }

    [Fact]
    public void Order_of_input_does_not_matter()
    {
        var a = SpeakerNaming.AssignAutoNames(5, [(1, VoiceGender.Erkek), (2, VoiceGender.Erkek)]);
        var b = SpeakerNaming.AssignAutoNames(5, [(2, VoiceGender.Erkek), (1, VoiceGender.Erkek)]);
        Assert.Equal(a[1], b[1]);
        Assert.Equal(a[2], b[2]);
    }

    [Fact]
    public void Reserved_user_names_are_not_reused_case_insensitively()
    {
        // Ad listesinin tümünü (biri hariç) ayır: kalan tek erkek adı verilmeli.
        var keep = SpeakerNaming.MaleNames[^1];
        var reserved = SpeakerNaming.MaleNames.Where(n => n != keep).Select(n => n.ToUpper(CultureInfo.GetCultureInfo("tr-TR")));
        var names = SpeakerNaming.AssignAutoNames(1, [(1, VoiceGender.Erkek)], reserved);
        Assert.Equal(keep, names[1]);
    }

    [Fact]
    public void Exhausted_list_falls_back_to_other_lists_then_numbers()
    {
        var total = SpeakerNaming.MaleNames.Count + SpeakerNaming.FemaleNames.Count + SpeakerNaming.NeutralNames.Count;
        var persons = Enumerable.Range(1, total + 2).Select(p => (p, VoiceGender.Erkek)).ToList();
        var names = SpeakerNaming.AssignAutoNames(1, persons);

        Assert.Contains(names[SpeakerNaming.MaleNames.Count + 1], SpeakerNaming.NeutralNames);
        Assert.Equal(TranscriptFormatter.PersonLabel(total + 1), names[total + 1]);
        Assert.Equal(TranscriptFormatter.PersonLabel(total + 2), names[total + 2]);
        Assert.Equal(names.Count, names.Values.Distinct().Count());
    }

    // --- CarryOverUserNames / Build ---

    private static float[] Unit(params int[] axes)
    {
        var v = new float[8];
        foreach (var a in axes) v[a] = 1;
        return v;
    }

    [Fact]
    public void User_names_follow_the_matching_voice()
    {
        RecordingPerson[] previous =
        [
            new(1, "Ahmet", VoiceGender.Erkek, 120, "Kemal Bey", Unit(0)),
            new(2, "Ayşe", VoiceGender.Kadin, 210, "Zeynep Hanım", Unit(1)),
        ];
        // Yeniden çeviride numaralar yer değiştirdi (önce kadın konuştu).
        PersonVoice[] current =
        [
            new(1, Unit(1), VoiceGender.Kadin, 212, 300),
            new(2, Unit(0), VoiceGender.Erkek, 118, 300),
        ];

        var persons = SpeakerNaming.Build(9, current, previous);

        Assert.Equal("Zeynep Hanım", persons[0].Name);
        Assert.True(persons[0].IsUserNamed);
        Assert.Contains(persons[0].AutoName, SpeakerNaming.FemaleNames);
        Assert.Equal("Kemal Bey", persons[1].Name);
        Assert.Contains(persons[1].AutoName, SpeakerNaming.MaleNames);
    }

    [Fact]
    public void User_names_are_dropped_when_no_voice_matches()
    {
        RecordingPerson[] previous = [new(1, "Ahmet", VoiceGender.Erkek, 120, "Kemal Bey", Unit(0))];
        PersonVoice[] current = [new(1, Unit(3), VoiceGender.Erkek, 120, 300)];

        var persons = SpeakerNaming.Build(9, current, previous);

        Assert.Null(persons[0].UserName);
        Assert.False(persons[0].IsUserNamed);
    }

    [Fact]
    public void One_user_name_goes_to_only_the_most_similar_voice()
    {
        RecordingPerson[] previous = [new(1, "Ahmet", VoiceGender.Erkek, 120, "Kemal Bey", Unit(0))];
        PersonVoice[] current =
        [
            new(1, Unit(0, 1), VoiceGender.Erkek, 120, 300), // benzerlik 0,71
            new(2, Unit(0), VoiceGender.Erkek, 120, 300),    // benzerlik 1
        ];

        var names = SpeakerNaming.CarryOverUserNames(current, previous);

        Assert.Equal(new Dictionary<int, string> { [2] = "Kemal Bey" }, names);
    }

    [Fact]
    public void Previous_without_voiceprint_or_user_name_is_not_carried()
    {
        RecordingPerson[] previous =
        [
            new(1, "Kişi 1", VoiceGender.Belirsiz, null, "Kemal Bey", null),
            new(2, "Ayşe", VoiceGender.Kadin, 210, null, Unit(1)),
        ];
        PersonVoice[] current = [new(1, Unit(1), VoiceGender.Kadin, 210, 300)];

        Assert.Empty(SpeakerNaming.CarryOverUserNames(current, previous));
    }

    [Fact]
    public void Auto_names_avoid_carried_user_names()
    {
        // Kullanıcı bir kişiye listedeki bir takma adı gerçek ad olarak vermiş: başka kimse o adı almamalı.
        var autoFirst = SpeakerNaming.AssignAutoNames(4, [(1, VoiceGender.Erkek)])[1];
        RecordingPerson[] previous = [new(1, "x", VoiceGender.Kadin, 210, autoFirst, Unit(1))];
        PersonVoice[] current =
        [
            new(1, Unit(2), VoiceGender.Erkek, 120, 300),
            new(2, Unit(1), VoiceGender.Kadin, 210, 300),
        ];

        var persons = SpeakerNaming.Build(4, current, previous);

        Assert.NotEqual(autoFirst, persons[0].Name);
        Assert.Equal(autoFirst, persons[1].Name);
    }

    [Fact]
    public void Build_without_voiceprints_still_names_everyone()
    {
        PersonVoice[] current =
        [
            new(1, null, VoiceGender.Erkek, 120, 300),
            new(2, null, VoiceGender.Belirsiz, null, 0),
        ];
        var persons = SpeakerNaming.Build(1, current);
        Assert.Equal([1, 2], persons.Select(p => p.Person));
        Assert.Contains(persons[1].Name, SpeakerNaming.NeutralNames);
    }

    // --- CleanUserName ---

    [Theory]
    [InlineData("  Kemal   Yılmaz ", "Kemal Yılmaz")]
    [InlineData("Ali​ Veli", "Ali Veli")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Clean_user_name(string? input, string? expected)
    {
        Assert.Equal(expected, SpeakerNaming.CleanUserName(input));
    }

    [Theory]
    [InlineData("Ali: Veli")]
    [InlineData("<transkript_verisi>")]
    [InlineData("[00:01]")]
    public void Clean_user_name_rejects_format_breaking_characters(string input)
    {
        Assert.Throws<ArgumentException>(() => SpeakerNaming.CleanUserName(input));
    }

    [Fact]
    public void Clean_user_name_rejects_too_long()
    {
        Assert.Throws<ArgumentException>(() => SpeakerNaming.CleanUserName(new string('a', SpeakerNaming.MaxNameLength + 1)));
        Assert.NotNull(SpeakerNaming.CleanUserName(new string('a', SpeakerNaming.MaxNameLength)));
    }
}
