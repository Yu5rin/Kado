using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// 常駐しているあいだの、新しい版の確認の段取り。
/// <para>
/// 以前は起動したときと押したときだけだった。トレイに入ったまま何日も動かし続けると、
/// 起動した版のまま、新しい版に一度も気づかなかった。
/// </para>
/// </summary>
public class UpdateCheckScheduleTests
{
    private static readonly DateTime Start = new(2026, 9, 24, 9, 0, 0);

    [Fact]
    public void 一度も確かめていなければ時期が来ている()
    {
        Assert.True(new UpdateCheckSchedule().IsDue(Start));
    }

    [Fact]
    public void 起動から24時間たつまでは確かめない()
    {
        var schedule = new UpdateCheckSchedule();
        schedule.MarkChecked(Start);

        Assert.False(schedule.IsDue(Start.AddMinutes(30)));
        Assert.False(schedule.IsDue(Start.AddHours(23).AddMinutes(59)));
        Assert.True(schedule.IsDue(Start.AddHours(24)));
    }

    [Fact]
    public void 確かめたら次はそこから24時間()
    {
        var schedule = new UpdateCheckSchedule();
        schedule.MarkChecked(Start);

        // 25時間後に確かめた。次は、そこから24時間
        schedule.MarkChecked(Start.AddHours(25));

        Assert.False(schedule.IsDue(Start.AddHours(48)));
        Assert.True(schedule.IsDue(Start.AddHours(49)));
    }

    [Fact]
    public void 日をまたいで眠っていたら復帰ですぐ確かめる()
    {
        var schedule = new UpdateCheckSchedule();
        schedule.MarkChecked(new DateTime(2026, 9, 24, 18, 0, 0));

        // 翌朝8時に復帰。24時間（翌18時）はまだ先だが、日付が変わっている
        var morning = new DateTime(2026, 9, 25, 8, 0, 0);

        Assert.False(schedule.IsDue(morning));
        Assert.True(schedule.IsDue(morning, afterResume: true));
    }

    [Fact]
    public void 同じ日の復帰では急がない()
    {
        var schedule = new UpdateCheckSchedule();
        schedule.MarkChecked(new DateTime(2026, 9, 24, 9, 0, 0));

        Assert.False(schedule.IsDue(new DateTime(2026, 9, 24, 13, 0, 0), afterResume: true));
    }

    [Fact]
    public void 確かめられなかったら2時間後に出し直す()
    {
        var schedule = new UpdateCheckSchedule();
        schedule.MarkChecked(Start);

        // 会社の回線で GitHub に届かない。24時間待たず、かといって30分ごとには叩かない
        var failedAt = Start.AddHours(24);
        schedule.MarkFailed(failedAt);

        Assert.False(schedule.IsDue(failedAt.AddMinutes(30)));
        Assert.False(schedule.IsDue(failedAt.AddHours(1).AddMinutes(59)));
        Assert.True(schedule.IsDue(failedAt.AddHours(2)));
    }

    [Fact]
    public void 失敗のあとの復帰でも日が変わっていれば確かめる()
    {
        var schedule = new UpdateCheckSchedule();
        schedule.MarkChecked(new DateTime(2026, 9, 24, 9, 0, 0));
        schedule.MarkFailed(new DateTime(2026, 9, 25, 9, 0, 0));

        // 失敗の2時間後より前でも、日をまたいだ復帰（最後に確かめた日と違う）なら
        Assert.True(schedule.IsDue(new DateTime(2026, 9, 25, 9, 30, 0), afterResume: true));
    }

    [Fact]
    public void 時計を過去へ戻されても待ち続けない()
    {
        var schedule = new UpdateCheckSchedule();
        schedule.MarkChecked(Start);

        // 手動で1年前に戻された。次の予定は1年先になってしまうので、確かめ直す
        Assert.True(schedule.IsDue(Start.AddYears(-1)));
    }
}
