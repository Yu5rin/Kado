using System.Windows.Input;

namespace Kado.Presentation.Infrastructure;

/// <summary>
/// 処理を直接渡せるコマンド。
/// <para>
/// <c>ICommand</c> は <c>System.Windows.Input</c> にあるが、WPF ではなく
/// 基底クラスライブラリの一部なので、UI に依存しないこのプロジェクトからも使える。
/// </para>
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    /// <summary>実行できるかどうかが変わったことを伝える。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>引数を取るコマンド。</summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    private readonly Action<T?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(Cast(parameter)) ?? true;

    public void Execute(object? parameter)
    {
        var value = Cast(parameter);
        if (canExecute?.Invoke(value) ?? true) _execute(value);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Cast(object? parameter) => parameter is T value ? value : default;
}

/// <summary>
/// 非同期の処理を渡せるコマンド。
/// <para>
/// <c>Action</c> に <c>async</c> のラムダを渡すと <c>async void</c> になり、
/// <b>中で起きた例外が誰にも届かないまま消える</b>。同期は通信を伴うので必ず失敗する
/// 場面があり、黙って何も起きないのがいちばん困る。
/// </para>
/// <para>
/// 走っている間は実行できなくする。同期のボタンを続けて押されても二重に走らせない。
/// </para>
/// </summary>
public sealed class AsyncRelayCommand(
    Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private readonly Func<Task> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    /// <summary>走っているか。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        IsRunning = true;
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // ここで受けないと、async void の例外としてアプリごと落ちる
            onError?.Invoke(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
