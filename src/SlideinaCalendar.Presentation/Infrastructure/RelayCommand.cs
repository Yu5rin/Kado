using System.Windows.Input;

namespace SlideinaCalendar.Presentation.Infrastructure;

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
