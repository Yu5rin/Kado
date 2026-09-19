using System.IO;
using System.Text.Json;
using AppBarProbe.Interop;

namespace AppBarProbe;

/// <summary>
/// 安全装置その3。前回の異常終了を検知してワークエリアを復旧する。
/// <para>
/// <c>ABM_REMOVE</c> を呼ばずにプロセスが落ちると、Windows はワークエリアを削られたまま放置する。
/// ユーザーのデスクトップが壊れた状態で残るため、<b>プロトタイプの時点から必ず入れる</b>。
/// </para>
/// <para>
/// 仕組みは単純で、AppBar を登録する直前に「登録前のワークエリア」をファイルへ書き、
/// 正常に解除できたら消す。起動時にファイルが残っていれば前回落ちたと判断し、
/// 記録しておいた矩形を <c>SPI_SETWORKAREA</c> で書き戻す。
/// </para>
/// </summary>
internal static class WorkAreaRecovery
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SlideinaCalendar");

    private static readonly string StateFile = Path.Combine(StateDirectory, "appbar-probe.state.json");

    private sealed record SavedWorkArea(int Left, int Top, int Right, int Bottom, string SavedAt);

    /// <summary>AppBar 登録の直前に、現在のワークエリアを控える。</summary>
    public static void MarkRegistered(RECT workAreaBeforeRegister)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var saved = new SavedWorkArea(
                workAreaBeforeRegister.Left,
                workAreaBeforeRegister.Top,
                workAreaBeforeRegister.Right,
                workAreaBeforeRegister.Bottom,
                DateTimeOffset.Now.ToString("O"));

            // 書き込み途中で落ちても壊れたファイルを残さないよう、一時ファイル経由で置き換える
            var temp = StateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, StateFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 控えが取れなくても AppBar 自体は動かす。復旧できないリスクだけが残る。
        }
    }

    /// <summary>AppBar を正常に解除できたら控えを消す。</summary>
    public static void MarkUnregistered()
    {
        try
        {
            if (File.Exists(StateFile)) File.Delete(StateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 起動時に呼ぶ。控えが残っていれば前回の異常終了なので、ワークエリアを書き戻す。
    /// </summary>
    /// <returns>復旧を実行したら、書き戻した矩形の説明。何もしなければ null。</returns>
    public static string? RecoverIfNeeded()
    {
        SavedWorkArea? saved;
        try
        {
            if (!File.Exists(StateFile)) return null;
            saved = JsonSerializer.Deserialize<SavedWorkArea>(File.ReadAllText(StateFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (saved is null)
        {
            MarkUnregistered();
            return null;
        }

        var rect = new RECT
        {
            Left = saved.Left,
            Top = saved.Top,
            Right = saved.Right,
            Bottom = saved.Bottom,
        };

        var current = NativeMethods.GetWorkArea();
        NativeMethods.SetWorkArea(rect);
        MarkUnregistered();

        return $"前回の異常終了を検知しました。ワークエリアを {current} から {rect} に復旧しました。"
             + $"（控えた日時: {saved.SavedAt}）";
    }
}
