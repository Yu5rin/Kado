using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 添付のアップロード中は保存させない。上げ終わる前に閉じると、その添付が予定に載らずに落ちる。
/// </summary>
public class AttachmentUploadGuardTests
{
    private static readonly SourceChoice[] GoogleCalendars = [new("primary", "仕事")];

    /// <summary>終わりを手で決められるアップロードの代わり。</summary>
    private sealed class GatedUploader : IAttachmentUploader
    {
        public TaskCompletionSource<AttachmentUploadResult> Gate { get; } = new();

        public Task<AttachmentUploadResult> UploadAsync(
            string localFilePath, CancellationToken cancellationToken = default) => Gate.Task;
    }

    private static (EventEditorViewModel Vm, GatedUploader Uploader) Create()
    {
        var uploader = new GatedUploader();

        // 既存の予定を開いた形。開いた直後は変更なしで、IsDirty の変わり目が見える
        var stored = new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = new DateOnly(2026, 9, 24), CalendarId = "primary",
        };
        var vm = new EventEditorViewModel(
            stored, GoogleCalendars, uploader, new FakeFileDialogs { FileToPick = "/tmp/資料.pdf" }, null);

        return (vm, uploader);
    }

    [Fact]
    public async Task アップロード中は保存できず理由を出す()
    {
        var (vm, uploader) = Create();
        Assert.True(vm.CanSave);

        var pending = vm.AddAttachmentAsync();

        Assert.True(vm.IsUploadingAttachment);
        Assert.False(vm.CanSave);
        Assert.Equal("添付をアップロードしています。", vm.ValidationMessage);

        uploader.Gate.SetResult(AttachmentUploadResult.Success(
            new EventAttachment("file1", "https://drive.google.com/file/d/file1/view", "資料.pdf", "application/pdf")));
        await pending;

        Assert.False(vm.IsUploadingAttachment);
        Assert.True(vm.CanSave);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public async Task アップロード中は閉じる確認の対象になる()
    {
        var (vm, uploader) = Create();
        Assert.False(vm.IsDirty);

        var pending = vm.AddAttachmentAsync();

        Assert.True(vm.IsDirty);

        uploader.Gate.SetResult(AttachmentUploadResult.Failure("失敗"));
        await pending;
    }

    [Fact]
    public async Task アップロードが失敗しても保存できる状態へ戻る()
    {
        var (vm, uploader) = Create();
        Assert.False(vm.IsDirty);

        var pending = vm.AddAttachmentAsync();
        uploader.Gate.SetResult(AttachmentUploadResult.Failure("失敗"));
        await pending;

        Assert.True(vm.CanSave);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task アップロード中の変更通知でCanSaveも出し直す()
    {
        var (vm, uploader) = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var pending = vm.AddAttachmentAsync();
        uploader.Gate.SetResult(AttachmentUploadResult.Failure("失敗"));
        await pending;

        Assert.Contains(nameof(EventEditorViewModel.CanSave), raised);
        Assert.Contains(nameof(EventEditorViewModel.ValidationMessage), raised);
    }
}
