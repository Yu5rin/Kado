using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// 検索は SQL の LIKE で先に絞る。結果は、全件を読んで
/// <see cref="StringComparison.OrdinalIgnoreCase"/> で絞っていた今までと同じであること。
/// </summary>
public class TextSearchTests
{
    private static readonly DateOnly D = new(2026, 9, 24);

    private static CalendarEvent Ev(string id, string title, string? location = null, string? note = null,
        DateOnly? date = null, TimeOnly? start = null, string? raw = null) => new()
    {
        Id = id, Title = title, Location = location, Note = note, Date = date ?? D,
        StartTime = start, EndTime = start?.AddHours(1), GoogleRaw = raw,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>今までの探し方。全件を読んで、題・場所・メモのどれかが含むものを残す。</summary>
    private static string[] Old(EventRepository repo, string text) =>
        repo.All()
            .Where(e => TextSearch.Contains(e.Title, text) || TextSearch.Contains(e.Location, text)
                || TextSearch.Contains(e.Note, text))
            .Select(e => e.Id).ToArray();

    private static EventRepository Seed(TestDatabase db)
    {
        var repo = new EventRepository(db.Connection);

        repo.Upsert(Ev("e01", "VENUS 打ち合わせ", start: new TimeOnly(10, 0)));
        repo.Upsert(Ev("e02", "venus レビュー", start: new TimeOnly(9, 0)));
        repo.Upsert(Ev("e03", "ＶＥＮＵＳ（全角）"));
        repo.Upsert(Ev("e04", "ｖｅｎｕｓ（全角の小文字）"));
        repo.Upsert(Ev("e05", "進捗", location: "第2会議室"));
        repo.Upsert(Ev("e06", "検討", note: "資料を第2会議室に置く"));
        repo.Upsert(Ev("e07", "100% 達成", note: "目標は100%"));
        repo.Upsert(Ev("e08", "100x 達成"));
        repo.Upsert(Ev("e09", "a_b", note: "a-b"));
        repo.Upsert(Ev("e10", @"C:\temp\log"));
        repo.Upsert(Ev("e11", "ｱｲｳ（半角カナ）"));
        repo.Upsert(Ev("e12", "アイウ（全角カナ）"));
        repo.Upsert(Ev("e13", "Café ミーティング"));
        repo.Upsert(Ev("e14", "CAFÉ 休憩"));
        repo.Upsert(Ev("e15", "🎉 パーティー"));
        repo.Upsert(Ev("e16", "同じ日同じ時刻 A", start: new TimeOnly(13, 0)));
        repo.Upsert(Ev("e17", "同じ日同じ時刻 B", start: new TimeOnly(13, 0)));
        repo.Upsert(Ev("e18", "同じ日同じ時刻 C", start: new TimeOnly(13, 0)));
        repo.Upsert(Ev("e19", "別の日の会議", date: D.AddDays(-30)));
        repo.Upsert(Ev("e20", "未来の会議", date: D.AddDays(45)));
        repo.Upsert(Ev("e21", "題だけ", raw: "{\"summary\":\"RAWTEXT-ONLY\"}"));

        return repo;
    }

    public static TheoryData<string> Queries => new()
    {
        "venus", "VENUS", "Venus", "ＶＥＮＵＳ", "ｖｅｎｕｓ", "ｖｅｎ",
        "会議", "第2会議室", "会議室", "100%", "100", "%", "_", "a_b", "a-b", "\\", "C:\\temp", "temp\\log",
        "ｱｲｳ", "アイウ", "ア", "café", "CAFÉ", "Café", "É", "é", "🎉", "パーティー",
        "同じ日", "同じ日同じ時刻", " ", "（", "存在しない言葉", "RAWTEXT-ONLY", "summary",
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void 予定の検索結果は_今までの探し方と同じ中身で同じ並び(string text)
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        Assert.Equal(Old(repo, text), repo.SearchText(text).Select(e => e.Id).ToArray());
    }

    [Fact]
    public void 大文字小文字は区別しない_全角と半角は区別する()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        Assert.Equal(["e02", "e01"], repo.SearchText("venus").Select(e => e.Id));   // 9時 → 10時

        // 全角どうしは大文字小文字を区別しない（今までどおり）。LIKE だけでは落ちるので、先に絞らない
        Assert.Equal(["e03", "e04"], repo.SearchText("ｖｅｎｕｓ").Select(e => e.Id).Order());

        // 全角と半角は別の文字
        Assert.DoesNotContain("e03", repo.SearchText("venus").Select(e => e.Id));
        Assert.DoesNotContain("e12", repo.SearchText("ｱｲｳ").Select(e => e.Id));
        Assert.Equal(["e11"], repo.SearchText("ｱｲｳ").Select(e => e.Id));

        // アクセント付きの文字も、大文字小文字を区別しない
        Assert.Equal(["e13", "e14"], repo.SearchText("é").Select(e => e.Id).Order());
    }

    [Fact]
    public void ワイルドカードの文字は文字として探す()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        Assert.Equal(["e07"], repo.SearchText("100%").Select(e => e.Id));
        Assert.Equal(["e09"], repo.SearchText("a_b").Select(e => e.Id));
        Assert.Equal(["e10"], repo.SearchText("temp\\log").Select(e => e.Id));

        // 「_」が「任意の1文字」として働くと e08 などに当たってしまう
        Assert.DoesNotContain("e08", repo.SearchText("100_").Select(e => e.Id));
    }

    [Fact]
    public void 同じ日で同じ時刻の予定は_登録した順に並ぶ()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        Assert.Equal(["e16", "e17", "e18"], repo.SearchText("同じ日同じ時刻").Select(e => e.Id));
    }

    [Fact]
    public void 返す予定は読む列を絞っていて_Googleの生データは読まない()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        var found = Assert.Single(repo.SearchText("題だけ"));

        Assert.Equal("e21", found.Id);
        Assert.Equal(D, found.Date);
        Assert.Null(found.GoogleRaw);

        // 生データの中の語では、見つからない（今までもそう。列は比べていない）
        Assert.Empty(repo.SearchText("RAWTEXT-ONLY"));
    }

    [Fact]
    public void マイルストーン判定に要る出どころは読む()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);
        repo.Upsert(Ev("m1", "仕様期限") with { Source = "workingday" });

        Assert.Equal("workingday", Assert.Single(repo.SearchText("仕様")).Source);
    }

    [Fact]
    public void 空の文字では何も返さない()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        Assert.Empty(repo.SearchText(""));
        Assert.Empty(new TaskRepository(db.Connection).SearchText(""));
    }

    // ------------------------------------------------------------------
    // タスク
    // ------------------------------------------------------------------

    private static TaskItem Tk(string id, string title, string? note = null, DateOnly? due = null) => new()
    {
        Id = id, Title = title, Note = note, Due = due, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Theory]
    [InlineData("資料")]
    [InlineData("SHIRYO")]
    [InlineData("ｓｈｉｒｙｏ")]
    [InlineData("100%")]
    [InlineData("メモ")]
    [InlineData("存在しない")]
    public void タスクの検索結果も_今までの探し方と同じ中身で同じ並び(string text)
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.Upsert(Tk("t1", "資料作成", due: D));
        repo.Upsert(Tk("t2", "shiryo 整理"));
        repo.Upsert(Tk("t3", "ＳＨＩＲＹＯ（全角）", note: "メモ", due: D.AddDays(-1)));
        repo.Upsert(Tk("t4", "ｓｈｉｒｙｏ（全角小文字）"));
        repo.Upsert(Tk("t5", "割合", note: "100% 資料"));
        repo.Upsert(Tk("t6", "期限なしの資料"));
        repo.Upsert(Tk("t7", "同じ期限 資料", due: D));

        var old = repo.All()
            .Where(t => TextSearch.Contains(t.Title, text) || TextSearch.Contains(t.Note, text))
            .Select(t => t.Id).ToArray();

        var actual = repo.SearchText(text);

        Assert.Equal(old, actual.Select(t => t.Id).ToArray());
        Assert.All(actual, t => Assert.Equal(repo.Find(t.Id)!.Due, t.Due));
    }

    [Fact]
    public void タスクの検索は題とメモだけを見る()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.Upsert(Tk("t1", "題") with { GoogleRaw = "{\"x\":\"隠れた語\"}" });

        Assert.Empty(repo.SearchText("隠れた語"));
    }

    // ------------------------------------------------------------------
    // LIKE で先に絞ってよい文字
    // ------------------------------------------------------------------

    [Fact]
    public void 日本語と英数字は先に絞れるが_全角英字とアクセントとサロゲートは絞らない()
    {
        Assert.True(TextSearch.TryCreateLikePattern("会議", out _));
        Assert.True(TextSearch.TryCreateLikePattern("ミーティング", out _));
        Assert.True(TextSearch.TryCreateLikePattern("ｱｲｳ", out _));          // 半角カナは大文字小文字を持たない
        Assert.True(TextSearch.TryCreateLikePattern("１２３（）", out _));
        Assert.True(TextSearch.TryCreateLikePattern("Venus 2", out _));

        Assert.False(TextSearch.TryCreateLikePattern("ＶＥＮＵＳ", out _));
        Assert.False(TextSearch.TryCreateLikePattern("ｖ", out _));
        Assert.False(TextSearch.TryCreateLikePattern("Café", out _));
        Assert.False(TextSearch.TryCreateLikePattern("🎉", out _));
        Assert.False(TextSearch.TryCreateLikePattern("", out _));
    }

    [Fact]
    public void パターンは_ワイルドカードをエスケープして両側を開ける()
    {
        Assert.True(TextSearch.TryCreateLikePattern("a_b%c\\d", out var pattern));
        Assert.Equal("%a\\_b\\%c\\\\d%", pattern);
    }

    [Fact]
    public void 先に絞ってよいと判断した文字は_ほかの文字とは大文字小文字でも等しくならない()
    {
        // LIKE は「同じ文字」か「ASCII の大文字小文字違い」しか同じとみなさない。
        // OrdinalIgnoreCase がそれ以外を同じとみなす文字（絞ると落としてしまう）が、
        // 先に絞ってよい文字の中に無いことを、BMP の全体で確かめる
        var sc = StringComparison.OrdinalIgnoreCase;

        for (var i = 0; i < 0x10000; i++)
        {
            var c = (char)i;
            if (!TextSearch.IsSafeForLike(c)) continue;

            var s = c.ToString();

            if (c < 0x80)
            {
                // ASCII の文字と等しくなる非 ASCII の文字が無い
                for (var j = 0x80; j < 0x10000; j++)
                {
                    if (char.IsSurrogate((char)j)) continue;
                    Assert.False(((char)j).ToString().Equals(s, sc), $"U+{j:X4} が {s} と等しい");
                }
            }
            else
            {
                // 大文字小文字を持たない文字は、自分としか等しくならない
                Assert.Equal(c, char.ToUpperInvariant(c));
                Assert.Equal(c, char.ToLowerInvariant(c));

                foreach (var other in new[] { char.ToUpperInvariant(c), char.ToLowerInvariant(c) })
                {
                    Assert.Equal(c, other);
                }
            }
        }
    }
}
