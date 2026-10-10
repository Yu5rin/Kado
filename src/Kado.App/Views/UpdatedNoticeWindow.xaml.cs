using System.Diagnostics;
using System.Windows;
using Kado.Presentation.Update;

namespace Kado.App.Views;

/// <summary>
/// 更新したことの知らせ。変更点（リリース本文）を文字のまま出す。
/// <para>本文が取れていなかったときは「変更点はリリースのページをご覧ください」と、ページへの導線を出す。</para>
/// </summary>
public partial class UpdatedNoticeWindow : Window
{
    /// <summary>本文が無いときに出す文。</summary>
    internal const string NoNotesMessage = "変更点はリリースのページをご覧ください。";

    private readonly string _pageUrl;

    /// <param name="applied">更新した版（タグ・リリースのページ・本文）。</param>
    /// <param name="fallbackPageUrl">リリースのページが控えに無いときの行き先（タグから組み立てたもの）。</param>
    public UpdatedNoticeWindow(StagedUpdate applied, string fallbackPageUrl)
    {
        ArgumentNullException.ThrowIfNull(applied);

        InitializeComponent();

        TitleText.Text = $"Kado を {applied.Tag} に更新しました";

        NotesText.Text = applied.ReleaseNotes is { Length: > 0 } notes ? notes : NoNotesMessage;

        _pageUrl = applied.ReleaseUrl is { Length: > 0 } url ? url : fallbackPageUrl;

        // 行き先が許されない（組み立てられない）ときは、ボタンを出さない
        OpenPageButton.Visibility = ReleaseFeed.IsAllowedDownloadUrl(_pageUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        // 保存してあった値を無検証で開かない（更新の窓と同じ）
        if (!ReleaseFeed.IsAllowedDownloadUrl(_pageUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(_pageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "リリースのページを開けませんでした。ブラウザの設定を確かめるか、"
                + "次のアドレスをブラウザに貼って開いてください。\n" + _pageUrl;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
