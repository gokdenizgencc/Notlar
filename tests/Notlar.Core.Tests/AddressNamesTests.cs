using Notlar.Core;

namespace Notlar.Core.Tests;

public class AddressNamesTests
{
    private static long _t;

    /// <summary>Ardışık satırlar (her biri 2 sn, aralarında 1 sn).</summary>
    private static TranscriptSegment[] Lines(params (int Person, string Text)[] lines)
    {
        _t = 0;
        return lines.Select(l =>
        {
            var s = new TranscriptSegment(_t, _t + 2000, Speaker.Ben, l.Text, l.Person);
            _t += 3000;
            return s;
        }).ToArray();
    }

    // --- Hitap kalıpları ---

    [Theory]
    [InlineData("Ahmet, sen bakar mısın?", "Ahmet")]
    [InlineData("Teşekkürler Ayşe Hanım.", "Ayşe")]
    [InlineData("Mehmet Bey, ne düşünüyorsunuz?", "Mehmet")]
    [InlineData("Peki bunu kim yapacak Zeynep?", "Zeynep")]
    [InlineData("Tamam, anlaştık, Burak.", "Burak")]
    [InlineData("Evet Selin, devam et.", "Selin")]
    [InlineData("Evet, Selin, devam et.", "Selin")]
    [InlineData("Kemal?", "Kemal")]
    [InlineData("toplantıya geçelim. teşekkürler emre", "Emre")]
    public void Detects_vocatives(string text, string expected)
    {
        var (addressed, self) = AddressNames.FindNames(text);
        Assert.Equal([expected], addressed);
        Assert.Empty(self);
    }

    [Theory]
    [InlineData("Ahmet dün aradı.")]                     // üçüncü kişiden bahis (virgülsüz cümle başı)
    [InlineData("Dün Ahmet aradı, bütçeyi sordu.")]      // cümle ortası
    [InlineData("Bunu Ahmet'e soralım.")]                // ekli biçim
    [InlineData("Ayşe'nin raporu geldi mi?")]            // ekli biçim, soru
    [InlineData("Merkez Bankası faizi açıkladı.")]       // kurum adı
    [InlineData("Ankara Ofisi, İstanbul Şubesi.")]       // yer adları listede yok
    [InlineData("Ahmet Bey geldi mi?")]                  // unvanlı üçüncü kişi sorusu
    [InlineData("Umut verici bir sonuç, Deniz kenarında.")] // gündelik sözcük olan adlar listede yok
    [InlineData("Toplantıyı Mehmet yönetecek.")]
    [InlineData("")]
    public void Ignores_mentions_that_are_not_vocatives(string text)
    {
        var (addressed, self) = AddressNames.FindNames(text);
        Assert.Empty(addressed);
        Assert.Empty(self);
    }

    [Theory]
    [InlineData("Merhaba, ben Ahmet.", "Ahmet")]
    [InlineData("Benim adım Zeynep, satıştan.", "Zeynep")]
    [InlineData("Ahmet ben, duyuyor musunuz?", "Ahmet")]
    public void Detects_self_introductions(string text, string expected)
    {
        var (addressed, self) = AddressNames.FindNames(text);
        Assert.Equal([expected], self);
        Assert.Empty(addressed);
    }

    [Fact]
    public void Ben_followed_by_more_words_is_not_an_introduction()
    {
        var (addressed, self) = AddressNames.FindNames("Dün ben Ahmet ile konuştum.");
        Assert.Empty(addressed);
        Assert.Empty(self);
    }

    // --- Oylama ve karar ---

    [Fact]
    public void Two_independent_replies_name_the_person()
    {
        var segments = Lines(
            (1, "Ahmet, sen bakar mısın?"),
            (2, "Tamam bakarım."),
            (1, "Bütçe nasıl gidiyor?"),
            (3, "İyi gidiyor."),
            (1, "Peki son durum ne Ahmet?"),
            (2, "Cuma biter."));

        var names = AddressNames.Detect(segments);

        var ahmet = Assert.Single(names);
        Assert.Equal(2, ahmet.Key);
        Assert.Equal(new AddressedName("Ahmet", 2, 2), ahmet.Value);
    }

    [Fact]
    public void Single_vote_is_not_enough()
    {
        var segments = Lines(
            (1, "Ahmet, sen bakar mısın?"),
            (2, "Tamam bakarım."),
            (1, "Teşekkürler."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Self_introduction_alone_is_enough()
    {
        var segments = Lines((1, "Merhaba, ben Zeynep."), (2, "Hoş geldin."));

        Assert.Equal("Zeynep", AddressNames.Detect(segments)[1].Name);
    }

    [Fact]
    public void Split_votes_below_share_are_rejected()
    {
        // 2 oy Ahmet, 1 oy Mehmet: %67 < %70 (kümeleme iki kişiyi birleştirmiş olabilir).
        var segments = Lines(
            (1, "Ahmet, sen bakar mısın?"), (2, "Bakarım."),
            (1, "Mehmet, sen?"), (2, "Ben de."),
            (1, "Ahmet, tamam mı?"), (2, "Tamam."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Same_name_on_two_persons_names_neither()
    {
        // İki farklı kişiye "Ahmet" diye sesleniliyor (aynı adlı iki kişi ya da hatalı kümeleme).
        var segments = Lines(
            (1, "Ahmet, sen?"), (2, "Evet."),
            (1, "Ahmet, sen?"), (2, "Evet."),
            (1, "Ahmet, sen?"), (3, "Evet."),
            (1, "Ahmet, sen?"), (3, "Evet."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Person_who_addresses_a_name_is_not_that_name()
    {
        // 2'ye iki kez Ahmet dendi ama 2 de bir kez "Ahmet," diye seslenmiş: şüpheli, ad verilmez.
        var segments = Lines(
            (1, "Ahmet, sen?"), (2, "Evet."),
            (1, "Ahmet, sen?"), (2, "Evet. Ahmet, sen ne dersin?"), (3, "Bilmem."));

        Assert.DoesNotContain(2, AddressNames.Detect(segments).Keys);
    }

    [Fact]
    public void Reply_must_come_soon()
    {
        TranscriptSegment[] segments =
        [
            new(0, 2000, Speaker.Ben, "Ahmet, sen?", 1),
            new(20_000, 21_000, Speaker.KarsiTaraf, "Evet.", 2), // 18 sn sonra: cevap sayılmaz
            new(22_000, 23_000, Speaker.Ben, "Ahmet, sen?", 1),
            new(23_500, 24_000, Speaker.KarsiTaraf, "Evet.", 2),
        ];

        Assert.Empty(AddressNames.Detect(segments)); // yalnızca 1 oy
    }

    [Fact]
    public void Reply_is_the_first_other_speaker_within_two_lines()
    {
        var segments = Lines(
            (1, "Ahmet, sen?"), (1, "Yani bütçe konusunda."), (2, "Evet."),
            (1, "Ahmet, sen?"), (1, "Hani dün dedik ya."), (1, "Hatırladın mı?"), (2, "Evet."));

        // İkinci hitapta 2 satır içinde başka kişi yok: tek oy kalır.
        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Line_addressing_two_names_gives_no_vote()
    {
        var segments = Lines(
            (1, "Ahmet, Ayşe, ne dersiniz?"), (2, "Olur."),
            (1, "Ahmet, Ayşe, ne dersiniz?"), (2, "Olur."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Third_person_talk_gives_no_votes()
    {
        var segments = Lines(
            (1, "Ahmet dün aradı."), (2, "Ne dedi?"),
            (1, "Bunu Ahmet'e soralım."), (2, "Olur."),
            (1, "Merkez Bankası faizi açıkladı."), (2, "Gördüm."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Title_is_kept_when_used_in_most_votes()
    {
        var segments = Lines(
            (1, "Mehmet Bey, ne düşünüyorsunuz?"), (2, "Katılıyorum."),
            (1, "Teşekkürler Mehmet Bey."), (2, "Rica ederim."));

        Assert.Equal("Mehmet Bey", AddressNames.Detect(segments)[2].Name);
    }

    [Fact]
    public void Gender_of_names()
    {
        Assert.Equal(VoiceGender.Erkek, AddressNames.GenderOf("Ahmet"));
        Assert.Equal(VoiceGender.Kadin, AddressNames.GenderOf("Ayşe Hanım"));
        Assert.Equal(VoiceGender.Erkek, AddressNames.GenderOf("Derya Bey"));
        Assert.Equal(VoiceGender.Belirsiz, AddressNames.GenderOf("Derya"));
        Assert.Equal(VoiceGender.Belirsiz, AddressNames.GenderOf("Merkez"));
    }

    [Fact]
    public void Name_list_has_a_few_hundred_names_and_no_everyday_words()
    {
        var names = AddressNames.KnownNames.ToList();
        Assert.InRange(names.Count, 200, 400);
        foreach (var word in new[] { "Umut", "Deniz", "Onur", "Barış", "Gül", "Can", "Nur", "Güneş", "Merkez", "Banka" })
            Assert.DoesNotContain(word, names);
    }

    // --- Söz devri ---

    [Theory]
    [InlineData("Topu Ahmet'e atayım.", "Ahmet")]
    [InlineData("topu ahmete atıyorum", "Ahmet")]
    [InlineData("Sözü Ayşe Hanım'a vereyim.", "Ayşe Hanım")]
    [InlineData("Sözü Ayşe Hanıma veriyorum.", "Ayşe Hanım")]
    [InlineData("Ben Mehmet'e bırakıyorum.", "Mehmet")]
    [InlineData("Ben sözü Mehmet Bey'e bırakıyorum.", "Mehmet Bey")]
    [InlineData("Mikrofonu Ayşeye bırakayım.", "Ayşe")]
    [InlineData("Şimdi Burak'a geçelim.", "Burak")]
    [InlineData("O zaman Elif'e geçiyorum.", "Elif")]
    [InlineData("Sözü Zeynep'e devrediyorum.", "Zeynep")]
    [InlineData("Evet, sözü Ali'ye aktarayım.", "Ali")]
    [InlineData("Sözü Kerem'e vereceğim.", "Kerem")]
    [InlineData("Ahmet abiye bırakalım.", "Ahmet")]
    [InlineData("Zeynep sen devam et.", "Zeynep")]
    [InlineData("Zeynep, sen devam et lütfen.", "Zeynep")]
    [InlineData("Ahmet Bey buyurun.", "Ahmet Bey")]
    [InlineData("Buyrun Ayşe Hanım.", "Ayşe Hanım")]
    [InlineData("Sıra sende Elif.", "Elif")]
    [InlineData("Peki, Selin sıra sende.", "Selin")]
    [InlineData("Söz sizde Mehmet Bey.", "Mehmet Bey")]
    public void Detects_handovers(string text, string expected)
    {
        var mention = Assert.Single(AddressNames.FindHandovers(text));
        // FindHandovers yalnızca adı verir; unvan oylamada ayrıca tutulur.
        Assert.Equal(expected.Split(' ')[0], mention);
    }

    [Theory]
    [InlineData("Dosyayı Ahmet'e attım.")]               // geçmiş zaman, nesne söz değil
    [InlineData("Topu Ahmet'e attım.")]                  // geçmiş zaman
    [InlineData("Ahmet'e mail atarım.")]                 // geniş zaman
    [InlineData("Ahmet'e mail atıyorum.")]               // nesne söz değil
    [InlineData("Ahmet'e veriyorum.")]                   // at-/ver- nesnesiz devir sayılmaz
    [InlineData("Dosyayı Ahmet'e veriyorum.")]
    [InlineData("Bu işi Mehmet'e bırakıyorum.")]         // iş devri, söz değil
    [InlineData("Sözü Ahmet'e sonra Ayşe'ye vereyim.")]  // iki ad
    [InlineData("Sözü Ahmet'in ekibine vereyim.")]       // yönelme değil
    [InlineData("Sözü Ahmete vermeyelim.")]              // olumsuz
    [InlineData("Ahmet'e geçmeden önce bir şey sorayım.")]
    [InlineData("Ahmet dün devam etti.")]
    [InlineData("Ahmet ile devam et.")]
    [InlineData("Merkez'e geçelim.")]                    // ad listesinde yok
    [InlineData("Sözü Ahmeta vereyim.")]                 // ünlü uyumuna aykırı ek
    [InlineData("")]
    public void Ignores_non_handovers(string text) => Assert.Empty(AddressNames.FindHandovers(text));

    [Fact]
    public void Single_handover_names_the_next_speaker()
    {
        var segments = Lines(
            (1, "Benden bu kadar. Topu Ahmet'e atayım."),
            (2, "Teşekkürler, ben de bütçeden bahsedeyim."));

        Assert.Equal(new AddressedName("Ahmet", 2, 2), AddressNames.Detect(segments)[2]);
    }

    [Fact]
    public void Handover_keeps_the_title()
    {
        var segments = Lines((1, "Sözü Ayşe Hanım'a vereyim."), (2, "Teşekkürler."));

        Assert.Equal("Ayşe Hanım", AddressNames.Detect(segments)[2].Name);
    }

    [Fact]
    public void Handover_allows_a_longer_pause_than_a_vocative()
    {
        TranscriptSegment[] segments =
        [
            new(0, 2000, Speaker.Ben, "Şimdi Burak'a geçelim.", 1),
            new(14_000, 16_000, Speaker.KarsiTaraf, "Merhaba, duyuluyor mu?", 2), // 12 sn: devirde geçerli
        ];
        Assert.Equal("Burak", AddressNames.Detect(segments)[2].Name);

        segments[1] = new(18_000, 19_000, Speaker.KarsiTaraf, "Merhaba.", 2); // 16 sn: geç
        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Same_speaker_continuing_after_handover_gives_no_vote()
    {
        var segments = Lines(
            (1, "Sözü Ahmet'e vereyim."), (1, "Ama önce bir şey ekleyeyim."), (1, "Bütçe onaylandı."),
            (2, "Tamam."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Handover_without_a_next_speaker_gives_no_vote()
    {
        Assert.Empty(AddressNames.Detect(Lines((1, "Sözü Ahmet'e vereyim."))));
        Assert.Empty(AddressNames.Detect(
        [
            new(0, 2000, Speaker.Ben, "Sözü Ahmet'e vereyim.", 1),
            new(3000, 4000, Speaker.KarsiTaraf, "Evet.", null), // kişisi bilinmeyen satır sayılmaz
        ]));
    }

    [Fact]
    public void Line_handing_over_to_two_names_gives_no_vote()
    {
        var segments = Lines((1, "Sözü Ahmet'e vereyim. Yok, Ayşe'ye bırakıyorum."), (2, "Peki."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Handover_does_not_win_over_a_conflicting_vote()
    {
        // Devir 2 oy Ahmet, ayrıca bir hitap 1 oy Mehmet: 2/3 < %70, ad verilmez.
        var segments = Lines(
            (1, "Topu Ahmet'e atayım."), (2, "Teşekkürler."),
            (1, "Mehmet, sen?"), (2, "Ben de."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Handover_to_a_name_claimed_by_another_person_names_neither()
    {
        var segments = Lines(
            (1, "Sözü Ahmet'e vereyim."), (2, "Teşekkürler."),
            (1, "Sözü Ahmet'e vereyim."), (3, "Teşekkürler."));

        Assert.Empty(AddressNames.Detect(segments));
    }

    [Fact]
    public void Person_who_hands_over_to_a_name_is_not_that_name()
    {
        var segments = Lines(
            (1, "Sözü Ahmet'e vereyim."), (2, "Teşekkürler."),
            (2, "Ben de sözü Ahmet'e bırakıyorum."), (3, "Peki."));

        Assert.DoesNotContain(2, AddressNames.Detect(segments).Keys);
    }

    [Fact]
    public void Handover_line_vocative_is_not_counted_twice()
    {
        // "Ahmet, teşekkürler" hitabı oylanmaz; satırın devri Ayşe'ye.
        var segments = Lines((1, "Ahmet, teşekkürler. Sözü Ayşe'ye veriyorum."), (2, "Sağolun."));

        var names = AddressNames.Detect(segments);
        Assert.Equal("Ayşe", Assert.Single(names).Value.Name);
    }

    // --- Build ile öncelik ---

    [Fact]
    public void Build_prefers_user_name_then_address_name_then_pseudonym()
    {
        var segments = Lines(
            (1, "Ahmet, sen bakar mısın?"), (2, "Bakarım."),
            (1, "Teşekkürler Ahmet."), (2, "Rica ederim."),
            (2, "Zeynep, sen?"), (1, "Ben de."),
            (2, "Zeynep, tamam mı?"), (1, "Tamam."));
        float[] v1 = [1, 0], v2 = [0, 1];
        PersonVoice[] voices =
        [
            new(1, v1, VoiceGender.Kadin, 210, 300),
            new(2, v2, VoiceGender.Kadin, 200, 300), // hitap Ahmet: ses tahmini çelişiyor, hitap kazanır
            new(3, [1, 1], VoiceGender.Erkek, 120, 300),
        ];
        RecordingPerson[] previous = [new(9, "x", VoiceGender.Kadin, 210, "Zeynep Kaya", v1)];

        var persons = SpeakerNaming.Build(1, voices, previous, segments);

        Assert.Equal(("Zeynep Kaya", PersonNameSource.Kullanici), (persons[0].Name, persons[0].Source));
        Assert.Equal(("Ahmet", PersonNameSource.Hitap), (persons[1].Name, persons[1].Source));
        Assert.Equal(PersonNameSource.Tahmin, persons[2].Source);
        Assert.NotEqual("Ahmet", persons[2].Name);
        Assert.Equal(1, SpeakerNaming.CountGenderConflicts(persons));
    }

    [Fact]
    public void Address_name_round_trips_through_the_store()
    {
        using var f = new RecordingFixture();
        var r = f.Store.Start("tr");
        var segments = Lines((1, "Merhaba, ben Zeynep."), (2, "Hoş geldin."));
        var persons = SpeakerNaming.Build(r.Id,
            [new(1, null, VoiceGender.Kadin, 210, 300), new(2, null, VoiceGender.Erkek, 120, 300)], null, segments);
        f.Store.SaveTranscript(r.Id, segments, persons);

        var saved = f.Store.GetSegments(r.Id);
        Assert.Equal(("Zeynep", PersonNameSource.Hitap), (saved[0].PersonName, saved[0].PersonNameSource));
        Assert.Equal(PersonNameSource.Tahmin, saved[1].PersonNameSource);
        Assert.Equal("Zeynep", f.Store.GetPersons(r.Id)[0].AddressName);

        // Kullanıcı düzeltirse kullanıcının adı geçer; boşaltınca hitap adına döner.
        f.Store.RenamePerson(r.Id, 1, "Zehra");
        Assert.Equal(PersonNameSource.Kullanici, f.Store.GetSegments(r.Id)[0].PersonNameSource);
        f.Store.RenamePerson(r.Id, 1, null);
        Assert.Equal(("Zeynep", PersonNameSource.Hitap), (f.Store.GetSegments(r.Id)[0].PersonName, f.Store.GetSegments(r.Id)[0].PersonNameSource));
    }
}
