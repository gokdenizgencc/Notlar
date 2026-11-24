using System.Globalization;

namespace Notlar.Core;

/// <summary>Sesin tonundan kestirilen cinsiyet. Yalnızca takma ad seçmek içindir; kimlik bilgisi değildir.</summary>
public enum VoiceGender { Belirsiz, Erkek, Kadin }

/// <summary>Görünen adın kaynağı (öncelik sırası: Kullanici &gt; Hitap &gt; Tahmin).</summary>
public enum PersonNameSource
{
    /// <summary>Sesin tonundan (erkek/kadın sesi tahmini) verilmiş takma ad ya da adsız eski kayıtta "Kişi N"; gerçek kimlik değil.</summary>
    Tahmin,

    /// <summary>Toplantıda kişiye bu adla hitap edildiği için konuşmadan çıkarılan ad (büyük olasılıkla doğru).</summary>
    Hitap,

    /// <summary>Kullanıcının elle verdiği gerçek ad.</summary>
    Kullanici,
}

/// <summary>Bir kaydın bir konuşmacısı (transkriptteki <see cref="TranscriptSegment.Person"/> numarası).
/// <paramref name="AutoName"/>: uygulamanın sesin tonuna göre verdiği takma ad (eski kayıtlarda "Kişi N").
/// <paramref name="UserName"/>: kullanıcının yazdığı gerçek ad (yoksa null). <paramref name="Voiceprint"/>: kişinin
/// ses gömmelerinin birim ortalaması; yeniden çeviride kullanıcı adını doğru kişiye taşımak için saklanır.
/// <paramref name="AddressName"/>: konuşmadaki hitaplardan emin olunarak çıkarılan ad (bkz. <see cref="AddressNames"/>).</summary>
public sealed record RecordingPerson(
    int Person, string AutoName, VoiceGender Gender, double? MedianF0Hz, string? UserName, float[]? Voiceprint,
    string? AddressName = null)
{
    /// <summary>Transkriptte görünen ad: kullanıcının verdiği ad, yoksa hitaptan çıkan ad, yoksa takma ad.</summary>
    public string Name => UserName ?? AddressName ?? AutoName;

    /// <summary>Görünen adın kaynağı.</summary>
    public PersonNameSource Source =>
        UserName is not null ? PersonNameSource.Kullanici
        : AddressName is not null ? PersonNameSource.Hitap
        : PersonNameSource.Tahmin;

    /// <summary>Ad kullanıcı tarafından verildiyse (gerçek kişi) true.</summary>
    public bool IsUserNamed => UserName is not null;
}

/// <summary>Kişi ayrımından çıkan bir konuşmacının ses özeti: ses parmak izi (gömme merkezi) ve ses tonu.</summary>
public sealed record PersonVoice(int Person, float[]? Voiceprint, VoiceGender Gender, double? MedianF0Hz, int VoicedFrames);

/// <summary>Kişinin F0 değerlerinden (bkz. <see cref="PitchEstimator"/>) erkek/kadın sesi kestirimi.
/// <para>Eşikler: yetişkin erkeklerde konuşma F0'ı tipik olarak ≈85–155 Hz (ortalama ≈110–130), kadınlarda
/// ≈165–255 Hz (ortalama ≈190–220) aralığındadır; Türkçe konuşucular için bildirilen değerler de bu aralıklarda.
/// İki dağılım 150–175 Hz arasında örtüşür (kalın sesli kadın, ince sesli/heyecanlı erkek, çocuk yok sayılır).
/// &lt;150 Hz erkek, &gt;175 Hz kadın, arası belirsiz: belirsizde cinsiyetsiz ad verilir, yanlış cinsiyette ad
/// vermekten iyidir. Ortalama değil ORTANCA kullanılır: tek tük oktav hataları (2× ya da ½×) ve gırtlaksı
/// (creaky) kesitler sonucu kaydırmaz.</para></summary>
public static class VoiceGenderClassifier
{
    /// <summary>Ortanca F0 bunun altındaysa erkek sesi.</summary>
    public const double MaleBelowHz = 150;

    /// <summary>Ortanca F0 bunun üstündeyse kadın sesi.</summary>
    public const double FemaleAboveHz = 175;

    /// <summary>Karar için gereken en az sesli pencere (10 ms adımla ≈1 sn sesli konuşma). Daha azı belirsizdir.</summary>
    public const int MinVoicedFrames = 100;

    public static (VoiceGender Gender, double? MedianHz) Classify(IReadOnlyCollection<double> f0s)
    {
        ArgumentNullException.ThrowIfNull(f0s);
        var median = PitchEstimator.Median(f0s);
        if (median is not { } hz || f0s.Count < MinVoicedFrames) return (VoiceGender.Belirsiz, median);
        if (hz < MaleBelowHz) return (VoiceGender.Erkek, hz);
        if (hz > FemaleAboveHz) return (VoiceGender.Kadin, hz);
        return (VoiceGender.Belirsiz, hz);
    }
}

/// <summary>Konuşmacılara takma ad verir ve kullanıcının verdiği gerçek adları korur.</summary>
public static class SpeakerNaming
{
    /// <summary>Kullanıcı adının en fazla uzunluğu.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Yeniden çeviride önceki kişinin ses parmak izi yeni kişinin parmak iziyle en az bu kadar benziyorsa
    /// (kosinüs) kullanıcının verdiği ad yeni kişiye taşınır. 0,6: kümelemenin "aynı kişi" eşiği 0,5 ve ölçülen
    /// kayıtta farklı kişiler arasında görülen en yüksek benzerlik 0,4 (bkz. <see cref="SpeakerClusteringOptions"/>);
    /// yanlış kişiye gerçek ad yapıştırmak adı kaybetmekten kötü olduğundan biraz daha sıkı.</summary>
    public const double CarryOverSimilarity = 0.6;

    /// <summary>Erkek sesine verilen adlar: Türkiye'de yaygın, yalnızca erkeklerde kullanılan adlar.</summary>
    public static readonly IReadOnlyList<string> MaleNames =
    [
        "Ahmet", "Mehmet", "Mustafa", "Ali", "Hasan", "Hüseyin", "Murat", "Emre", "Burak", "Kemal",
        "Osman", "Yusuf", "Ömer", "Serkan", "Hakan", "Tolga", "Kerem", "Onur", "Selim", "Okan",
    ];

    /// <summary>Kadın sesine verilen adlar: Türkiye'de yaygın, yalnızca kadınlarda kullanılan adlar.</summary>
    public static readonly IReadOnlyList<string> FemaleNames =
    [
        "Ayşe", "Fatma", "Zeynep", "Elif", "Emine", "Merve", "Esra", "Selin", "Büşra", "Gamze",
        "Seda", "Burcu", "Sibel", "Pınar", "Özlem", "Ece", "Melek", "Nazlı", "Leyla", "Hülya",
    ];

    /// <summary>Cinsiyeti belirsiz sese verilen adlar: Türkiye'de hem kadınlarda hem erkeklerde gerçekten yaygın
    /// olanlar (ör. Deniz, Umut, Evren). Yalnızca bir cinsiyette yaygın olanlar (Ozan, Ece, Yağmur…) ve ağırlıkla
    /// tek cinsiyette kullanılanlar (Can, Tuna, Doğa…) yok.</summary>
    public static readonly IReadOnlyList<string> NeutralNames =
    [
        "Deniz", "Umut", "Evren", "Derya", "Ümit", "Özgür", "Bilge", "Ekin", "İlkay", "Güneş", "Yüksel",
    ];

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static IReadOnlyList<string> NamesFor(VoiceGender gender) => gender switch
    {
        VoiceGender.Erkek => MaleNames,
        VoiceGender.Kadin => FemaleNames,
        _ => NeutralNames,
    };

    /// <summary>Kişilerin kayıt bilgisini oluşturur: önceki çeviride kullanıcının verdiği adlar ses parmak izine göre
    /// eşleşen yeni kişilere taşınır (<see cref="CarryOverUserNames"/>); <paramref name="segments"/> verilirse
    /// konuşmadaki hitaplardan emin olunan adlar bulunur (<see cref="AddressNames.Detect"/>); sonra herkese yedek
    /// olarak takma ad verilir (<see cref="AssignAutoNames"/>; kullanıcı ve hitap adlarıyla çakışmaz). Aynı hitap
    /// adı, kullanıcının o adı başka bir kişiye vermişse kullanılmaz.</summary>
    public static IReadOnlyList<RecordingPerson> Build(long recordingId, IReadOnlyList<PersonVoice> voices,
        IReadOnlyList<RecordingPerson>? previous = null, IReadOnlyList<TranscriptSegment>? segments = null)
    {
        ArgumentNullException.ThrowIfNull(voices);
        var comparer = StringComparer.Create(Tr, ignoreCase: true);
        var userNames = CarryOverUserNames(voices, previous ?? []);
        var addressNames = new Dictionary<int, string>();
        if (segments is not null)
        {
            var userSet = new HashSet<string>(userNames.Values, comparer);
            foreach (var (person, addressed) in AddressNames.Detect(segments))
            {
                if (voices.Any(v => v.Person == person) && !userSet.Contains(FirstWord(addressed.Name)) && !userSet.Contains(addressed.Name))
                    addressNames[person] = addressed.Name;
            }
        }
        var reserved = userNames.Values.Concat(addressNames.Values).SelectMany(n => new[] { n, FirstWord(n) });
        var autoNames = AssignAutoNames(recordingId, voices.Select(v => (v.Person, v.Gender)).ToList(), reserved);
        return voices
            .OrderBy(v => v.Person)
            .Select(v => new RecordingPerson(v.Person, autoNames[v.Person], v.Gender, v.MedianF0Hz,
                userNames.GetValueOrDefault(v.Person), v.Voiceprint, addressNames.GetValueOrDefault(v.Person)))
            .ToList();
    }

    /// <summary>Hitaptan çıkan adın cinsiyeti (ad listesi ya da Bey/Hanım) ile ses tonu kestirimi çelişen kişi sayısı
    /// (ikisi de belirliyken farklı). Hitap adı yine kullanılır (ses tahmini daha zayıf kanıttır); sayı teşhis içindir.</summary>
    public static int CountGenderConflicts(IEnumerable<RecordingPerson> persons) =>
        persons.Count(p => p.AddressName is { } name
                           && AddressNames.GenderOf(name) is var g && g != VoiceGender.Belirsiz
                           && p.Gender != VoiceGender.Belirsiz && g != p.Gender);

    private static string FirstWord(string name)
    {
        var space = name.IndexOf(' ');
        return space < 0 ? name : name[..space];
    }

    /// <summary>Her kişiye cinsiyetine uygun listeden bir takma ad verir. Deterministiktir: liste, kayıt id'sinden
    /// türeyen bir kaymayla başlar (kayıttan kayda adlar değişsin ama aynı kayıt yeniden çevrilince aynı kalsın);
    /// kişiler numara sırasıyla sıradaki boş adı alır. Aynı kayıtta iki kişiye aynı ad verilmez ve
    /// <paramref name="reserved"/> adlar (kullanıcının verdikleri) kullanılmaz. Liste tükenirse diğer listelerden
    /// (önce cinsiyetsiz), o da tükenirse "Kişi N" verilir.</summary>
    public static IReadOnlyDictionary<int, string> AssignAutoNames(long recordingId,
        IReadOnlyList<(int Person, VoiceGender Gender)> persons, IEnumerable<string>? reserved = null)
    {
        ArgumentNullException.ThrowIfNull(persons);
        var taken = new HashSet<string>(StringComparer.Create(Tr, ignoreCase: true));
        foreach (var name in reserved ?? []) taken.Add(name.Trim());

        var result = new Dictionary<int, string>();
        foreach (var (person, gender) in persons.OrderBy(p => p.Person))
        {
            if (result.ContainsKey(person)) continue;
            var name = Pick(NamesFor(gender))
                       ?? Pick(NeutralNames)
                       ?? Pick(gender == VoiceGender.Kadin ? MaleNames : FemaleNames)
                       ?? Pick(gender == VoiceGender.Kadin ? FemaleNames : MaleNames)
                       ?? TranscriptFormatter.PersonLabel(person);
            taken.Add(name);
            result[person] = name;
        }
        return result;

        string? Pick(IReadOnlyList<string> list)
        {
            var start = Offset(recordingId, list.Count);
            for (var k = 0; k < list.Count; k++)
            {
                var candidate = list[(start + k) % list.Count];
                if (!taken.Contains(candidate)) return candidate;
            }
            return null;
        }
    }

    /// <summary>Önceki kişilerden kullanıcının ad verdiklerini, ses parmak izi en az <paramref name="minSimilarity"/>
    /// benzeyen yeni kişilere taşır (en benzer çiftten başlayarak bire bir). Parmak izi olmayanlar taşınmaz.</summary>
    public static IReadOnlyDictionary<int, string> CarryOverUserNames(IReadOnlyList<PersonVoice> current,
        IReadOnlyList<RecordingPerson> previous, double minSimilarity = CarryOverSimilarity)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        var pairs = new List<(double Sim, int Person, RecordingPerson Old)>();
        foreach (var old in previous)
        {
            if (old.UserName is null || old.Voiceprint is null) continue;
            foreach (var voice in current)
            {
                var sim = SpeakerClustering.Cosine(voice.Voiceprint, old.Voiceprint);
                if (sim >= minSimilarity) pairs.Add((sim, voice.Person, old));
            }
        }

        var result = new Dictionary<int, string>();
        var used = new HashSet<int>();
        foreach (var (_, person, old) in pairs.OrderByDescending(p => p.Sim).ThenBy(p => p.Person).ThenBy(p => p.Old.Person))
        {
            if (result.ContainsKey(person) || !used.Add(old.Person)) continue;
            result[person] = old.UserName!;
        }
        return result;
    }

    /// <summary>Kullanıcının yazdığı adı temizler: görünmez/kontrol karakterleri atılır, boşluklar teke iner.
    /// Boşsa null (takma ada dön). <see cref="MaxNameLength"/>'ten uzunsa ya da transkript biçimini bozacak
    /// karakter (: &lt; &gt; [ ]) içeriyorsa <see cref="ArgumentException"/>.</summary>
    public static string? CleanUserName(string? name)
    {
        var clean = TextSafety.Flatten(name);
        if (clean.Length == 0) return null;
        if (clean.Length > MaxNameLength)
            throw new ArgumentException($"Ad en fazla {MaxNameLength} karakter olabilir.");
        if (clean.IndexOfAny([':', '<', '>', '[', ']']) >= 0)
            throw new ArgumentException("Ad şu karakterleri içeremez: : < > [ ]");
        return clean;
    }

    /// <summary>Cinsiyetin Türkçe kısa açıklaması ("erkek sesi", "kadın sesi", "belirsiz").</summary>
    public static string Describe(VoiceGender gender) => gender switch
    {
        VoiceGender.Erkek => "erkek sesi",
        VoiceGender.Kadin => "kadın sesi",
        _ => "belirsiz",
    };

    private static int Offset(long recordingId, int count) =>
        count == 0 ? 0 : (int)(unchecked((ulong)recordingId * 2654435761UL) % (ulong)count);
}
