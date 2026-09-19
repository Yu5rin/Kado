using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SlideinaCalendar.Presentation.Infrastructure;

/// <summary>
/// 値が変わったことを通知できるオブジェクト。
/// <para>
/// MVVM のフレームワークは入れていない。必要なのは通知と コマンドだけで、
/// どちらも .NET の標準インターフェイスで足りるため。依存は最小限にする方針
/// （要件書 1.2）に沿う。
/// </para>
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>値が変わったときだけ差し替えて通知する。</summary>
    /// <returns>実際に変わったら true。</returns>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(propertyName);
        return true;
    }

    /// <summary>計算で決まるプロパティなど、外から通知したいとき。</summary>
    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>まとめて通知する。</summary>
    protected void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames) Raise(name);
    }
}
