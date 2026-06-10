using System.Windows;

namespace CwcConfigurator.App;

public partial class TiaProgressDialog : Window
{
    public TiaProgressDialog(string title, string status)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        StatusText.Text = status;
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }
}
