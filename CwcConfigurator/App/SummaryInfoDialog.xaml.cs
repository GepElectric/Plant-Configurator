using System.Windows;
using System.Windows.Controls;

namespace CwcConfigurator.App;

public sealed record SummaryInfoColumn(string Title, string Text);

public partial class SummaryInfoDialog : Window
{
    public SummaryInfoDialog(string title, string header, IReadOnlyList<SummaryInfoColumn> columns)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        ColumnsHost.ItemsSource = columns;
    }

    public void AddCrossReferenceTab(TiaCrossReferencePanel panel)
    {
        RootTabs.Items.Add(new TabItem
        {
            Header = "TIA cross-reference",
            Content = panel
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyOwnerRelativeSize();
    }

    private void ApplyOwnerRelativeSize()
    {
        var ownerWidth = Owner?.ActualWidth > 0 ? Owner.ActualWidth : SystemParameters.WorkArea.Width;
        var ownerHeight = Owner?.ActualHeight > 0 ? Owner.ActualHeight : SystemParameters.WorkArea.Height;
        Width = ownerWidth * 0.9;
        Height = ownerHeight * 0.9;

        if (Owner is null)
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2.0;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2.0;
            return;
        }

        if (Owner.WindowState == WindowState.Maximized)
        {
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Top;
            return;
        }

        Left = Owner.Left;
        Top = Owner.Top;
    }
}
