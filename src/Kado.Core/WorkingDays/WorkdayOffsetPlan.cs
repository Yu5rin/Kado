using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kado.Core.WorkingDays;

/// <summary>
/// 工程逆算の1節目。「1次GO −9実働日」のような、基準日（納期）からのオフセット。
/// </summary>
/// <param name="Name">節目の名前。マイルストーン名（仕様期限・1次GOなど）に合わせておくと見比べやすい。</param>
/// <param name="Offset">
/// 基準日からの実働日数。負なら基準日より前へ遡り、正なら先送り、0なら基準日そのもの。
/// </param>
public sealed record WorkdayOffsetStep(string Name, int Offset)
{
    public override string ToString() => $"{Name} {(Offset > 0 ? "+" : string.Empty)}{Offset}";
}

/// <summary>
/// 名前付きのオフセット列（工程逆算の1セット）。
/// <para>
/// 製品や区分でリードタイム構造が違うことがあるため、複数セット持てるようにしてある
/// （要件書 4.2「仕様期限→1次GO→S中→M中が連続する稼働日に並び、月3サイクル」）。
/// </para>
/// </summary>
/// <param name="Id">セットの識別子。並べ替え・削除のあとも同じ行だと分かるための内部キー（画面には出さない）。</param>
/// <param name="Name">セットの名前。「量産品」「試作品」など。</param>
/// <param name="Steps">並び順を保った節目の列。基準日に近い側／遠い側、どちらを先にしてもよい。</param>
public sealed record WorkdayOffsetPlan(string Id, string Name, IReadOnlyList<WorkdayOffsetStep> Steps)
{
    /// <summary>
    /// 現行データに合わせた既定の1セット（要件書 4.2）。
    /// <para>設定が何も無い状態（初回起動）でだけ使う。空にした状態を「未設定」と取り違えないため、
    /// ここを呼ぶのは <see cref="WorkdayOffsetPlanStore.Read"/> が保存が無いと判定したときだけにする。</para>
    /// </summary>
    public static WorkdayOffsetPlan CreateDefault() => new(
        Guid.NewGuid().ToString("N"),
        "標準",
        [
            new WorkdayOffsetStep("仕様期限", -12),
            new WorkdayOffsetStep("1次GO", -9),
            new WorkdayOffsetStep("S中日程", -5),
            new WorkdayOffsetStep("M中日程", -2),
        ]);
}

/// <summary>
/// <see cref="WorkdayOffsetPlan"/> の列を設定（キーと値だけの単純な表）へ出し入れする。
/// <para>
/// 構造のある値は JSON にして1つのキーへ入れる、という既存の約束
/// （<c>SettingsRepository</c> の方針）に従う。
/// </para>
/// </summary>
public static class WorkdayOffsetPlanStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 書き出す。
    /// </summary>
    public static string Write(IReadOnlyList<WorkdayOffsetPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        return JsonSerializer.Serialize(plans, Options);
    }

    /// <summary>
    /// 読み込む。
    /// <para>
    /// 保存されたことが一度も無い（<paramref name="json"/> が null か空）ときだけ、既定の1セットを返す。
    /// 利用者がすべて消して空にした状態（<c>"[]"</c>）は、それとして空のまま返す。壊れて読めない
    /// ときも黙って既定へ倒す（設定を丸ごと失うより、いつもの1セットが出るほうがまだよい）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<WorkdayOffsetPlan> Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [WorkdayOffsetPlan.CreateDefault()];

        try
        {
            var plans = JsonSerializer.Deserialize<List<WorkdayOffsetPlan>>(json, Options);
            return plans is null ? [] : plans.AsReadOnly();
        }
        catch (JsonException)
        {
            return [WorkdayOffsetPlan.CreateDefault()];
        }
    }
}
