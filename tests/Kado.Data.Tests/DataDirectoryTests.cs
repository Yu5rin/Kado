using Kado.Data;

namespace Kado.Data.Tests;

/// <summary>
/// 保存先フォルダの決め方。アプリ名を Kado へ改めたときの引っ越しを確かめる。
/// <para>
/// 予定・タスク・実働日・設定・バックアップ・Google のトークンは、すべてこの
/// フォルダ1つに入っている。名前だけ変えて置き去りにすると、更新したとたんに
/// 中身が空になったように見える。
/// </para>
/// </summary>
public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"kado-datadir-{Guid.NewGuid():N}");

    public DataDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けで転んでもテストの結果は変えない
        }
    }

    [Fact]
    public void 新しく入れた人はそのまま新しい名前のフォルダを使う()
    {
        var directory = CalendarDatabase.ResolveDataDirectory(_root);

        Assert.Equal(Path.Combine(_root, "Kado"), directory);
        // 決めるだけで作りはしない。作るのは実際に開くとき
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void 旧い名前のフォルダしか無ければ中身ごと引っ越す()
    {
        var legacy = Path.Combine(_root, "SlideinaCalendar");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "data.db"), "予定とタスク");
        File.WriteAllText(Path.Combine(legacy, "google-tokens.dat"), "トークン");

        var directory = CalendarDatabase.ResolveDataDirectory(_root);

        Assert.Equal(Path.Combine(_root, "Kado"), directory);
        Assert.Equal("予定とタスク", File.ReadAllText(Path.Combine(directory, "data.db")));
        Assert.Equal("トークン", File.ReadAllText(Path.Combine(directory, "google-tokens.dat")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void 両方あるなら新しいほうを使い旧いほうには触らない()
    {
        var legacy = Path.Combine(_root, "SlideinaCalendar");
        var target = Path.Combine(_root, "Kado");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "data.db"), "旧");
        File.WriteAllText(Path.Combine(target, "data.db"), "新");

        var directory = CalendarDatabase.ResolveDataDirectory(_root);

        Assert.Equal(target, directory);
        Assert.Equal("旧", File.ReadAllText(Path.Combine(legacy, "data.db")));
        Assert.Equal("新", File.ReadAllText(Path.Combine(target, "data.db")));
    }
}
