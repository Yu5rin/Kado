using System.Windows;

namespace Kado.App.Views;

/// <summary>取り込みの結果を見せる窓。</summary>
public partial class ReportWindow : Window
{
    public ReportWindow(string heading, string body)
    {
        InitializeComponent();

        Title = heading;
        Heading.Text = heading;
        Body.Text = body;
    }
}
