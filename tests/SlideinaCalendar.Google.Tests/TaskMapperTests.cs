using System.Text.Json;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Google.Mapping;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// Google Tasks のタスクの読み書き。
/// <para>
/// 期限は UTC の 00:00 として届く。日本時間で解釈すると9時間ぶん前の日付になり、
/// 1日ずれる。
/// </para>
/// </summary>
public class TaskMapperTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void タスクを読める()
    {
        var value = TaskMapper.FromGoogle(Json("""
            {
              "id": "t1",
              "title": "台数計画の確定",
              "notes": "9月度ぶん",
              "status": "needsAction",
              "due": "2026-09-24T00:00:00.000Z",
              "updated": "2026-09-19T01:23:45.000Z",
              "position": "00000000000000000001"
            }
            """), "@default");

        Assert.Equal("台数計画の確定", value.Title);
        Assert.Equal(D(2026, 9, 24), value.Due);
        Assert.False(value.IsDone);
        Assert.Equal("9月度ぶん", value.Note);
        Assert.Equal("t1", value.GoogleTaskId);
        Assert.Equal("@default", value.GoogleTaskListId);
        Assert.Equal("00000000000000000001", value.Position);
    }

    [Fact]
    public void 期限の日付が時差でずれない()
    {
        // 日本時間で解釈すると9月23日になってしまう
        var value = TaskMapper.FromGoogle(
            Json("""{"id":"t2","title":"集計","due":"2026-09-24T00:00:00.000Z"}"""), "@default");

        Assert.Equal(D(2026, 9, 24), value.Due);
    }

    [Fact]
    public void 期限は読んで書いても動かない()
    {
        var value = TaskMapper.FromGoogle(
            Json("""{"id":"t3","title":"集計","due":"2026-09-24T00:00:00.000Z"}"""), "@default");

        var body = TaskMapper.ToGoogle(value);
        Assert.Equal("2026-09-24T00:00:00.000Z", body["due"]!.GetValue<string>());

        // もう一往復しても動かない
        var again = TaskMapper.FromGoogle(
            Json($$"""{"id":"t3","title":"集計","due":{{body["due"]!.ToJsonString()}}}"""), "@default");

        Assert.Equal(value.Due, again.Due);
    }

    [Fact]
    public void 期限なしを持てる()
    {
        var value = TaskMapper.FromGoogle(Json("""{"id":"t4","title":"いつかやる"}"""), "@default");

        // 「期限だけある」「いつやるか未定」を持てる必要がある（要件書 3.1）
        Assert.Null(value.Due);
        Assert.False(value.HasDue);

        // null を送ると期限を外す意味になる
        Assert.Null(TaskMapper.ToGoogle(value)["due"]);
    }

    [Fact]
    public void 完了を読める()
    {
        var value = TaskMapper.FromGoogle(Json("""
            {
              "id": "t5", "title": "済んだ作業", "status": "completed",
              "completed": "2026-09-18T07:00:00.000Z"
            }
            """), "@default");

        Assert.True(value.IsDone);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T07:00:00.000Z"), value.CompletedAt);
    }

    [Fact]
    public void 未完了なら完了日時を持たない()
    {
        var value = TaskMapper.FromGoogle(
            Json("""{"id":"t6","title":"まだ","status":"needsAction"}"""), "@default");

        Assert.False(value.IsDone);
        Assert.Null(value.CompletedAt);
    }

    [Fact]
    public void 完了を書き戻せる()
    {
        var done = TaskMapper.ToGoogle(new TaskItem { Id = "t1", Title = "済んだ", IsDone = true });
        Assert.Equal("completed", done["status"]!.GetValue<string>());

        var open = TaskMapper.ToGoogle(new TaskItem { Id = "t1", Title = "まだ", IsDone = false });
        Assert.Equal("needsAction", open["status"]!.GetValue<string>());
    }

    [Fact]
    public void 削除と完了を混ぜない()
    {
        // deleted は消された印、hidden は完了して一覧から隠れただけ
        Assert.True(TaskMapper.IsDeleted(Json("""{"id":"t1","deleted":true}""")));
        Assert.False(TaskMapper.IsDeleted(Json("""{"id":"t1","hidden":true,"status":"completed"}""")));
        Assert.False(TaskMapper.IsDeleted(Json("""{"id":"t1"}""")));
    }

    [Fact]
    public void 親子と並び順は書き戻さない()
    {
        var body = TaskMapper.ToGoogle(new TaskItem
        {
            Id = "t1", Title = "サブタスク", ParentId = "t0", Position = "00000000000000000002",
        });

        // move でしか変えられない。送っても無視される
        Assert.False(body.ContainsKey("parent"));
        Assert.False(body.ContainsKey("position"));
    }

    [Fact]
    public void 親子と並び順は控える()
    {
        var value = TaskMapper.FromGoogle(Json("""
            {"id":"t7","title":"サブタスク","parent":"t0","position":"00000000000000000002"}
            """), "@default");

        Assert.Equal("t0", value.ParentId);
        Assert.Equal("00000000000000000002", value.Position);
    }

    [Fact]
    public void 所属はこちらの識別子を保つ()
    {
        // Google のリスト ID をそのまま出すと画面に出てしまう
        var value = TaskMapper.FromGoogle(
            Json("""{"id":"t8","title":"集計"}"""), "MTIzNDU2", localListId: "local:mytasks");

        Assert.Equal("local:mytasks", value.TaskListId);
        Assert.Equal("MTIzNDU2", value.GoogleTaskListId);
    }

    [Fact]
    public void 受け取ったままなら書き戻さない()
    {
        var value = TaskMapper.FromGoogle(Json("""
            {
              "id": "t9", "title": "集計", "notes": null,
              "status": "needsAction", "due": "2026-09-24T00:00:00.000Z"
            }
            """), "@default");

        Assert.False(TaskMapper.NeedsPush(value));
    }

    [Fact]
    public void 期限の書き方の揺れでは書き戻さない()
    {
        // 小数秒の桁が違っても、指している日付が同じなら送らない
        var value = TaskMapper.FromGoogle(Json("""
            {"id":"t10","title":"集計","status":"needsAction","due":"2026-09-24T00:00:00Z"}
            """), "@default");

        Assert.False(TaskMapper.NeedsPush(value));
    }

    [Fact]
    public void 変えたら書き戻す()
    {
        var value = TaskMapper.FromGoogle(Json("""
            {"id":"t11","title":"集計","status":"needsAction","due":"2026-09-24T00:00:00.000Z"}
            """), "@default");

        Assert.True(TaskMapper.NeedsPush(value with { Title = "集計（変更）" }));
        Assert.True(TaskMapper.NeedsPush(value with { IsDone = true }));
        Assert.True(TaskMapper.NeedsPush(value with { Due = D(2026, 9, 25) }));
        Assert.True(TaskMapper.NeedsPush(value with { Due = null }));
    }

    [Fact]
    public void 一度も受け取っていなければ書き戻す()
    {
        Assert.True(TaskMapper.NeedsPush(new TaskItem { Id = "local1", Title = "手で入れたタスク" }));
    }

    [Fact]
    public void ローカルの識別子は引き継ぐ()
    {
        var existing = new TaskItem { Id = "元からの ID", Title = "古い名前" };
        var value = TaskMapper.FromGoogle(
            Json("""{"id":"t12","title":"集計"}"""), "@default", existing: existing);

        Assert.Equal("元からの ID", value.Id);
        Assert.Equal("集計", value.Title);
    }
}
