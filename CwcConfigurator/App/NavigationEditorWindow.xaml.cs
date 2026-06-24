using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

public partial class NavigationEditorWindow : Window
{
    private readonly ScadaDatabase _database;
    private readonly ScadaNavigationModel _model;

    public bool Applied { get; private set; }

    public NavigationEditorWindow(ScadaDatabase database, IReadOnlyList<string> screenCatalog)
    {
        InitializeComponent();
        _database = database;
        _model = ScadaNavigationModel.FromDatabase(database);
        ButtonsList.ItemsSource = _model.Buttons;

        var screens = new List<string> { "" };
        screens.AddRange((screenCatalog ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)));
        CardScreenColumn.ItemsSource = screens;
        SideContentCombo.ItemsSource = screens;

        if (_model.Buttons.Count > 0)
        {
            ButtonsList.SelectedIndex = 0;
        }
    }

    private ScadaNavButton? Selected => ButtonsList.SelectedItem as ScadaNavButton;

    private void ButtonsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // DetailPanel.DataContext follows ButtonsList.SelectedItem via XAML binding.
    }

    private void RefreshButtons() => ButtonsList.Items.Refresh();

    private void RefreshCards() => CardsGrid.Items.Refresh();

    private void RebindDetail()
    {
        // Force the TwoWay-bound detail fields to reload after we mutate the object in code.
        var index = ButtonsList.SelectedIndex;
        ButtonsList.SelectedIndex = -1;
        ButtonsList.SelectedIndex = index;
    }

    private void CardAdd_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        if (button is null)
        {
            return;
        }

        if (button.Cards.Count >= ScadaNavigationModel.MaxCards)
        {
            MessageBox.Show(this, $"A button can hold at most {ScadaNavigationModel.MaxCards} top-nav cards.", "Navigation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        button.Cards.Add(new ScadaTopNavCard());
        RefreshCards();
        RefreshButtons();
        CardsGrid.SelectedIndex = button.Cards.Count - 1;
    }

    private void CardRemove_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        var index = CardsGrid.SelectedIndex;
        if (button is null || index < 0)
        {
            return;
        }

        button.Cards.RemoveAt(index);
        RefreshCards();
        RefreshButtons();
        CardsGrid.SelectedIndex = System.Math.Min(index, button.Cards.Count - 1);
    }

    private void CardUp_Click(object sender, RoutedEventArgs e) => MoveCard(-1);

    private void CardDown_Click(object sender, RoutedEventArgs e) => MoveCard(1);

    private void MoveCard(int direction)
    {
        var button = Selected;
        if (button is null)
        {
            return;
        }

        var index = CardsGrid.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= button.Cards.Count)
        {
            return;
        }

        (button.Cards[index], button.Cards[target]) = (button.Cards[target], button.Cards[index]);
        RefreshCards();
        CardsGrid.SelectedIndex = target;
    }

    private void ButtonUp_Click(object sender, RoutedEventArgs e) => MoveButton(-1);

    private void ButtonDown_Click(object sender, RoutedEventArgs e) => MoveButton(1);

    private void MoveButton(int direction)
    {
        var index = ButtonsList.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _model.Buttons.Count)
        {
            return;
        }

        // Buttons are fixed slots (1..16), so reordering swaps their content.
        SwapButtonContent(_model.Buttons[index], _model.Buttons[target]);
        RefreshButtons();
        ButtonsList.SelectedIndex = target;
    }

    private static void SwapButtonContent(ScadaNavButton a, ScadaNavButton b)
    {
        (a.Identity, b.Identity) = (b.Identity, a.Identity);
        (a.LabelEn, b.LabelEn) = (b.LabelEn, a.LabelEn);
        (a.LabelOther, b.LabelOther) = (b.LabelOther, a.LabelOther);
        (a.IconId, b.IconId) = (b.IconId, a.IconId);
        (a.LayoutShape, b.LayoutShape) = (b.LayoutShape, a.LayoutShape);
        (a.FirstTopNav, b.FirstTopNav) = (b.FirstTopNav, a.FirstTopNav);
        (a.SideContentScreen, b.SideContentScreen) = (b.SideContentScreen, a.SideContentScreen);

        var moved = a.Cards.ToList();
        a.Cards.Clear();
        a.Cards.AddRange(b.Cards);
        b.Cards.Clear();
        b.Cards.AddRange(moved);
    }

    private void ButtonClear_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        if (button is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"Clear button {button.Index} (identity, labels, icon, layout and all its top-nav cards)?",
                "Clear button", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        button.Identity = "";
        button.LabelEn = "";
        button.LabelOther = "";
        button.IconId = "0";
        button.LayoutShape = 0;
        button.SideContentScreen = "";
        button.Cards.Clear();

        RefreshButtons();
        RefreshCards();
        RebindDetail();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        CardsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _model.ApplyTo(_database);
        Applied = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
