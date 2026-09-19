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
/// 記録しておいた矩形を書き戻す。
/// </para>
/// <para>
/// なお、タスクマネージャの「プロセス」タブからの終了は、ウィンドウに <c>WM_CLOSE</c> が
/// 送られるため<b>正常終了として扱われる</b>（控えも消える）。異常終了を試すには
/// 「詳細」タブから終了するか <c>taskkill /F</c> を使う必要がある。
/// </para>
/// </summary>
internal static class WorkAreaRecovery
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SlideinaCalendar");

    private static readonly string StateFile = Path.Combine(StateDirectory, "appbar-probe.state.json");

    /// <summary>控えの中身。</summary>
    internal sealed record SavedWorkArea(int Left, int Top, int Right, int Bottom, string SavedAt)
    {
        public RECT ToRect() => new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
    }

    /// <summary>控えの保存先。ログに出して所在を分かるようにする。</summary>
    public static string StateFilePath => StateFile;

    // ------------------------------------------------------------------
    // 記録
    // ------------------------------------------------------------------

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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
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

    // ------------------------------------------------------------------
    // 復旧
    // ------------------------------------------------------------------

    /// <summary>
    /// 控えが残っているか調べる。残っていれば前回は異常終了している。
    /// <para>ここでは消さない。実際に復旧する <see cref="Recover"/> で消す。</para>
    /// </summary>
    public static SavedWorkArea? ReadPending()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return JsonSerializer.Deserialize<SavedWorkArea>(File.ReadAllText(StateFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // 壊れた控えは判断材料にならないので捨てる
            MarkUnregistered();
            return null;
        }
    }

    /// <summary>
    /// ワークエリアを控えの状態へ書き戻す。
    /// <para>
    /// Explorer 側に死んだウィンドウの AppBar 登録が残っていると
    /// <c>SPI_SETWORKAREA</c> だけでは戻り切らないことがあるため、
    /// 先に AppBar のやり取りを一度発生させて掃除を促す。
    /// </para>
    /// </summary>
    /// <param name="saved">控えの内容。</param>
    /// <param name="hwnd">ダミーの AppBar 登録に使うウィンドウハンドル。</param>
    /// <returns>ログに出す復旧結果の説明。</returns>
    public static string Recover(SavedWorkArea saved, IntPtr hwnd)
    {
        var before = NativeMethods.GetWorkArea();
        var target = saved.ToRect();

        NativeMethods.NudgeAppBarRegistry(hwnd);
        NativeMethods.SetWorkArea(target);

        var after = NativeMethods.GetWorkArea();
        MarkUnregistered();

        var verdict = after.Left == target.Left && after.Top == target.Top
                   && after.Right == target.Right && after.Bottom == target.Bottom
            ? "復旧しました"
            : "復旧を試みましたが、期待どおりになっていません";

        return $"前回の異常終了を検知しました。ワークエリアを {before} → {after} に{verdict}"
             + $"（控えた日時: {saved.SavedAt}、目標: {target}）。";
    }
}
