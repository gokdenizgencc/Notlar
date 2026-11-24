namespace Notlar.Core;

/// <summary>Bir transkript satırının ses parmak izi. <paramref name="Embedding"/>: satırın ses kesitinden çıkarılan
/// konuşmacı gömmesi (herhangi bir boyut/ölçek; kosinüs benzerliğiyle karşılaştırılır); çıkarılamadıysa null.</summary>
public sealed record VoiceSample(long StartMs, long EndMs, Speaker Channel, float[]? Embedding);

/// <summary><see cref="SpeakerClustering.Assign"/> ayarları. Varsayılanlar 3D-Speaker CAM++ gömmeleri içindir
/// (başka modelin benzerlik dağılımı farklıdır; eşikler yeniden ölçülmeli).</summary>
public sealed record SpeakerClusteringOptions
{
    /// <summary>İki küme, ortalama kosinüs benzerliği en az bu kadarsa aynı kişi sayılıp birleştirilir.
    /// 0,5: gerçek bir Türkçe toplantı kaydında (28 dk, 113 satır) bulunan kişi sayısı 0,45–0,60 arasında
    /// değişmiyor (bu aralığın ortası); 0,65'ten sonra aynı kişi parçalanmaya, 0,40'ın altında kümeler
    /// birleşmeye başlıyor. Aynı kayıtta kesin farklı kişilerin (mikrofon × sistem sesi) benzerliği 0,4'ü hiç
    /// geçmedi; aynı satırın iki yarısının benzerliğinin ortancası 0,77.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>Kısa (güvenilmez) bir satır, en yakın kişinin merkezine en az bu kadar benziyorsa o kişiye atanır;
    /// daha az benziyorsa sesine değil zamanca en yakın komşu satıra bakılır. 0,4: ölçülen kayıtta farklı kişiler
    /// arasında görülen en yüksek benzerliğin hemen üstü.</summary>
    public double LooseThreshold { get; init; } = 0.4;

    /// <summary>Bundan kısa satırların gömmesi güvenilmez sayılır: kümelemeye katılmaz, sonradan atanır.</summary>
    public long MinReliableMs { get; init; } = 1500;

    /// <summary>Toplam konuşması bundan kısa olan küme ayrı kişi sayılmaz (üst üste konuşma, gürültü, bozuk kesit
    /// gibi aykırı satırlar sahte "kişi" üretmesin); satırları kısa satır gibi yeniden atanır.</summary>
    public long MinPersonMs { get; init; } = 4000;

    public static SpeakerClusteringOptions Default { get; } = new();
}

/// <summary>Transkript satırlarını ses gömmelerine göre kişilere ayırır ("aynı ses = aynı kişi").
/// 1) Yeterince uzun satırlar ortalama bağlantılı (average linkage) birleştirici kümelemeyle, en benzer iki küme
/// eşiğin altına düşene dek birleştirilir. 2) Çok az konuşması olan kümeler dağıtılır. 3) Kısa/gömmesiz/dağıtılan
/// satırlar en yakın kişi merkezine, o da tutmazsa zamanca en yakın (tercihen aynı kanaldaki) etiketli satırın
/// kişisine atanır. 4) Kişiler ilk konuşma sırasına göre 1'den numaralanır.</summary>
public static class SpeakerClustering
{
    /// <summary>Her örnek için kişi numarası (girişle aynı sırada). Hiçbir satırdan gömme çıkmadıysa tümü null.
    /// Örnekler zaman sırasında verilmelidir (numaralandırma giriş sırasına göredir).</summary>
    public static IReadOnlyList<int?> Assign(IReadOnlyList<VoiceSample> samples, SpeakerClusteringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        options ??= SpeakerClusteringOptions.Default;

        var n = samples.Count;
        var vectors = new float[]?[n];
        for (var i = 0; i < n; i++)
            vectors[i] = Normalize(samples[i].Embedding);

        // Gömmeler farklı boyuttaysa (olmamalı) çoğunluğun boyutu dışındakiler gömmesiz sayılır.
        var dim = vectors.Where(v => v is not null).GroupBy(v => v!.Length)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        for (var i = 0; i < n; i++)
            if (vectors[i] is { } v && v.Length != dim) vectors[i] = null;

        var core = Enumerable.Range(0, n)
            .Where(i => vectors[i] is not null && Duration(samples[i]) >= options.MinReliableMs).ToList();
        // Hiç uzun satır yoksa (çok kısa kayıt) eldeki gömmelerle yetinilir.
        if (core.Count == 0)
            core = Enumerable.Range(0, n).Where(i => vectors[i] is not null).ToList();
        if (core.Count == 0)
            return new int?[n];

        var cluster = new int[n];
        Array.Fill(cluster, -1);
        var coreLabels = Agglomerate(core.Select(i => vectors[i]!).ToList(), options.Threshold);
        for (var k = 0; k < core.Count; k++)
            cluster[core[k]] = coreLabels[k];

        // Az konuşmalı kümeleri dağıt (en çok konuşan küme her zaman kalır).
        var totals = new Dictionary<int, long>();
        foreach (var i in core)
            totals[cluster[i]] = totals.GetValueOrDefault(cluster[i]) + Duration(samples[i]);
        var biggest = totals.OrderByDescending(t => t.Value).ThenBy(t => t.Key).First().Key;
        foreach (var i in core)
            if (cluster[i] != biggest && totals[cluster[i]] < options.MinPersonMs) cluster[i] = -1;

        // Kişi merkezleri (birim gömmelerin ortalaması).
        var centroids = new Dictionary<int, float[]>();
        foreach (var i in core)
        {
            if (cluster[i] < 0) continue;
            if (!centroids.TryGetValue(cluster[i], out var sum))
                centroids[cluster[i]] = sum = new float[dim];
            var v = vectors[i]!;
            for (var d = 0; d < dim; d++) sum[d] += v[d];
        }
        foreach (var key in centroids.Keys.ToList())
            centroids[key] = Normalize(centroids[key]) ?? centroids[key];

        // Kalan satırlar: önce sese göre en yakın merkez…
        var anchored = cluster.Select(c => c >= 0).ToArray(); // komşu aramasında yalnızca sesle etiketlenenler kullanılır
        for (var i = 0; i < n; i++)
        {
            if (cluster[i] >= 0 || vectors[i] is not { } v) continue;
            var best = -1;
            var bestSim = double.NegativeInfinity;
            foreach (var (key, centroid) in centroids.OrderBy(c => c.Key))
            {
                var sim = Dot(v, centroid);
                if (sim > bestSim) (best, bestSim) = (key, sim);
            }
            if (bestSim >= options.LooseThreshold)
            {
                cluster[i] = best;
                anchored[i] = true;
            }
        }

        // …sonra zamanca en yakın etiketli satır (önce aynı kanalda, yoksa herhangi bir kanalda).
        for (var i = 0; i < n; i++)
        {
            if (cluster[i] >= 0) continue;
            var neighbor = Nearest(samples, anchored, i, sameChannel: true);
            if (neighbor < 0) neighbor = Nearest(samples, anchored, i, sameChannel: false);
            cluster[i] = cluster[neighbor]; // en büyük küme hep kaldığından en az bir etiketli satır vardır
        }

        // İlk konuşma sırasına göre numarala.
        var numbers = new Dictionary<int, int>();
        var result = new int?[n];
        for (var i = 0; i < n; i++)
        {
            if (!numbers.TryGetValue(cluster[i], out var number))
                numbers[cluster[i]] = number = numbers.Count + 1;
            result[i] = number;
        }
        return result;
    }

    /// <summary>Her kişinin ses parmak izi: o kişiye atanmış satırların birim gömmelerinin birim ortalaması.
    /// Gömmesi olmayan kişi sonuçta yer almaz. <paramref name="embeddings"/> ve <paramref name="persons"/> aynı
    /// uzunlukta olmalı.</summary>
    public static IReadOnlyDictionary<int, float[]> Centroids(IReadOnlyList<float[]?> embeddings, IReadOnlyList<int?> persons)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(persons);
        if (embeddings.Count != persons.Count)
            throw new ArgumentException("Gömme ve kişi sayıları farklı.");

        var dim = embeddings.Where(v => v is { Length: > 0 }).GroupBy(v => v!.Length)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var sums = new Dictionary<int, float[]>();
        for (var i = 0; i < embeddings.Count; i++)
        {
            if (persons[i] is not { } person || Normalize(embeddings[i]) is not { } v || v.Length != dim) continue;
            if (!sums.TryGetValue(person, out var sum)) sums[person] = sum = new float[dim];
            for (var d = 0; d < dim; d++) sum[d] += v[d];
        }
        var result = new Dictionary<int, float[]>();
        foreach (var (person, sum) in sums)
            if (Normalize(sum) is { } unit) result[person] = unit;
        return result;
    }

    /// <summary>İki gömmenin kosinüs benzerliği (−1…1); biri boş/sıfır/bozuksa ya da boyutlar farklıysa 0.</summary>
    public static double Cosine(float[]? a, float[]? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        return x is null || y is null || x.Length != y.Length ? 0 : Dot(x, y);
    }

    /// <summary>Ortalama bağlantılı birleştirici kümeleme; her vektör için küme kimliği döndürür (kimlikler ardışık
    /// olmak zorunda değildir). Vektörler birim uzunlukta olmalıdır.</summary>
    private static int[] Agglomerate(IReadOnlyList<float[]> vectors, double threshold)
    {
        var n = vectors.Count;
        var labels = Enumerable.Range(0, n).ToArray();
        if (n < 2) return labels;

        var sim = new float[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
                sim[i, j] = sim[j, i] = (float)Dot(vectors[i], vectors[j]);
        }

        var active = new bool[n];
        Array.Fill(active, true);
        var size = new int[n];
        Array.Fill(size, 1);
        // Her etkin kümenin en benzer komşusu; her adımda tüm matrisi taramamak için tutulur.
        var bestOf = new int[n];
        for (var i = 0; i < n; i++) bestOf[i] = FindBest(i);

        while (true)
        {
            var a = -1;
            for (var i = 0; i < n; i++)
            {
                if (!active[i] || bestOf[i] < 0) continue;
                if (a < 0 || sim[i, bestOf[i]] > sim[a, bestOf[a]]) a = i;
            }
            if (a < 0 || sim[a, bestOf[a]] < threshold) break;

            var b = bestOf[a];
            if (b < a) (a, b) = (b, a); // küçük indisli küme kalır
            for (var k = 0; k < n; k++)
            {
                if (!active[k] || k == a || k == b) continue;
                sim[a, k] = sim[k, a] = (size[a] * sim[a, k] + size[b] * sim[b, k]) / (size[a] + size[b]);
            }
            size[a] += size[b];
            active[b] = false;
            for (var k = 0; k < n; k++)
                if (labels[k] == b) labels[k] = a;

            bestOf[a] = FindBest(a);
            for (var k = 0; k < n; k++)
            {
                if (!active[k] || k == a) continue;
                if (bestOf[k] == a || bestOf[k] == b) bestOf[k] = FindBest(k);
                else if (bestOf[k] < 0 || sim[k, a] > sim[k, bestOf[k]]) bestOf[k] = a;
            }
        }
        return labels;

        int FindBest(int i)
        {
            var best = -1;
            for (var j = 0; j < n; j++)
            {
                if (j == i || !active[j]) continue;
                if (best < 0 || sim[i, j] > sim[i, best]) best = j;
            }
            return best;
        }
    }

    /// <summary>Zamanca en yakın etiketli satırın indisi (aralıklar çakışıyorsa uzaklık 0; eşitlikte önceki satır);
    /// yoksa −1.</summary>
    private static int Nearest(IReadOnlyList<VoiceSample> samples, bool[] anchored, int index, bool sameChannel)
    {
        var self = samples[index];
        var best = -1;
        var bestGap = long.MaxValue;
        for (var j = 0; j < samples.Count; j++)
        {
            if (j == index || !anchored[j]) continue;
            var other = samples[j];
            if (sameChannel && other.Channel != self.Channel) continue;
            var gap = Math.Max(0, Math.Max(other.StartMs - self.EndMs, self.StartMs - other.EndMs));
            if (gap < bestGap) (best, bestGap) = (j, gap);
        }
        return best;
    }

    private static long Duration(VoiceSample s) => Math.Max(0, s.EndMs - s.StartMs);

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }

    /// <summary>Birim uzunluğa ölçekler; boş, sıfır ya da NaN/sonsuz içeren gömme için null.</summary>
    private static float[]? Normalize(float[]? v)
    {
        if (v is null || v.Length == 0) return null;
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        if (!double.IsFinite(sum) || sum <= 1e-12) return null;
        var norm = Math.Sqrt(sum);
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }
}
