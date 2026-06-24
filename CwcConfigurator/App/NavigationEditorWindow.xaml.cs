using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

public partial class NavigationEditorWindow : Window
{
    private readonly ScadaDatabase _database;
    private readonly ScadaNavigationModel _model;
    private readonly Func<Task<IReadOnlyList<string>>>? _screenRefresh;
    private readonly List<string> _screens = new() { "" };
    private ScadaNavButton? _buttonClipboard;
    private ScadaTopNavCard? _cardClipboard;

    public bool Applied { get; private set; }

    public NavigationEditorWindow(ScadaDatabase database, IReadOnlyList<string> screenCatalog, Func<Task<IReadOnlyList<string>>>? screenRefresh = null)
    {
        InitializeComponent();
        _database = database;
        _screenRefresh = screenRefresh;
        _model = ScadaNavigationModel.FromDatabase(database);
        ButtonsList.ItemsSource = _model.Buttons;

        MergeScreens(screenCatalog);
        ApplyScreenCatalog();

        RefreshScreensButton.IsEnabled = _screenRefresh is not null;
        if (_model.Buttons.Count > 0)
        {
            ButtonsList.SelectedIndex = 0;
        }
    }

    private void MergeScreens(IEnumerable<string>? screens)
    {
        var rest = _screens.Where(s => !string.IsNullOrWhiteSpace(s))
            .Concat((screens ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _screens.Clear();
        _screens.Add("");
        _screens.AddRange(rest);
    }

    private void ApplyScreenCatalog()
    {
        CardScreenColumn.ItemsSource = null;
        CardScreenColumn.ItemsSource = _screens;
        SideContentCombo.ItemsSource = _screens;
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

    private void DetailField_LostFocus(object sender, RoutedEventArgs e) => RefreshButtons();

    private void ButtonCopy_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        if (button is null)
        {
            return;
        }

        _buttonClipboard = CloneButton(button);
        Title = $"Configure navigation  —  copied button {button.Index}";
    }

    private void ButtonPaste_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        if (button is null || _buttonClipboard is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"Overwrite button {button.Index} with the copied button (all settings and top-nav cards)?",
                "Paste button", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        CopyButtonInto(_buttonClipboard, button);
        RefreshButtons();
        RefreshCards();
        RebindDetail();
    }

    private void CardCopy_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        var index = CardsGrid.SelectedIndex;
        if (button is null || index < 0 || index >= button.Cards.Count)
        {
            return;
        }

        _cardClipboard = CloneCard(button.Cards[index]);
        Title = "Configure navigation  —  copied card";
    }

    private void CardPaste_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        if (button is null || _cardClipboard is null)
        {
            return;
        }

        if (button.Cards.Count >= ScadaNavigationModel.MaxCards)
        {
            MessageBox.Show(this, $"A button can hold at most {ScadaNavigationModel.MaxCards} top-nav cards.", "Navigation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var insertAt = CardsGrid.SelectedIndex >= 0 ? CardsGrid.SelectedIndex + 1 : button.Cards.Count;
        button.Cards.Insert(insertAt, CloneCard(_cardClipboard));
        RefreshCards();
        RefreshButtons();
        CardsGrid.SelectedIndex = insertAt;
    }

    private async void RefreshScreens_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRefresh is null)
        {
            return;
        }

        try
        {
            RefreshScreensButton.IsEnabled = false;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var screens = await _screenRefresh();
            MergeScreens(screens);
            ApplyScreenCatalog();
            ScreensInfo.Text = $"{_screens.Count(s => !string.IsNullOrWhiteSpace(s))} screen(s) available.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Refresh screens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshScreensButton.IsEnabled = true;
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private static ScadaNavButton CloneButton(ScadaNavButton source)
    {
        var clone = new ScadaNavButton
        {
            Index = source.Index,
            Identity = source.Identity,
            LabelEn = source.LabelEn,
            LabelOther = source.LabelOther,
            IconId = source.IconId,
            LayoutShape = source.LayoutShape,
            FirstTopNav = source.FirstTopNav,
            SideContentScreen = source.SideContentScreen
        };
        foreach (var card in source.Cards)
        {
            clone.Cards.Add(CloneCard(card));
        }

        return clone;
    }

    private static void CopyButtonInto(ScadaNavButton source, ScadaNavButton target)
    {
        target.Identity = source.Identity;
        target.LabelEn = source.LabelEn;
        target.LabelOther = source.LabelOther;
        target.IconId = source.IconId;
        target.LayoutShape = source.LayoutShape;
        target.FirstTopNav = source.FirstTopNav;
        target.SideContentScreen = source.SideContentScreen;
        target.Cards.Clear();
        foreach (var card in source.Cards)
        {
            target.Cards.Add(CloneCard(card));
        }
    }

    private static ScadaTopNavCard CloneCard(ScadaTopNavCard source) => new()
    {
        Screen = source.Screen,
        LabelEn = source.LabelEn,
        LabelOther = source.LabelOther,
        IconId = source.IconId
    };

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
