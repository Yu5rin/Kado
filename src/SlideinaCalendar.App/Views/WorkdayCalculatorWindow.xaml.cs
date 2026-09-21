using System.Windows;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 実働日計算パネル。
/// <para>数えるだけなので、閉じる以外の操作を置かない。</para>
/// </summary>
public partial class WorkdayCalculatorWindow : Window
{
    public WorkdayCalculatorWindow(WorkdayCalculatorViewModel calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);

        InitializeComponent();
        DataContext = calculator;
    }
}
