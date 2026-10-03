using Kado.Presentation.Settings;
using static Kado.App.Shell.NativeMethods;

namespace Kado.App.Shell;

/// <summary>
/// <see cref="System.Windows.Window"/> や Win32 の呼び出しを挟まない、シェルまわりの
/// 純粋な計算。
/// <para>
/// 入力から出力が一意に決まる部分だけをここへ引き剥がしてある。<see cref="ShellController"/>
/// や <see cref="AppBarHost"/> の本体は実際に Win32 を呼ぶので Windows でしか検査できないが、
/// ここは <c>Window</c> に依存しないのでテストできる。
/// </para>
/// </summary>
internal static class ShellGeometry
{
    /// <summary>
    /// 画面の外に置いたときの左端。休止位置（<paramref name="resting"/>）を基準にする。
    /// <para>
    /// モニタの取り違えや倍率のずれがあっても、寄せている辺の向こう側にしか行かない
    /// （不具合1）。寄せている辺にタスクバーがあるなどで休止位置がモニタ端より内側の
    /// ときは、モニタ端まで出す。
    /// </para>
    /// </summary>
    internal static double OffScreenLeft(DockEdge edge, double resting, double width, RECT screen, double scale) =>
        edge == DockEdge.Left
            ? Math.Min(resting, screen.left / scale) - width
            : Math.Max(resting + width, screen.right / scale);

    /// <summary>
    /// 居かたに応じて、タスクバーにボタンを出すか。ふつうのウィンドウのときだけ出す。
    /// <para>
    /// スライド・ピン留めは、通知領域のアイコンとホットキーで呼べる。スライドで窓を
    /// 出し入れ（Show／Hide）するたびにボタンが出たり消えたりするのは邪魔なので出さない。
    /// ボタンが無い窓は Alt＋Tab にも出ない。ウィンドウに戻せば、ボタンも Alt＋Tab も戻る。
    /// </para>
    /// </summary>
    internal static bool ShowsInTaskbar(ShellMode mode) => mode == ShellMode.Window;

    /// <summary>1 DIP 未満のずれでは動かさない。丸め誤差だけで毎回位置を書き換えない（不具合2）。</summary>
    internal static bool ShouldMove(double value, double current) => Math.Abs(value - current) >= 1;

    /// <summary>
    /// 開く演出（滑り出し）のあいだ、いまの <paramref name="width"/> に対応する
    /// <c>Left</c>。
    /// <para>
    /// 左に寄せているときは <paramref name="restingLeft"/> のまま動かさない。右に
    /// 寄せているときは、右端（<paramref name="restingLeft"/> + <paramref name="restingWidth"/>）
    /// を定位置のまま固定し、<paramref name="width"/> が変わるぶん <c>Left</c> を
    /// 追従させる。窓が左へ伸びる形になる。
    /// </para>
    /// </summary>
    internal static double RevealLeft(DockEdge edge, double restingLeft, double restingWidth, double width) =>
        edge == DockEdge.Left ? restingLeft : restingLeft + restingWidth - width;

    /// <summary>
    /// AppBar へ提案する矩形。上下は作業領域、左右はモニタ全体から取る（不具合2）。
    /// <para>
    /// 上下までモニタ全体を提案すると、タスクバー分の切り詰めを Windows 任せにする
    /// ことになり、ピン留めした瞬間に少し動く。左右まで作業領域から取ってしまうと、
    /// 登録中に再交渉が走るたびに、自分が削った帯のぶん内側へ押し込まれていく。
    /// </para>
    /// </summary>
    internal static RECT ProposeRect(RECT monitor, RECT work, DockEdge edge, int width)
    {
        var rc = monitor;
        rc.top = work.top;
        rc.bottom = work.bottom;

        return SliceWidth(rc, edge, width);
    }

    /// <summary>
    /// <c>ABM_QUERYPOS</c> が返した矩形（<paramref name="queried"/>）から、実際に
    /// <c>ABM_SETPOS</c> へ渡す矩形を決める。<b>上下は提案のまま、左右だけ返事を採る。</b>
    /// <para>
    /// Windows 11 では、タスクバーを除いた作業領域（rcWork）をそのまま上下に提案しても、
    /// <c>ABM_QUERYPOS</c> が下端を24px 詰めて返してくることがある（実機：提案 bottom=1032 に対し
    /// 返事 bottom=1008）。そのまま採ると、窓の下とタスクバーのあいだに隙間ができる。
    /// rcWork は他の AppBar とタスクバーをすでに除いた領域なので、上下をそこに合わせても
    /// 他の AppBar とは重ならない。左右は、同じ辺に他の AppBar が居るときの譲り合いが
    /// 返事に入るので、返事を採る。
    /// </para>
    /// <para>返事の左右が空（幅0以下）のときは、左右も提案のままにする。</para>
    /// </summary>
    /// <param name="proposed"><see cref="ProposeRect"/> で提案した矩形。</param>
    /// <param name="queried"><c>ABM_QUERYPOS</c> が返した矩形。</param>
    /// <param name="verticalAdjusted">返事が上下を提案と違う値にしていたら true（記録用）。</param>
    internal static RECT ResolveDocked(
        RECT proposed, RECT queried, DockEdge edge, int width, out bool verticalAdjusted)
    {
        verticalAdjusted = queried.top != proposed.top || queried.bottom != proposed.bottom;

        var rc = proposed;

        if (queried.right > queried.left)
        {
            rc.left = queried.left;
            rc.right = queried.right;
        }

        return SliceWidth(rc, edge, width);
    }

    /// <summary>
    /// 交渉の結果を <c>ABM_SETPOS</c> と窓の移動へ反映する必要があるか。
    /// <para>
    /// すでに確定している矩形と同じで、窓もそこに居るなら、もう一度 <c>ABM_SETPOS</c> を
    /// 呼んで窓を動かす意味が無い（作業領域の変更の通知が増え、デスクトップのアイコンの
    /// 並べ直しを誘う）。確定値と違う、まだ確定していない、窓がずれている、のどれかなら
    /// 反映する。
    /// </para>
    /// </summary>
    /// <param name="confirmed">前回確定した矩形。まだ無ければ null。</param>
    /// <param name="next">今回の交渉で決まった矩形。</param>
    /// <param name="actual">いまの窓の矩形（物理ピクセル）。取れなければ null。</param>
    /// <param name="tolerance">窓の位置の食い違いとして許すピクセル数。DIP との丸めぶん。</param>
    internal static bool NeedsApply(RECT? confirmed, RECT next, RECT? actual, int tolerance)
    {
        if (confirmed is not { } known || !Same(known, next, 0)) return true;
        if (actual is not { } window) return true;

        return !Same(window, next, tolerance);
    }

    /// <summary>2つの矩形が、各辺 <paramref name="tolerance"/> ピクセル以内で同じか。</summary>
    internal static bool Same(RECT a, RECT b, int tolerance) =>
        Math.Abs(a.left - b.left) <= tolerance &&
        Math.Abs(a.top - b.top) <= tolerance &&
        Math.Abs(a.right - b.right) <= tolerance &&
        Math.Abs(a.bottom - b.bottom) <= tolerance;

    /// <summary>
    /// 矩形から、寄せている辺の側に自分の幅ぶんだけ切り出す。
    /// <para><c>ABM_QUERYPOS</c> への提案と、返ってきた矩形からの切り出しの両方で使う。</para>
    /// </summary>
    internal static RECT SliceWidth(RECT rect, DockEdge edge, int width)
    {
        if (edge == DockEdge.Left) rect.right = rect.left + width;
        else rect.left = rect.right - width;

        return rect;
    }

    /// <summary>
    /// <c>WM_WINDOWPOSCHANGING</c> を、AppBar と交渉して確定した矩形（<paramref name="confirmed"/>）
    /// へ押し戻すかどうかを判定する。
    /// <para>
    /// ピン留め中にシェルが「削った帯に重なる非 Topmost の窓」を押し出そうとするのを
    /// 打ち消すガード（不具合：ピン留め時に一瞬右へ飛ぶ）。Windows からの移動要求
    /// （<paramref name="x"/>／<paramref name="y"/>／<paramref name="cx"/>／<paramref name="cy"/>）が
    /// 確定値と違えば、確定値へ書き換える値を返す。
    /// </para>
    /// <para>
    /// <c>SWP_NOMOVE</c> が立っているときは位置を、<c>SWP_NOSIZE</c> が立っているときは
    /// 大きさを、それぞれ書き換えない（Windows がそもそも動かす気の無い軸には触れない）。
    /// </para>
    /// </summary>
    /// <returns>どこかを書き換えたら true。呼び出し側はこのときだけ <c>lParam</c> へ書き戻す。</returns>
    internal static bool TryGuardWindowPos(
        RECT confirmed, int x, int y, int cx, int cy, int flags,
        out int guardedX, out int guardedY, out int guardedCx, out int guardedCy)
    {
        guardedX = x;
        guardedY = y;
        guardedCx = cx;
        guardedCy = cy;

        var changed = false;

        if ((flags & SWP_NOMOVE) == 0 && (x != confirmed.left || y != confirmed.top))
        {
            guardedX = confirmed.left;
            guardedY = confirmed.top;
            changed = true;
        }

        if ((flags & SWP_NOSIZE) == 0 && (cx != confirmed.Width || cy != confirmed.Height))
        {
            guardedCx = confirmed.Width;
            guardedCy = confirmed.Height;
            changed = true;
        }

        return changed;
    }

    // ------------------------------------------------------------------
    // 起動時の復元と、画面の構成が変わったときの置き直し
    //
    // ログオン直後は、モニタの数・位置・倍率が数秒〜数十秒かけて変わることがある。
    // また、ノート PC は持ち出す先と戻る先で画面の構成がまるごと違う（実機：前日は
    // 物理 2880x1824・200% の単独、当日は 1920x1080 の主画面＋左に 1024x1280）。
    // 控えてあった位置が今の画面に無いとき、どこへ置くかをここで決める。
    //
    // 判断だけをここに寄せてある（Window や Win32 を呼ばない）。座標はすべて物理ピクセル。
    // ------------------------------------------------------------------

    /// <summary>窓が1枚の画面の上に、最低でもこれだけの幅で乗っていれば「手が届く」。</summary>
    internal const int MinVisibleWidth = 160;

    /// <summary>窓が1枚の画面の上に、最低でもこれだけの高さで乗っていれば「手が届く」。</summary>
    internal const int MinVisibleHeight = 80;

    /// <summary>
    /// 窓の上端は、画面の下端からこれだけ内側までに居ること。これより下だと、
    /// タイトルバー（掴んで動かす所）が画面の外で、引き戻せない。
    /// </summary>
    internal const int TitleBarReach = 40;

    /// <summary>
    /// 窓の上端が画面の上端より外へ出ていてよい量。最大化した窓は、見えない枠のぶん
    /// （Windows 11 で 8px）だけ上へはみ出す。
    /// </summary>
    internal const int TopTolerance = 32;

    /// <summary>主画面の番号。印が付いたものが無ければ先頭。1枚も無ければ -1。</summary>
    internal static int PrimaryIndex(IReadOnlyList<ScreenInfo> screens)
    {
        for (var i = 0; i < screens.Count; i++)
        {
            if (screens[i].IsPrimary) return i;
        }

        return screens.Count > 0 ? 0 : -1;
    }

    /// <summary>2つの矩形が重なっている面積。重なっていなければ 0。</summary>
    internal static long OverlapArea(RECT a, RECT b)
    {
        var width = Math.Min(a.right, b.right) - Math.Max(a.left, b.left);
        var height = Math.Min(a.bottom, b.bottom) - Math.Max(a.top, b.top);

        return width > 0 && height > 0 ? (long)width * height : 0;
    }

    /// <summary>
    /// 窓といちばん広く重なっている画面の番号。どの画面とも重なっていなければ -1。
    /// <para>
    /// 窓を持っていない（<c>null</c>）・潰れている（幅か高さが 0 以下）ときも -1。
    /// 画面をまたいでいれば、広いほうを採る（<c>MonitorFromWindow</c> の
    /// <c>MONITOR_DEFAULTTONEAREST</c> と同じ選び方）。
    /// </para>
    /// </summary>
    internal static int BestOverlapIndex(RECT? window, IReadOnlyList<ScreenInfo> screens)
    {
        if (window is not { } rect || rect.Width <= 0 || rect.Height <= 0) return -1;

        var best = -1;
        long bestArea = 0;

        for (var i = 0; i < screens.Count; i++)
        {
            var area = OverlapArea(rect, screens[i].Monitor);

            if (area <= bestArea) continue;

            best = i;
            bestArea = area;
        }

        return best;
    }

    /// <summary>
    /// 普通の窓が、どれかの画面に「手が届くほど」乗っているか。
    /// <para>
    /// 1枚の画面の上に、幅 <see cref="MinVisibleWidth"/>・高さ <see cref="MinVisibleHeight"/>
    /// （窓がそれより小さければ窓の大きさ）以上で乗っていて、上端が画面の中
    /// （タイトルバーを掴める所）にあれば true。
    /// </para>
    /// <para>
    /// 仮想画面全体の外接矩形に収めただけでは足りない。画面が L 字に並んでいると、
    /// 外接矩形の中にもどの画面でもない場所がある。
    /// </para>
    /// </summary>
    internal static bool IsReachable(RECT window, IReadOnlyList<ScreenInfo> screens)
    {
        if (window.Width <= 0 || window.Height <= 0) return false;

        foreach (var screen in screens)
        {
            var monitor = screen.Monitor;

            var width = Math.Min(window.right, monitor.right) - Math.Max(window.left, monitor.left);
            var height = Math.Min(window.bottom, monitor.bottom) - Math.Max(window.top, monitor.top);

            if (width < Math.Min(MinVisibleWidth, window.Width)) continue;
            if (height < Math.Min(MinVisibleHeight, window.Height)) continue;

            if (window.top < monitor.top - TopTolerance) continue;
            if (window.top > monitor.bottom - TitleBarReach) continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// 控えてあった窓の位置（DIP）を、物理ピクセルへ直す。<b>控えた位置の画面の倍率で</b>直す。
    /// <para>
    /// 控えてあるのは、窓が居た画面の倍率で割った値（DIP）。これを物理ピクセルへ戻す倍率は、
    /// 窓が居た画面のもの。ところが起動時は、窓をまだ出していないので、窓の倍率は主画面のものを
    /// 指している。倍率の違う2画面（ノート PC の 150% と、外付けの 100% など）で、主画面でない側に
    /// 置いていた窓は、主画面の倍率で戻すと、本来の場所から大きくずれて、乗っていない・
    /// 乗っているが違う場所、となる。
    /// </para>
    /// <para>
    /// 画面ごとに「その画面の倍率で戻したとき、その画面の中に収まるか」を調べて、
    /// いちばん収まるものを採る。収まる画面が無ければ（その画面がもう無い、など）
    /// <paramref name="fallbackScale"/>（窓の倍率）で戻す。今までと同じ。同じくらい収まる画面が
    /// 複数あるときは、窓の倍率と同じ画面を先にする（今までの結果を変えないため）。
    /// </para>
    /// </summary>
    /// <param name="screens">いまつながっている画面（<see cref="Screens.All"/>）。</param>
    /// <param name="fallbackScale">窓の倍率。収まる画面が無いときの代用。</param>
    internal static SavedWindowRect ResolveSavedWindow(
        double left, double top, double width, double height,
        IReadOnlyList<ScreenInfo> screens, double fallbackScale)
    {
        if (fallbackScale <= 0) fallbackScale = 1.0;

        var bestIndex = -1;
        var bestRatio = 0.0;
        var bestIsFallback = false;
        RECT bestRect = default;

        for (var i = 0; i < screens.Count; i++)
        {
            var scale = screens[i].Scale;
            var rect = ToPhysical(left, top, width, height, scale);

            if (rect.Width <= 0 || rect.Height <= 0) continue;

            var overlap = OverlapArea(rect, screens[i].Monitor);
            if (overlap <= 0) continue;

            // 窓のうち、その画面に乗っている割合。収まっていれば 1。画面の大きさは問わない
            var ratio = Math.Round(overlap / ((double)rect.Width * rect.Height), 2);
            var isFallback = Math.Abs(scale - fallbackScale) < 0.005;

            if (bestIndex >= 0)
            {
                if (ratio < bestRatio) continue;
                if (ratio == bestRatio && (!isFallback || bestIsFallback)) continue;
            }

            bestIndex = i;
            bestRatio = ratio;
            bestIsFallback = isFallback;
            bestRect = rect;
        }

        return bestIndex >= 0
            ? new SavedWindowRect(bestRect, bestIndex, screens[bestIndex].Scale)
            : new SavedWindowRect(ToPhysical(left, top, width, height, fallbackScale), -1, fallbackScale);
    }

    private static RECT ToPhysical(double left, double top, double width, double height, double scale) => new()
    {
        left = (int)Math.Round(left * scale),
        top = (int)Math.Round(top * scale),
        right = (int)Math.Round((left + width) * scale),
        bottom = (int)Math.Round((top + height) * scale),
    };

    /// <summary>
    /// 起動時（と、画面の構成が変わったとき）に、普通の窓を今の画面へ収める。
    /// <para>
    /// 手が届くほど乗っていれば、そのまま返す（<b>動かさない</b>）。そうでなければ
    /// 主画面の作業領域へ置き直す。画面より大きければ縮める。主画面に少しでも
    /// 掛かっていれば、そこから押し戻すだけ（動きを最小にする）。まったく掛かって
    /// いなければ、真ん中に置く。
    /// </para>
    /// </summary>
    internal static WindowFit RestoreWindow(RECT window, IReadOnlyList<ScreenInfo> screens)
    {
        if (screens.Count == 0)
        {
            return new WindowFit(window, false, -1, "画面の一覧を取れなかったので、そのままにした");
        }

        if (IsReachable(window, screens))
        {
            return new WindowFit(window, false, BestOverlapIndex(window, screens), "どれかの画面に十分に乗っているので、そのままにした");
        }

        var primary = PrimaryIndex(screens);
        var target = screens[primary];

        var onNothing = BestOverlapIndex(window, screens) < 0;
        var placed = FitInto(window, target.Work, keepNear: OverlapArea(window, target.Monitor) > 0);

        var reason = onNothing
            ? "どの画面にも乗っていないので、主画面へ置き直した"
            : "画面にわずかしか乗っていないので、主画面へ置き直した";

        return new WindowFit(placed, true, primary, reason);
    }

    /// <summary>
    /// 窓を <paramref name="area"/> の中へ収める。大きければ縮める。
    /// <paramref name="keepNear"/> が true なら今の位置から最小限だけ押し戻し、
    /// false なら真ん中に置く。
    /// </summary>
    private static RECT FitInto(RECT window, RECT area, bool keepNear)
    {
        var width = Math.Min(Math.Max(window.Width, 1), Math.Max(area.Width, 1));
        var height = Math.Min(Math.Max(window.Height, 1), Math.Max(area.Height, 1));

        var left = keepNear
            ? Math.Clamp(window.left, area.left, area.right - width)
            : area.left + (area.Width - width) / 2;

        var top = keepNear
            ? Math.Clamp(window.top, area.top, area.bottom - height)
            : area.top + (area.Height - height) / 2;

        return new RECT { left = left, top = top, right = left + width, bottom = top + height };
    }

    /// <summary>
    /// 画面端（スライド・ピン留め）の置き場所を決める。
    /// <para>
    /// 窓が乗っている画面があれば、その画面の同じ端（<b>動かさない</b>）。どの画面にも
    /// 乗っていなければ（控えていた位置の画面がもう無い）、主画面の同じ端。
    /// 帯の見張りもこの画面で張る。存在しない画面の端を見張り続けない。
    /// </para>
    /// <para>
    /// 端は作業領域から取る（タスクバーが寄せたい辺にあれば、その内側）。幅は
    /// <paramref name="widthDip"/> を画面の倍率で物理ピクセルに直す。
    /// </para>
    /// </summary>
    /// <param name="window">いまの窓の矩形。取れなければ <c>null</c>（主画面になる）。</param>
    internal static EdgeDecision DecideEdge(
        RECT? window, DockEdge edge, double widthDip, IReadOnlyList<ScreenInfo> screens)
    {
        if (screens.Count == 0)
        {
            return new EdgeDecision(-1, false, default, "画面の一覧を取れなかった");
        }

        var onScreen = BestOverlapIndex(window, screens);
        var index = onScreen >= 0 ? onScreen : PrimaryIndex(screens);
        var screen = screens[index];

        var widthPx = (int)Math.Round(widthDip * screen.Scale);
        var target = OverlayRect(screen.Work, edge, widthPx);

        var reason = onScreen >= 0
            ? "窓が乗っている画面の端へ置いた"
            : "保存されていた位置はどの画面にも乗っていないので、主画面の同じ端へ置いた";

        return new EdgeDecision(index, onScreen < 0, target, reason);
    }

    /// <summary>作業領域の、寄せている辺に、幅 <paramref name="widthPx"/> で置いた矩形。</summary>
    internal static RECT OverlayRect(RECT work, DockEdge edge, int widthPx)
    {
        var width = Math.Max(widthPx, 1);

        return edge == DockEdge.Left
            ? new RECT { left = work.left, top = work.top, right = work.left + width, bottom = work.bottom }
            : new RECT { left = work.right - width, top = work.top, right = work.right, bottom = work.bottom };
    }

    /// <summary>
    /// 画面の構成が同じか（順序は問わない）。モニタの数・位置・作業領域・倍率・主画面の
    /// どれかが違えば false。
    /// </summary>
    internal static bool SameLayout(IReadOnlyList<ScreenInfo> a, IReadOnlyList<ScreenInfo> b)
    {
        if (a.Count != b.Count) return false;

        var rest = b.ToList();

        foreach (var screen in a)
        {
            var found = rest.FindIndex(other => other.Equals(screen));

            if (found < 0) return false;

            rest.RemoveAt(found);
        }

        return true;
    }

    internal static string FormatRect(RECT rect) => $"({rect.left},{rect.top},{rect.right},{rect.bottom})";

    /// <summary>
    /// 画面の一覧を1行に。<c>startup</c> 行の <c>monitors=</c> と同じ並び
    /// （<c>[左,上,右,下]@横x縦DPI</c>）に、主画面の印だけ足してある。
    /// </summary>
    internal static string DescribeScreens(IReadOnlyList<ScreenInfo> screens) =>
        "[" + string.Join(" ", screens.Select(screen =>
            $"[{screen.Monitor.left},{screen.Monitor.top},{screen.Monitor.right},{screen.Monitor.bottom}]" +
            $"{(screen.IsPrimary ? "主" : string.Empty)}@{screen.Dpi}x{screen.Dpi}")) + "]";

    /// <summary>起動時の復元（画面端）の判断を、shell.log の1行にする。</summary>
    internal static string FormatEdgeRestoreLine(
        ShellMode mode, DockEdge edge, double dockWidth, RECT? saved,
        IReadOnlyList<ScreenInfo> screens, EdgeDecision decision)
    {
        var chosen = decision.ScreenIndex >= 0
            ? $"#{decision.ScreenIndex}{FormatRect(screens[decision.ScreenIndex].Monitor)}"
            : "なし";

        return
            $"startup-restore mode={mode} edge={edge} dockWidth={dockWidth:F1} " +
            $"saved={(saved is { } rect ? FormatRect(rect) : "取得不可")} " +
            $"screens={DescribeScreens(screens)} → screen={chosen} " +
            $"置き直し={(decision.Relocated ? "はい" : "いいえ")} " +
            $"置き場所={(decision.ScreenIndex >= 0 ? FormatRect(decision.Target) : "なし")} " +
            $"理由={decision.Reason}";
    }

    /// <summary>起動時の復元（普通の窓）の判断を、shell.log の1行にする。</summary>
    internal static string FormatWindowRestoreLine(
        RECT saved, IReadOnlyList<ScreenInfo> screens, WindowFit fit, SavedWindowRect? resolved = null) =>
        $"startup-restore mode=Window saved={FormatRect(saved)} screens={DescribeScreens(screens)} → " +
        $"screen={(fit.ScreenIndex >= 0 ? $"#{fit.ScreenIndex}" : "なし")} " +
        $"置き直し={(fit.Relocated ? "はい" : "いいえ")} placed={FormatRect(fit.Placed)} 理由={fit.Reason}" +
        (resolved is { } r
            ? $" 換算倍率={r.Scale:F2}({(r.ScreenIndex >= 0 ? $"画面#{r.ScreenIndex}" : "窓の倍率")})"
            : string.Empty);
}

/// <summary>
/// 1枚の画面。座標は物理ピクセル。
/// </summary>
/// <param name="Monitor">モニタ全体。</param>
/// <param name="Work">作業領域（タスクバーなどを除いた領域）。</param>
/// <param name="IsPrimary">主画面か。</param>
/// <param name="Dpi">実効の DPI（96 で 100%）。引けなかったときは窓の倍率で代用した値。</param>
internal readonly record struct ScreenInfo(RECT Monitor, RECT Work, bool IsPrimary, uint Dpi)
{
    /// <summary>倍率（100% で 1.0）。DPI が 0 のときは 1.0。</summary>
    internal double Scale => Dpi == 0 ? 1.0 : Dpi / 96.0;
}

/// <summary>控えてあった窓の位置を物理ピクセルへ直した結果。</summary>
/// <param name="Physical">物理ピクセルの矩形。</param>
/// <param name="ScreenIndex">直すのに使った倍率の画面の番号。収まる画面が無く、窓の倍率で直したときは -1。</param>
/// <param name="Scale">直すのに使った倍率。</param>
internal readonly record struct SavedWindowRect(RECT Physical, int ScreenIndex, double Scale);

/// <summary>普通の窓を今の画面へ収めた結果。</summary>
/// <param name="Placed">置いた矩形（物理ピクセル）。動かさなければ元のまま。</param>
/// <param name="Relocated">元の位置から動かしたか。</param>
/// <param name="ScreenIndex">乗っている（置いた）画面の番号。分からなければ -1。</param>
/// <param name="Reason">判断の理由（shell.log 用）。</param>
internal readonly record struct WindowFit(RECT Placed, bool Relocated, int ScreenIndex, string Reason);

/// <summary>画面端（スライド・ピン留め）の置き場所の判断。</summary>
/// <param name="ScreenIndex">寄せる画面の番号。画面の一覧が取れなければ -1。</param>
/// <param name="Relocated">窓が乗っていた画面が無く、主画面へ置き直したか。</param>
/// <param name="Target">寄せた矩形（物理ピクセル。作業領域の寄せている辺）。</param>
/// <param name="Reason">判断の理由（shell.log 用）。</param>
internal readonly record struct EdgeDecision(int ScreenIndex, bool Relocated, RECT Target, string Reason);
