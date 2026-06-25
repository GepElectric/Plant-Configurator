using System.Collections.Generic;
using System.Windows;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

public partial class PushTargetsDialog : Window
{
    public PushTargetsDialog(IReadOnlyList<ScadaPushTarget> targets)
    {
        InitializeComponent();
        TargetsList.ItemsSource = targets;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
