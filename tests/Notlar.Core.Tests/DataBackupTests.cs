using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class DataBackupTests
{
    private static BackupEntry Entry(string name)
    {
        Assert.True(DataBackup.TryParseFileName(name, out var at, out var label));
        return new BackupEntry(Path.Combine("x", name), null, at, label);
    }

    [Fact]
    public void FileName_RoundTrips_WithAndWithoutLabel()
    {
        var at = new DateTime(2026, 11, 24, 15, 30, 5);
        Assert.Equal("notlar-2026-11-24-153005.db", DataBackup.FileNameFor(at));
        Assert.Equal("notlar-2026-11-24-153005-geri-yukleme-oncesi.db", DataBackup.FileNameFor(at, DataBackup.BeforeRestoreLabel));

        Assert.True(DataBackup.TryParseFileName("notlar-2026-11-24-153005-geri-yukleme-oncesi.db", out var parsed, out var label));
        Assert.Equal(at, parsed);
        Assert.Equal(DataBackup.BeforeRestoreLabel, label);
    }

    [Fact]
    public void TryParse_AcceptsLegacyDailyName_RejectsOthers()
    {
        Assert.True(DataBackup.TryParseFileName("notlar-2026-11-20.db", out var at, out var label));
        Assert.Equal(new DateTime(2026, 11, 20), at);
        Assert.Null(label);

        Assert.False(DataBackup.TryParseFileName("notlar-2026-11-20.db.tmp", out _, out _));
        Assert.False(DataBackup.TryParseFileName("notlar-2026-11-20.db-wal", out _, out _));
        Assert.False(DataBackup.TryParseFileName("notlar-2026-11-20-153005.ayarlar.json", out _, out _));
        Assert.False(DataBackup.TryParseFileName("notlar-2026-13-40.db", out _, out _));
        Assert.False(DataBackup.TryParseFileName("baska.db", out _, out _));
    }

    [Fact]
    public void SelectForDeletion_KeepsNewestByTime_IncludingLegacyNames()
    {
        var entries = new[]
        {
            Entry("notlar-2026-11-20.db"),
            Entry("notlar-2026-11-24-090000.db"),
            Entry("notlar-2026-11-22-120000-geri-yukleme-oncesi.db"),
            Entry("notlar-2026-11-24.db"), // 24'ü 00:00 → 24 09:00'dan eski
            Entry("notlar-2026-11-21-235959.db"),
        };

        var deleted = DataBackup.SelectForDeletion(entries, keep: 3).Select(e => Path.GetFileName(e.DbPath)).ToList();

        Assert.Equal(["notlar-2026-11-21-235959.db", "notlar-2026-11-20.db"], deleted);
    }

    [Fact]
    public void SelectForDeletion_NeverDeletesEverything()
    {
        var entries = new[] { Entry("notlar-2026-11-20.db"), Entry("notlar-2026-11-21.db") };
        var deleted = DataBackup.SelectForDeletion(entries, keep: 0);
        Assert.Single(deleted);
        Assert.Equal("notlar-2026-11-20.db", Path.GetFileName(deleted[0].DbPath));
        Assert.Empty(DataBackup.SelectForDeletion(entries, keep: 5));
    }

    [Theory]
    [InlineData(BackupFrequency.Off, null, false)]
    [InlineData(BackupFrequency.Daily, null, true)]
    [InlineData(BackupFrequency.Weekly, null, true)]
    [InlineData(BackupFrequency.Daily, "2026-11-24T00:00:00", false)]
    [InlineData(BackupFrequency.Daily, "2026-11-23T23:59:00", true)]
    [InlineData(BackupFrequency.Weekly, "2026-11-18T23:00:00", false)]
    [InlineData(BackupFrequency.Weekly, "2026-11-17T23:00:00", true)]
    [InlineData(BackupFrequency.Off, "2020-01-01T00:00:00", false)]
    public void IsDue(BackupFrequency frequency, string? last, bool expected)
    {
        var now = new DateTime(2026, 11, 24, 10, 0, 0);
        DateTime? lastBackup = last is null ? null : DateTime.Parse(last, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, DataBackup.IsDue(frequency, lastBackup, now));
    }

    [Theory]
    [InlineData(0, DataBackup.DefaultKeep)]
    [InlineData(-3, DataBackup.DefaultKeep)]
    [InlineData(101, DataBackup.DefaultKeep)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    public void NormalizeKeep(int input, int expected) => Assert.Equal(expected, DataBackup.NormalizeKeep(input));

    [Fact]
    public void Create_List_Rotate_Restore_RoundTrip()
    {
        using var fx = new StoreFixture();
        var backupDir = Path.Combine(fx.Dir, "yedek");
        var settingsPath = Path.Combine(fx.Dir, "ayarlar.json");
        File.WriteAllText(settingsPath, """{"DayFontSize":18}""");

        fx.Store.Add("birinci");
        var first = DataBackup.Create(fx.DbPath, settingsPath, backupDir, new DateTime(2026, 11, 24, 9, 0, 0));
        Assert.True(File.Exists(first.DbPath));
        Assert.NotNull(first.SettingsPath);
        Assert.Equal("""{"DayFontSize":18}""", File.ReadAllText(first.SettingsPath!));

        // Aynı saniyede ikinci yedek ezmez.
        var dup = DataBackup.Create(fx.DbPath, null, backupDir, new DateTime(2026, 11, 24, 9, 0, 0));
        Assert.NotEqual(first.DbPath, dup.DbPath);
        Assert.Null(dup.SettingsPath);

        fx.Store.Add("ikinci");
        fx.Store.Add("üçüncü");
        Assert.Equal(3, fx.Store.List(new NoteFilter()).Count);

        var pre = DataBackup.Create(fx.DbPath, settingsPath, backupDir, new DateTime(2026, 11, 24, 10, 0, 0),
            DataBackup.BeforeRestoreLabel);
        Assert.True(pre.IsBeforeRestore);

        DataBackup.RestoreDatabase(first.DbPath, fx.DbPath);

        // Açık (havuzdaki) mağaza bağlantısı da geri yüklenen içeriği görür.
        var notes = fx.Store.List(new NoteFilter());
        Assert.Single(notes);
        Assert.Equal("birinci", notes[0].Text);
        Assert.Single(fx.Reopen().List(new NoteFilter()));

        var listed = DataBackup.List(backupDir);
        Assert.Equal(3, listed.Count);
        Assert.Equal(pre.DbPath, listed[0].DbPath);

        var deleted = DataBackup.Rotate(backupDir, keep: 1);
        Assert.Equal(2, deleted.Count);
        Assert.False(File.Exists(first.DbPath));
        Assert.False(File.Exists(first.SettingsPath));
        Assert.Equal(pre.DbPath, Assert.Single(DataBackup.List(backupDir)).DbPath);

        // Geri yükleme öncesi yedekten tekrar dönülebilir.
        DataBackup.RestoreDatabase(pre.DbPath, fx.DbPath);
        Assert.Equal(3, fx.Store.List(new NoteFilter()).Count);
    }

    [Fact]
    public void Restore_RejectsNonNotlarDatabase_AndLeavesLiveDataIntact()
    {
        using var fx = new StoreFixture();
        fx.Store.Add("canlı");

        var foreign = Path.Combine(fx.Dir, "notlar-2026-11-24-120000.db");
        using (var conn = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE baska(x INTEGER)";
            cmd.ExecuteNonQuery();
        }
        var garbage = Path.Combine(fx.Dir, "bozuk.db");
        File.WriteAllText(garbage, "bu bir veritabanı değil");

        Assert.Throws<InvalidDataException>(() => DataBackup.RestoreDatabase(foreign, fx.DbPath));
        Assert.Throws<InvalidDataException>(() => DataBackup.RestoreDatabase(garbage, fx.DbPath));
        Assert.Throws<FileNotFoundException>(() => DataBackup.RestoreDatabase(Path.Combine(fx.Dir, "yok.db"), fx.DbPath));

        Assert.Equal("canlı", Assert.Single(fx.Store.List(new NoteFilter())).Text);
    }

    [Fact]
    public void List_MissingDir_IsEmpty() =>
        Assert.Empty(DataBackup.List(Path.Combine(Path.GetTempPath(), "notlar-yok-" + Guid.NewGuid().ToString("N"))));
}

public sealed class AudioRetentionPolicyTests
{
    [Theory]
    [InlineData(7, 7)]
    [InlineData(14, 14)]
    [InlineData(30, 30)]
    [InlineData(90, 90)]
    [InlineData(0, 0)]
    [InlineData(15, AudioRetentionPolicy.DefaultDays)]
    [InlineData(-1, AudioRetentionPolicy.DefaultDays)]
    public void Normalize(int input, int expected) => Assert.Equal(expected, AudioRetentionPolicy.Normalize(input));

    [Fact]
    public void ToAge_NeverMeansNoPurge()
    {
        Assert.Null(AudioRetentionPolicy.ToAge(AudioRetentionPolicy.Never));
        Assert.Equal(TimeSpan.FromDays(30), AudioRetentionPolicy.ToAge(30));
        Assert.Equal(TimeSpan.FromDays(14), AudioRetentionPolicy.ToAge(12345));
    }

    [Fact]
    public void Describe()
    {
        Assert.Equal("Hiç silme", AudioRetentionPolicy.Describe(0));
        Assert.Equal("90 gün", AudioRetentionPolicy.Describe(90));
    }
}

public sealed class StorageUsageTests
{
    [Fact]
    public void SizeOf_SumsNestedFiles_AndMissingIsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "notlar-boyut-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "alt"));
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "alt", "b.bin"), new byte[250]);
            File.WriteAllBytes(Path.Combine(dir, "x.db"), new byte[10]);
            File.WriteAllBytes(Path.Combine(dir, "x.db-wal"), new byte[5]);

            Assert.Equal(365, StorageUsage.SizeOf(dir));
            Assert.Equal(100, StorageUsage.SizeOf(Path.Combine(dir, "a.bin")));
            Assert.Equal(15, StorageUsage.DatabaseSize(Path.Combine(dir, "x.db")));
            Assert.Equal(0, StorageUsage.SizeOf(Path.Combine(dir, "yok")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, "0,0 MB")]
    [InlineData(-5, "0,0 MB")]
    [InlineData(1024 * 1024 * 12 + 1024 * 300, "12,3 MB")]
    [InlineData(1024L * 1024 * 2048, "2.048,0 MB")]
    public void FormatMegabytes(long bytes, string expected) => Assert.Equal(expected, StorageUsage.FormatMegabytes(bytes));
}
