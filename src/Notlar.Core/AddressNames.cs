using System.Globalization;
using System.Text.RegularExpressions;

namespace Notlar.Core;

/// <summary>Bir kişiye konuşmadaki hitaplardan çıkarılan ad. <paramref name="Votes"/>: bu adın kişiye aldığı oy
/// (öz tanıtım 2 sayılır), <paramref name="TotalVotes"/>: kişinin aldığı tüm oylar.</summary>
public sealed record AddressedName(string Name, int Votes, int TotalVotes);

/// <summary>Konuşmacıların gerçek adını toplantıdaki hitaplardan çıkarır ("Ahmet, sen bakar mısın?",
/// "Teşekkürler Ayşe Hanım", "ne düşünüyorsun Mehmet?", "ben Ahmet"). Yanlış ad vermek ad vermemekten kötü
/// olduğundan çok temkinlidir; emin olunamayan kişiye ad verilmez (ses tonundan takma ad kalır).
/// <para>Ad tespiti: yalnızca kapalı bir Türkçe ad listesi (<see cref="KnownNames"/>); büyük harf sezgisi yok
/// (Whisper'ın büyük harfi güvenilmez, "Merkez Bankası" ad sanılmasın). Gündelik sözcük olarak da geçen adlar
/// (Umut, Deniz, Onur, Barış, Gül, Can…) listede yok. Ekli biçimler ("Ahmet'e", "Ahmet'in") hitap değil,
/// üçüncü kişiden bahistir; yalnızca çıplak ad (isteğe bağlı Bey/Hanım/Abi/Abla/Hocam ile) sayılır.</para>
/// <para>Hitap kalıpları (cümle = . ? ! ile biten parça): 1) cümle başında ad ve hemen ardından virgül ya da cümle
/// sonu ("Ahmet, …", "Ahmet Bey?"); başta "evet/peki/tamam/teşekkürler…" gibi bir ünlem olabilir. 2) cümle sonunda
/// ad, önünde virgül ya da teşekkür/selamlama sözcüğü varsa ya da cümle soru ise ("…, Ahmet.", "Teşekkürler Ayşe
/// Hanım.", "ne düşünüyorsun Mehmet?"). Ortada geçen ad ("Dün Ahmet aradı"), virgülsüz baştaki ad ("Ahmet dün
/// aradı") sayılmaz. Öz tanıtım: "ben Ahmet", "adım Ahmet", "Ahmet ben" (ad cümle/virgül sınırında).</para>
/// <para>Oylama: hitap içeren satırdan sonraki en çok <see cref="MaxReplyLines"/> satır içinde ve en çok
/// <see cref="MaxReplyGapMs"/> içinde konuşmaya başlayan İLK farklı kişi o ada 1 oy alır (hitap edilen genelde
/// hemen cevap verir). Bir satırda birden çok farklı ada hitap varsa (kime cevap verildiği belirsiz) oy yok. Öz
/// tanıtım konuşana 2 oy. Hitap eden kişi o ad değildir (karşı kanıt).</para>
/// <para>Karar (hepsi gerekli): en çok oy alan ad en az <see cref="MinVotes"/> oy almış (tek tesadüfi hitap yetmez)
/// VE kişinin oylarının en az <see cref="MinShare"/>'ini almış VE kişi bu ada kendisi hiç hitap etmemiş VE başka bir
/// kişi aynı ad için en az <see cref="MinVotes"/> oy almamış (aynı adlı iki kişi ya da kümeleme hatası: ikisine de
/// ad verilmez).</para>
/// <para>Söz devri: konuşan sözü bir sonrakine adıyla verir ("topu Ahmet'e atayım", "sözü Ayşe Hanım'a vereyim",
/// "ben Mehmet'e bırakıyorum", "şimdi Burak'a geçelim", "Zeynep sen devam et", "Ahmet Bey buyurun", "sıra sende
/// Elif"). Devirden sonra en çok <see cref="MaxReplyLines"/> satır ve <see cref="MaxHandoverGapMs"/> içinde konuşan
/// İLK farklı kişi o ada <see cref="HandoverWeight"/> oy alır (öz tanıtımla aynı ağırlık); devir içeren satırdaki
/// hitaplar ayrıca oylanmaz. Satırda birden çok farklı ada devir varsa oy yok. Yalnızca kapalı kalıplar: yönelme
/// ekli ad ('e/'a/'ye/'ya, kesmeli ya da kesmesiz; Bey/Hanım'a dahil) + devir fiilinin istek / şimdiki / gelecek
/// zaman çekimi (geçmiş "attım" ve geniş zaman "atarım" yok); at-/ver-/aktar- ayrıca "sözü/topu/mikrofonu/sırayı/
/// pası" nesnesini ister; virgüller arasındaki cümlecikte bunlar dışında yalnızca dolgu sözcükleri olabilir
/// ("dosyayı Ahmet'e veriyorum" devir değildir). Tek devir 2 oy olduğundan tek başına ad verebilir, ancak
/// karar kuralları (pay, kendine seslenme, çakışma) aynen geçerlidir: kişiye başka bir ada tek bir oy bile
/// gelmişse 2/3 &lt; %70 olur ve ad verilmez.</para></summary>
public static partial class AddressNames
{
    /// <summary>Hitaptan sonra cevabın aranacağı en fazla satır.</summary>
    public const int MaxReplyLines = 2;

    /// <summary>Hitap satırının bitişiyle cevabın başlangıcı arası en fazla süre.</summary>
    public const long MaxReplyGapMs = 10_000;

    /// <summary>Söz devri satırının bitişiyle devralanın konuşmaya başlaması arası en fazla süre (devralan
    /// mikrofonunu açarken biraz gecikebilir).</summary>
    public const long MaxHandoverGapMs = 15_000;

    /// <summary>Öz tanıtımın konuşana verdiği oy.</summary>
    public const int SelfIntroductionWeight = 2;

    /// <summary>Söz devrinin devralana verdiği oy: öz tanıtım kadar güçlü (konuşan sözü açıkça o ada veriyor).</summary>
    public const int HandoverWeight = 2;

    /// <summary>Ad atamak için gereken en az oy: tek hitap tesadüf olabilir (araya giren biri, yanlış kümelenmiş satır).</summary>
    public const int MinVotes = 2;

    /// <summary>Kazanan adın kişinin oyları içindeki en az payı: 0,7, iki oyda oybirliği, 3–4 oyda en çok bir aykırı,
    /// 10 oyda en çok üç aykırı oy demek; kişiye iki farklı ad sık sık hitap ediliyorsa (kümeleme iki kişiyi
    /// birleştirmiş olabilir) karar verilmez.</summary>
    public const double MinShare = 0.7;

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Hitapta adı izleyebilen unvanlar; yalnızca Bey/Hanım görünen ada eklenir.</summary>
    private static readonly HashSet<string> Titles = ["bey", "hanım", "hanim", "abi", "abla", "hocam", "hoca"];

    private static readonly HashSet<string> KeptTitles = ["bey", "hanım", "hanim"];

    /// <summary>Cümle başında adın önünde gelebilen ünlemler ("Evet Ahmet, …").</summary>
    private static readonly HashSet<string> Interjections =
    [
        "evet", "hayır", "peki", "tamam", "şimdi", "bak", "merhaba", "selam", "günaydın", "teşekkürler",
        "teşekkür", "sağol", "sağ", "eyvallah", "buyrun", "buyur", "hah", "ha", "e", "ee", "yani", "hadi", "haydi",
    ];

    /// <summary>Cümle sonundaki adın önünde gelince hitap sayılan sözcükler ("Teşekkürler Ayşe").</summary>
    private static readonly HashSet<string> Greetings =
    [
        "teşekkürler", "teşekkür", "ederim", "ederiz", "sağol", "sağolun", "ol", "olun", "merhaba", "selam",
        "günaydın", "eyvallah", "buyrun", "buyur", "hoşgeldin", "hoşgeldiniz", "geldin", "geldiniz",
    ];

    // ---------------------------------------------------------------- Söz devri sözlüğü (kapalı liste)

    /// <summary>Devir fiillerinin nesneleri: at-/ver-/aktar- yalnızca bunlardan biriyle devir sayılır.</summary>
    private static readonly HashSet<string> HandoverObjects = ["sözü", "topu", "mikrofonu", "sırayı", "pası"];

    /// <summary>Nesne isteyen devir fiilleri ("topu Ahmet'e atayım", "sözü Ayşe'ye veriyorum").</summary>
    private static readonly HashSet<string> ObjectHandoverVerbs =
    [
        .. VerbForms("at", 'ı', back: true),
        .. VerbForms("ver", 'i', back: false),
        .. VerbForms("aktar", 'ı', back: true),
    ];

    /// <summary>Nesnesiz de devir sayılan fiiller ("Mehmet'e bırakıyorum", "Burak'a geçelim").</summary>
    private static readonly HashSet<string> FreeHandoverVerbs =
    [
        .. VerbForms("bırak", 'ı', back: true),
        .. VerbForms("devred", 'i', back: false),
        .. VerbForms("geç", 'i', back: false),
    ];

    /// <summary>Devir cümleciğinde ad, nesne ve fiil dışında bulunabilecek sözcükler.</summary>
    private static readonly HashSet<string> HandoverFillers =
    [
        "ben", "biz", "şimdi", "artık", "o", "zaman", "de", "da", "evet", "tamam", "peki", "hadi", "haydi", "ve",
        "buradan", "burada", "hemen", "izninizle", "müsaadenizle", "isterseniz", "dilerseniz", "e", "ee",
    ];

    /// <summary>Adla birlikte söylenince söz devri olan kalıplar ("Zeynep sen devam et", "sıra sende Elif").</summary>
    private static readonly string[][] HandoverPhrases =
    [
        ["buyur"], ["buyurun"], ["buyrun"],
        ["sıra", "sende"], ["sıra", "sizde"], ["söz", "sende"], ["söz", "sizde"],
        ["sen", "devam", "et"], ["siz", "devam", "edin"], ["devam", "et"], ["devam", "edin"],
    ];

    /// <summary>Devir kalıbının önünde gelebilen sözcükler ("Evet Zeynep, sen devam et").</summary>
    private static readonly HashSet<string> HandoverLeadFillers =
    [
        "evet", "tamam", "peki", "şimdi", "o", "zaman", "artık", "hadi", "haydi", "e", "ee", "teşekkürler",
        "sağol", "sağolun", "eyvallah",
    ];

    /// <summary>Fiilin devirde kullanılan çekimleri: istek (atayım, atalım), şimdiki zaman (atıyorum, atıyoruz),
    /// gelecek zaman (atacağım, atacağız). <paramref name="stem"/>: ünlüyle başlayan ekten önceki kök
    /// (devret → devred), <paramref name="yorVowel"/>: -yor'dan önceki dar ünlü.</summary>
    private static IEnumerable<string> VerbForms(string stem, char yorVowel, bool back)
    {
        string[] suffixes = back
            ? ["ayım", "alım", "acağım", "acağız"]
            : ["eyim", "elim", "eceğim", "eceğiz"];
        foreach (var suffix in suffixes) yield return stem + suffix;
        yield return stem + yorVowel + "yorum";
        yield return stem + yorVowel + "yoruz";
    }

    public static readonly IReadOnlyList<string> MaleNames =
    [
        "Ahmet", "Mehmet", "Mustafa", "Ali", "Hasan", "Hüseyin", "İbrahim", "İsmail", "Osman", "Yusuf", "Ömer",
        "Murat", "Emre", "Burak", "Kemal", "Serkan", "Hakan", "Tolga", "Kerem", "Selim", "Okan", "Halil",
        "Süleyman", "Ramazan", "Recep", "Abdullah", "Fatih", "Kadir", "Yasin", "Enes", "Furkan", "Oğuz",
        "Oğuzhan", "Orhan", "Erkan", "Erhan", "Volkan", "Gökhan", "Serdar", "Sinan", "Cem", "Cengiz", "Taner",
        "Tamer", "Tarık", "Levent", "Bülent", "Mert", "Arda", "Berk", "Batuhan", "Berkay", "Alper", "Anıl",
        "Baran", "Bora", "Cihan", "Çağlar", "Çağatay", "Doğukan", "Ercan", "Erdal", "Ertan", "Ertuğrul", "Eren",
        "Faruk", "Ferhat", "Fikret", "Gökay", "Gürkan", "Harun", "Hamza", "İlker", "İlhan", "Kaan", "Koray",
        "Kubilay", "Kürşat", "Mahmut", "Metin", "Muhammed", "Nihat", "Nuri", "Oktay", "Orçun", "Ozan", "Rıza",
        "Sadık", "Sami", "Selçuk", "Semih", "Sercan", "Serhat", "Sezer", "Sabri", "Şahin", "Şükrü", "Tahsin",
        "Talha", "Tayfun", "Timur", "Tuncay", "Tuğrul", "Uğur", "Utku", "Vedat", "Yakup", "Yavuz", "Yiğit",
        "Yunus", "Zeki", "Ziya", "Necati", "Necdet", "Cevdet", "Celal", "Cemal", "Kenan", "Kamil", "Kazım",
        "Hilmi", "Hikmet", "Haluk", "Hayri", "Erol", "Erdoğan", "Ersin", "Ekrem", "Emrah", "Fuat", "Gürsel",
        "Turgay", "Mesut", "Melih", "Rauf", "Rüştü", "Sedat", "Selahattin", "Tufan",
    ];

    public static readonly IReadOnlyList<string> FemaleNames =
    [
        "Ayşe", "Fatma", "Zeynep", "Elif", "Emine", "Hatice", "Merve", "Esra", "Selin", "Büşra", "Gamze", "Seda",
        "Burcu", "Sibel", "Pınar", "Özlem", "Ece", "Nazlı", "Leyla", "Hülya", "Zehra", "Meryem", "Hacer",
        "Hanife", "Havva", "Rabia", "Kübra", "Tuğba", "Esma", "Sümeyye", "Betül", "Şeyma", "Hande", "Ebru",
        "Elçin", "Didem", "Eda", "Elvan", "Esin", "Feride", "Filiz", "Funda", "Gonca", "Gökçe", "Gülay",
        "Gülşen", "Gülsüm", "İrem", "Işıl", "Kevser", "Melis", "Melike", "Neslihan", "Nesrin", "Nilay",
        "Nilüfer", "Nurcan", "Nuray", "Özge", "Pelin", "Reyhan", "Selda", "Selma", "Semra", "Sevil", "Sevim",
        "Sinem", "Songül", "Şebnem", "Şule", "Tülay", "Tuba", "Yasemin", "Yeliz", "Zerrin", "Zuhal", "Aslı",
        "Aylin", "Aysel", "Ayten", "Asuman", "Begüm", "Belgin", "Berna", "Beyza", "Buse", "Canan", "Ceren",
        "Cansu", "Cemile", "Defne", "Dilara", "Emel", "Fadime", "Fulya", "Gülten", "Hümeyra", "Leman",
        "Müge", "Nihan", "Nermin", "Perihan", "Sevgül", "Şeyda", "Tuğçe", "Zübeyde", "Necla", "Nalan", "Nihal",
        "Aynur", "Gülcan", "Ayşegül", "Gülsen", "Serpil", "Tülin",
    ];

    /// <summary>Her iki cinsiyette yaygın ve gündelik sözcük olarak pek geçmeyen adlar.</summary>
    public static readonly IReadOnlyList<string> NeutralNames = ["Derya", "İlkay", "Aytaç", "Evren", "Tuna"];


    private static readonly Dictionary<string, (string Name, VoiceGender Gender)> Lookup = BuildLookup();

    /// <summary>Hitapta tanınan tüm adlar (görünen biçimleriyle).</summary>
    public static IEnumerable<string> KnownNames => Lookup.Values.Select(v => v.Name);

    /// <summary>Adın listedeki cinsiyeti (Bey/Hanım unvanı da sayılır); bilinmiyorsa Belirsiz.</summary>
    public static VoiceGender GenderOf(string name)
    {
        var words = Words(name);
        if (words.Count == 0) return VoiceGender.Belirsiz;
        if (words.Count > 1 && words[^1] == "bey") return VoiceGender.Erkek;
        if (words.Count > 1 && words[^1] is "hanım" or "hanim") return VoiceGender.Kadin;
        return Lookup.TryGetValue(words[0], out var entry) ? entry.Gender : VoiceGender.Belirsiz;
    }

    /// <summary>Kişi numarası → hitaplardan emin olunarak çıkarılan ad. Emin olunamayan kişi sonuçta yer almaz.</summary>
    public static IReadOnlyDictionary<int, AddressedName> Detect(IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var votes = new Dictionary<int, Dictionary<string, Tally>>();
        var addressedBy = new Dictionary<int, HashSet<string>>();

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var found = Find(s.Text);
            var handovers = FindHandoverMentions(s.Text).DistinctBy(n => n.Key).ToList();

            if (s.Person is { } speaker)
            {
                foreach (var self in found.SelfNames.DistinctBy(n => n.Key))
                    Add(speaker, self, SelfIntroductionWeight);
                // Sözü bir ada veren de o ad değildir (hitapla aynı karşı kanıt).
                foreach (var addressed in found.Addressed.Concat(handovers))
                    (addressedBy.TryGetValue(speaker, out var set) ? set : addressedBy[speaker] = []).Add(addressed.Key);
            }

            if (handovers.Count > 0)
            {
                // Devir satırında yalnızca devir oylanır; birden çok ada devir belirsizdir, oy yok.
                if (handovers.Count == 1 && Reply(segments, i, MaxHandoverGapMs) is { } taker)
                    Add(taker, handovers[0], HandoverWeight);
                continue;
            }

            var distinct = found.Addressed.DistinctBy(n => n.Key).ToList();
            if (distinct.Count != 1) continue; // hitap yok ya da kime cevap verildiği belirsiz
            if (Reply(segments, i, MaxReplyGapMs) is { } replier)
                Add(replier, distinct[0], 1);
        }

        var result = new Dictionary<int, AddressedName>();
        foreach (var (person, tallies) in votes)
        {
            var total = tallies.Values.Sum(t => t.Votes);
            var (key, top) = tallies.OrderByDescending(t => t.Value.Votes).ThenBy(t => t.Key, StringComparer.Ordinal).First();
            if (top.Votes < MinVotes || top.Votes < MinShare * total) continue;
            if (addressedBy.TryGetValue(person, out var own) && own.Contains(key)) continue;
            if (votes.Any(v => v.Key != person && v.Value.TryGetValue(key, out var other) && other.Votes >= MinVotes)) continue;

            var name = Lookup[key].Name;
            // Bey/Hanım çoğu hitapta geçiyorsa görünen ada eklenir.
            if (top.Titles.Count > 0)
            {
                var (title, count) = top.Titles.OrderByDescending(t => t.Value).First();
                if (count * 2 > top.Votes) name += " " + (title == "bey" ? "Bey" : "Hanım");
            }
            result[person] = new AddressedName(name, top.Votes, total);
        }
        return result;

        void Add(int person, Mention mention, int weight)
        {
            var tallies = votes.TryGetValue(person, out var t) ? t : votes[person] = [];
            var tally = tallies.TryGetValue(mention.Key, out var existing) ? existing : tallies[mention.Key] = new Tally();
            tally.Votes += weight;
            if (mention.Title is { } title) tally.Titles[title] = tally.Titles.GetValueOrDefault(title) + weight;
        }
    }

    /// <summary>Satırdaki hitaplar ve öz tanıtımlar (test ve teşhis için açık).</summary>
    public static (IReadOnlyList<string> Addressed, IReadOnlyList<string> SelfNames) FindNames(string text)
    {
        var found = Find(text);
        return (found.Addressed.Select(m => Lookup[m.Key].Name).ToList(), found.SelfNames.Select(m => Lookup[m.Key].Name).ToList());
    }

    /// <summary>Satırdaki söz devirleri: sözün verildiği adlar (test ve teşhis için açık).</summary>
    public static IReadOnlyList<string> FindHandovers(string text) =>
        FindHandoverMentions(text).Select(m => Lookup[m.Key].Name).ToList();

    /// <summary>Hitap edilen (ya da sözün verildiği) satırdan sonra en çok <see cref="MaxReplyLines"/> satır ve
    /// <paramref name="maxGapMs"/> içinde konuşan ilk farklı kişi.</summary>
    private static int? Reply(IReadOnlyList<TranscriptSegment> segments, int index, long maxGapMs)
    {
        var source = segments[index];
        for (var j = index + 1; j < segments.Count && j <= index + MaxReplyLines; j++)
        {
            var s = segments[j];
            if (s.StartMs - source.EndMs > maxGapMs) return null;
            if (s.Person is not { } person) continue;
            if (person != source.Person) return person;
        }
        return null;
    }

    private sealed record Mention(string Key, string? Title);

    private sealed class Tally
    {
        public int Votes;
        public Dictionary<string, int> Titles { get; } = [];
    }

    private static (List<Mention> Addressed, List<Mention> SelfNames) Find(string? text)
    {
        var addressed = new List<Mention>();
        var selfNames = new List<Mention>();
        if (string.IsNullOrWhiteSpace(text)) return (addressed, selfNames);

        foreach (Match sentence in SentenceRegex().Matches(TextSafety.Flatten(text)))
        {
            var question = sentence.Groups["end"].Value.Contains('?');
            // Sözcükler ve virgüller (virgül "," olarak).
            var tokens = TokenRegex().Matches(sentence.Groups["body"].Value)
                .Select(m => m.Value == "," ? "," : m.Value.ToLower(Tr)).ToList();
            if (tokens.Count == 0) continue;

            for (var k = 0; k < tokens.Count; k++)
            {
                if (!IsName(tokens[k])) continue;
                var title = k + 1 < tokens.Count && Titles.Contains(tokens[k + 1]) ? tokens[k + 1] : null;
                var after = k + (title is null ? 1 : 2);
                var atEnd = after >= tokens.Count;
                var boundaryAfter = atEnd || tokens[after] == ",";
                var mention = new Mention(tokens[k], title is not null && KeptTitles.Contains(title) ? title : null);

                var before = k - 1 >= 0 ? tokens[k - 1] : null;
                var before2 = k - 2 >= 0 ? tokens[k - 2] : null;

                // Öz tanıtım: "ben Ahmet", "adım Ahmet", "Ahmet ben".
                if (boundaryAfter && before is "ben" or "adım")
                {
                    selfNames.Add(mention);
                    continue;
                }
                if (before is null && title is null && k + 1 < tokens.Count && tokens[k + 1] == "ben"
                    && (k + 2 >= tokens.Count || tokens[k + 2] == ","))
                {
                    selfNames.Add(mention);
                    continue;
                }

                // Cümle başında (isteğe bağlı ünlem ve virgülden sonra) ve ardından virgül ya da cümle sonu.
                // "Ahmet, Ayşe, …" gibi ad dizisindeki sonraki adlar da hitaptır (satır birden çok kişiye seslenir).
                var start = before is null || (before == "," && before2 is not null && Interjections.Contains(before2) && k == 2)
                            || (Interjections.Contains(before) && k == 1)
                            || (before == "," && before2 is not null && (IsName(before2) || Titles.Contains(before2)));
                if (start && boundaryAfter)
                {
                    addressed.Add(mention);
                    continue;
                }

                // Cümle sonunda: önünde virgül ya da teşekkür/selamlama, ya da cümle soru.
                if (atEnd && before is not null && (before == "," || Greetings.Contains(before) || question))
                    addressed.Add(mention);
            }
        }
        return (addressed, selfNames);
    }

    /// <summary>Söz devirleri. İki kalıp: 1) virgüller arasındaki cümlecik = [dolgu/nesne]* yönelme ekli ad
    /// [dolgu/nesne]* devir fiili ("topu Ahmet'e atayım", "ben Mehmet Bey'e bırakıyorum"); 2) cümle = [baş
    /// dolgusu]* ad [unvan] kalıp ya da kalıp ad [unvan] ("Zeynep, sen devam et", "sıra sende Elif", "Ahmet Bey
    /// buyurun"), sonda isteğe bağlı "lütfen".</summary>
    private static List<Mention> FindHandoverMentions(string? text)
    {
        var result = new List<Mention>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (Match sentence in SentenceRegex().Matches(TextSafety.Flatten(text)))
        {
            var tokens = TokenRegex().Matches(sentence.Groups["body"].Value)
                .Select(m => m.Value == "," ? "," : m.Value.ToLower(Tr).Replace('’', '\'')).ToList();
            if (tokens.Count == 0) continue;

            // 1) Yönelme ekli ad + devir fiili, cümlecik cümlecik.
            var clause = new List<string>();
            foreach (var token in tokens.Append(","))
            {
                if (token != ",")
                {
                    clause.Add(token);
                    continue;
                }
                if (DativeHandover(clause) is { } mention) result.Add(mention);
                clause.Clear();
            }

            // 2) Adla söylenen devir kalıbı, tüm cümle (virgüller önemsiz).
            if (PhraseHandover(tokens.Where(t => t != ",").ToList()) is { } phrase) result.Add(phrase);
        }
        return result;
    }

    private static Mention? DativeHandover(List<string> clause)
    {
        if (clause.Count < 2) return null;
        var verb = clause[^1];
        var needsObject = ObjectHandoverVerbs.Contains(verb);
        if (!needsObject && !FreeHandoverVerbs.Contains(verb)) return null;

        Mention? dative = null;
        var hasObject = false;
        for (var k = 0; k < clause.Count - 1; k++)
        {
            var token = clause[k];
            if (IsName(token) && k + 1 < clause.Count - 1 && DativeStem(clause[k + 1]) is { } titleStem
                && Titles.Contains(titleStem))
            {
                // "Mehmet Bey'e", "Ayşe Hanıma", "Ahmet abiye"
                if (dative is not null) return null; // iki ad: kime devredildiği belirsiz
                dative = new Mention(token, KeptTitles.Contains(titleStem) ? titleStem : null);
                k++;
            }
            else if (!IsName(token) && DativeStem(token) is { } stem && IsName(stem))
            {
                if (dative is not null) return null;
                dative = new Mention(stem, null);
            }
            else if (HandoverObjects.Contains(token))
            {
                hasObject = true;
            }
            else if (!HandoverFillers.Contains(token))
            {
                return null; // başka sözcük: "dosyayı Ahmet'e veriyorum", "Ahmet'e mail atıyorum"
            }
        }
        return dative is not null && (hasObject || !needsObject) ? dative : null;
    }

    private static Mention? PhraseHandover(List<string> words)
    {
        if (words.Count > 0 && words[^1] == "lütfen") words.RemoveAt(words.Count - 1);
        for (var p = 0; p < words.Count; p++)
        {
            // Ad [unvan] kalıp
            if (IsName(words[p]))
            {
                var hasTitle = p + 1 < words.Count && Titles.Contains(words[p + 1]);
                var rest = words.Skip(p + (hasTitle ? 2 : 1)).ToList();
                if (HandoverPhrases.Any(phrase => phrase.SequenceEqual(rest)))
                    return new Mention(words[p], hasTitle && KeptTitles.Contains(words[p + 1]) ? words[p + 1] : null);
            }

            // Kalıp ad [unvan]
            foreach (var phrase in HandoverPhrases)
            {
                var at = p + phrase.Length;
                if (at >= words.Count || !words.Skip(p).Take(phrase.Length).SequenceEqual(phrase) || !IsName(words[at]))
                    continue;
                var tail = words.Count - at - 1;
                var title = tail == 1 && Titles.Contains(words[at + 1]) ? words[at + 1] : null;
                if (tail == 0 || title is not null)
                    return new Mention(words[at], title is not null && KeptTitles.Contains(title) ? title : null);
            }

            if (!HandoverLeadFillers.Contains(words[p])) break;
        }
        return null;
    }

    /// <summary>Yönelme hali ekli sözcüğün kökü ("ahmet'e", "ahmete" → "ahmet"; "ayşe'ye", "ayşeye" → "ayşe";
    /// "hanıma" → "hanım"); ek ünlü uyumuna uymuyorsa ya da kaynaştırma yanlışsa null. Kökün ad olup olmadığına
    /// çağıran bakar.</summary>
    private static string? DativeStem(string token)
    {
        var apostrophe = token.IndexOf('\'');
        if (apostrophe > 0)
        {
            var stem = token[..apostrophe];
            return IsDativeSuffix(stem, token[(apostrophe + 1)..]) ? stem : null;
        }
        foreach (var suffix in (ReadOnlySpan<string>)["ye", "ya", "e", "a"])
        {
            if (token.Length <= suffix.Length + 1 || !token.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var stem = token[..^suffix.Length];
            if (IsDativeSuffix(stem, suffix)) return stem;
        }
        return null;
    }

    private const string FrontVowels = "eiöü";
    private const string Vowels = "aıoueiöü";

    /// <summary>-ye/-ya ünlüyle biten köke, -e/-a ünsüzle biten köke gelir; e/a son ünlüye uyar.</summary>
    private static bool IsDativeSuffix(string stem, string suffix)
    {
        if (suffix is not ("e" or "a" or "ye" or "ya") || stem.Length == 0) return false;
        var endsWithVowel = Vowels.Contains(stem[^1]);
        if (endsWithVowel != (suffix.Length == 2)) return false;
        var lastVowel = stem.LastOrDefault(c => Vowels.Contains(c));
        if (lastVowel == default) return false;
        return FrontVowels.Contains(lastVowel) == (suffix[^1] == 'e');
    }

    private static bool IsName(string token) => Lookup.ContainsKey(token);

    private static List<string> Words(string text) =>
        TokenRegex().Matches(text).Where(m => m.Value != ",").Select(m => m.Value.ToLower(Tr)).ToList();

    private static Dictionary<string, (string, VoiceGender)> BuildLookup()
    {
        var lookup = new Dictionary<string, (string, VoiceGender)>(StringComparer.Ordinal);
        foreach (var (list, gender) in new[] { (MaleNames, VoiceGender.Erkek), (FemaleNames, VoiceGender.Kadin), (NeutralNames, VoiceGender.Belirsiz) })
            foreach (var name in list)
                lookup.TryAdd(name.ToLower(Tr), (name, gender));
        return lookup;
    }

    [GeneratedRegex(@"(?<body>[^.?!…]+)(?<end>[.?!…]*)")]
    private static partial Regex SentenceRegex();

    /// <summary>Sözcük (harf ve kesme işareti; "Ahmet'e" tek sözcük kalır, ad sayılmaz) ya da virgül.</summary>
    [GeneratedRegex(@"[\p{L}][\p{L}'’]*|,")]
    private static partial Regex TokenRegex();
}
