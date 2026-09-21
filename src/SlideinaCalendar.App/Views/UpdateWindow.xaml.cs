using System.Diagnostics;
using System.Windows;
using SlideinaCalendar.App.Update;
using SlideinaCalendar.Presentation.Update;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 新しい版の案内と入れ替え。
/// <para>落とす → ハッシュを確かめる → 入れ替える → 再起動、までをここで行う。</para>
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updater;
    private readonly UpdateInfo _info;
    private readonly Action _shutdown;

    private CancellationTokenSource? _cancel;

    /// <summary>落としている最中か。連打と、閉じたときの中断の判断に使う。</summary>
    private bool _working;

    /// <summary>入れ替えている最中か。この間は閉じさせない。</summary>
    private bool _applying;

    public UpdateWindow(UpdateService updater, UpdateInfo info, Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(info);

        InitializeComponent();

        _updater = updater;
        _info = info;
        _shutdown = shutdown;

        VersionText.Text = $"いま {UpdateService.CurrentVersion} ／ 新しい版 {info.Version}";

        NotesText.Text = info.ReleaseNotes is { Length: > 0 } notes
            ? notes
            : "（何が変わったかの記載はありません）";

        // Program Files のような場所に置かれていると、自分では入れ替えられない
        if (!UpdateService.CanWriteToInstallDirectory(out var directory))
        {
            UpdateButton.IsEnabled = false;
            Say($"このフォルダには書き込めないので、自分では入れ替えられません（{directory}）。" +
                "リリースのページから手で差し替えてください。");
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        // 連打で2本目が走ると、同じファイルへ二重に書いて壊れる
        if (_working) return;

        _working = true;
        _cancel = new CancellationTokenSource();

        UpdateButton.IsEnabled = false;
        LaterButton.Content = "中止";
        Progress.Visibility = Visibility.Visible;
        Say("落としています…");

        try
        {
            var progress = new Progress<double>(v => Progress.Value = v);
            var downloaded = await _updater.DownloadAsync(_info, progress, _cancel.Token);

            Say("入れ替えています…");
            _applying = true;

            if (_updater.Apply(downloaded))
            {
                // 新しいほうがもう立ち上がっている。こちらは速やかに終わる
                _shutdown();
                return;
            }

            _applying = false;
            Say("入れ替えられませんでした。元の版のままです。リリースのページから手で差し替えてください。");
        }
        catch (OperationCanceledException)
        {
            Say("中止しました。");
        }
        catch (Exception ex)
        {
            Say($"更新できませんでした（{ex.Message}）。リリースのページから手で差し替えてください。");
        }
        finally
        {
            _working = false;
            _applying = false;

            Progress.Visibility = Visibility.Collapsed;
            LaterButton.Content = "閉じる";
            UpdateButton.IsEnabled = true;
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            _cancel?.Cancel();
            return;
        }

        Close();
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (_info.ReleaseUrl is not { Length: > 0 } url) return;

        // 応答の html_url を無検証で開かない。応答を差し替えられる立場なら、
        // ここに file: などを渡して ShellExecute させることもできてしまう
        if (!ReleaseFeed.IsAllowedDownloadUrl(url))
        {
            Say("リリースのページの行き先が正しくないため、開けませんでした。");
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 入れ替えの最中に閉じられると、中途半端な状態で残る
        if (_applying)
        {
            e.Cancel = true;
            return;
        }

        _cancel?.Cancel();
        base.OnClosing(e);
    }

    private void Say(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
