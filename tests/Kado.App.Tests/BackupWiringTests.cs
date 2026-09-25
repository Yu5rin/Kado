using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// App.xaml.cs の配線を、アプリを起動せずに検査する。
/// <para>
/// <c>MainViewModel</c> は本体を持たない口（<c>SaveBackup</c> / <c>RestoreBackup</c>、
/// どちらも <c>Action&lt;string&gt;?</c>）をいくつか持ち、App 側が組み立てるときに
/// 実装を差し込む。ここを結び忘れると、ビルドも既存のテストも通ったまま
/// <b>実機でだけ</b>「バックアップ」「復元」が動かない――実際に、この2つが
/// まるごと配線漏れになっていたことがある（<c>SaveBackup</c> / <c>RestoreBackup</c> に
/// 代入している箇所が src 内に1つも無かった）。
/// </para>
/// <para>
/// App プロジェクトは <c>net10.0-windows</c> で、この <c>net10.0</c> のテストからは
/// 参照できない（<c>XamlResourceTests</c> と同じ理由。実際に試すと NU1201 で
/// 復元が失敗する）。実行して確かめる代わりに、<c>XamlResourceTests</c> と同じ
/// やり方で、ソースを読んで「結んでいる行が実在するか」を見る。
/// </para>
/// </summary>
public class BackupWiringTests
{
    [Fact]
    public void SaveBackupはDatabaseBackupへ結んである()
    {
        Assert.Matches(
            new Regex(@"\bmain\.SaveBackup\s*=.*?DatabaseBackup\.SaveTo", RegexOptions.Singleline),
            AppSource);
    }

    [Fact]
    public void RestoreBackupは復元処理へ結んである()
    {
        Assert.Matches(new Regex(@"\bmain\.RestoreBackup\s*=\s*RestoreAndRestart\b"), AppSource);
    }

    [Fact]
    public void RestoreCommandのCanExecuteは結んだあとに立て直している()
    {
        // RelayCommand は CommandManager に乗っていない（docs/README.md の
        // 「WPF で踏んだ落とし穴」）。RestoreBackup をあとから入れても、
        // RaiseCanExecuteChanged を呼ばないと「復元」ボタンが無効のまま戻らない
        Assert.Matches(
            new Regex(
                @"main\.RestoreBackup\s*=.*?main\.RestoreCommand\.RaiseCanExecuteChanged\(\)",
                RegexOptions.Singleline),
            AppSource);
    }

    // ------------------------------------------------------------------

    /// <summary>App プロジェクトの場所。テストの出力先から上へたどって探す。</summary>
    private static string AppDirectory { get; } = Locate();

    // AppDirectory より後ろに置くこと。静的フィールドは宣言順に初期化されるので、
    // 先に置くと AppDirectory がまだ null のまま Path.Combine に渡ってしまう
    private static string AppSource { get; } =
        File.ReadAllText(Path.Combine(AppDirectory, "App.xaml.cs"));

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Kado.App");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/Kado.App が見つかりません。");
    }
}
