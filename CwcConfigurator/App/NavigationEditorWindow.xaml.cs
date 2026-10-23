using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly List<string> _screens = new();
    private readonly HashSet<string> _tiaScreens = new(StringComparer.OrdinalIgnoreCase);
    private ScadaNavButton? _buttonClipboard;
    private ScadaTopNavCard? _cardClipboard;
    private ScadaNavButton? _watched;

    public bool Applied { get; private set; }

    /// <summary>Screens offered for the selected button: those starting with its identity (prefix) first.</summary>
    public ObservableCollection<string> CardScreens { get; } = new();

    /// <summary>Screens for the side content (all screens).</summary>
    public ObservableCollection<string> AllScreens { get; } = new();

    public IReadOnlyList<string> FirstTopNavChoices { get; } = Enumerable.Range(1, ScadaNavigationModel.MaxCards).Select(i => i.ToString()).ToList();

    /// <param name="screenCatalog">Screens offered in the dropdowns.</param>
    /// <param name="tiaScreens">Screens that exist on the SCADA (from TIA); cards opening another screen are marked. Empty = not checked.</param>
    public NavigationEditorWindow(ScadaDatabase database, IReadOnlyList<string> screenCatalog, Func<Task<IReadOnlyList<string>>>? screenRefresh = null,
        IReadOnlyCollection<string>? tiaScreens = null)
    {
        InitializeComponent();
        _database = database;
        _screenRefresh = screenRefresh;
        _model = ScadaNavigationModel.FromDatabase(database);
        ButtonsList.ItemsSource = _model.Buttons;
        ButtonCountCombo.ItemsSource = Enumerable.Range(0, ScadaNavigationModel.MaxButtons + 1).ToList();
        ButtonCountCombo.SelectedItem = _model.ButtonCount;

        SetTiaScreens(tiaScreens);
        MergeScreens(screenCatalog);
        foreach (var button in _model.Buttons)
        {
            foreach (var card in button.Cards)
            {
                card.PropertyChanged += Card_PropertyChanged;
            }
        }

        CheckScreens();
        RefreshScreensButton.IsEnabled = _screenRefresh is not null;
        if (_model.Buttons.Count > 0)
        {
            ButtonsList.SelectedIndex = 0;
        }
    }

    private void SetTiaScreens(IEnumerable<string>? screens)
    {
        _tiaScreens.Clear();
        foreach (var screen in screens ?? [])
        {
            if (!string.IsNullOrWhiteSpace(screen))
            {
                _tiaScreens.Add(screen.Trim());
            }
        }
    }

    private void MergeScreens(IEnumerable<string>? screens)
    {
        var all = _screens.Concat(screens ?? [])
            .Concat(_tiaScreens)
            .Where(screen => !string.IsNullOrWhiteSpace(screen))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(screen => screen, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _screens.Clear();
        _screens.AddRange(all);
        AllScreens.Clear();
        AllScreens.Add("");
        foreach (var screen in _screens)
        {
            AllScreens.Add(screen);
        }

        UpdateCardScreens();
        ScreensInfo.Text = _tiaScreens.Count > 0 ? $"{_tiaScreens.Count} screen(s) on the SCADA." : $"{_screens.Count} screen(s) known (no TIA screen list).";
    }

    /// <summary>Card screen list for the selected button: screens with its prefix first, then the others.</summary>
    private void UpdateCardScreens()
    {
        var prefix = Selected?.Identity?.Trim() ?? "";
        CardScreens.Clear();
        CardScreens.Add("");
        foreach (var screen in _screens
                     .OrderBy(screen => prefix.Length > 0 && screen.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(screen => screen, StringComparer.OrdinalIgnoreCase))
        {
            CardScreens.Add(screen);
        }
    }

    /// <summary>Marks cards whose screen is not on the SCADA (only when the SCADA's screens are known).</summary>
    private void CheckScreens()
    {
        var unknown = 0;
        foreach (var card in _model.Buttons.SelectMany(button => button.Cards))
        {
            card.ScreenKnown = _tiaScreens.Count == 0 || string.IsNullOrWhiteSpace(card.Screen) || _tiaScreens.Contains(card.Screen.Trim());
            unknown += card.ScreenKnown ? 0 : 1;
        }

        CheckText.Text = unknown == 0 ? "" : $"{unknown} card(s) open a screen that is not on the SCADA (red).";
    }

    private void Card_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScadaTopNavCard.Screen))
        {
            CheckScreens();
        }
    }

    private void Button_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScadaNavButton.Identity))
        {
            UpdateCardScreens();
        }
    }

    /// <summary>Prime Volt SCADA: no icons on the top-nav cards.</summary>
    public void ShowCardIcons(bool show) => CardIconColumn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void PickButtonIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } button && IconPickerWindow.Pick(this, _database, ScadaGraphicLists.MainNav, button.IconId, $"main nav button {button.Index}") is { } value)
        {
            button.IconId = value;
        }
    }

    private void PickCardIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScadaTopNavCard card } && Selected is { } button
            && IconPickerWindow.Pick(this, _database, ScadaGraphicLists.TopNav, card.IconId, $"button {button.Index}, top-nav card {button.Cards.IndexOf(card) + 1}") is { } value)
        {
            card.IconId = value;
        }
    }

    private void ButtonCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ButtonCountCombo.SelectedItem is int count)
        {
            _model.ButtonCount = count;
        }
    }

    private ScadaNavButton? Selected => ButtonsList.SelectedItem as ScadaNavButton;

    private void ButtonsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // DetailPanel.DataContext follows ButtonsList.SelectedItem via XAML binding.
        if (_watched is not null)
        {
            _watched.PropertyChanged -= Button_PropertyChanged;
        }

        _watched = Selected;
        if (_watched is not null)
        {
            _watched.PropertyChanged += Button_PropertyChanged;
        }

        UpdateCardScreens();
    }

    private void RefreshCards() => CardsGrid.Items.Refresh();

    // Enumerate top-nav cards 1, 2, 3... in the row-header (left) column.
    private void CardsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    private ScadaTopNavCard NewCard(ScadaTopNavCard? from = null)
    {
        var card = from is null ? new ScadaTopNavCard() : CloneCard(from);
        card.PropertyChanged += Card_PropertyChanged;
        return card;
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

        button.Cards.Add(NewCard());
        CardsGrid.SelectedIndex = button.Cards.Count - 1;
        CardsGrid.ScrollIntoView(CardsGrid.SelectedItem);
    }

    private void CardRemove_Click(object sender, RoutedEventArgs e)
    {
        var button = Selected;
        var index = CardsGrid.SelectedIndex;
        if (button is null || index < 0 || index >= button.Cards.Count)
        {
            return;
        }

        button.Cards.RemoveAt(index);
        RefreshCards();
        CardsGrid.SelectedIndex = Math.Min(index, button.Cards.Count - 1);
        CheckScreens();
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

        button.Cards.Move(index, target);
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
        ButtonsList.SelectedIndex = target;
    }

    private static void SwapButtonContent(ScadaNavButton a, ScadaNavButton b)
    {
        var copy = CloneButton(a);
        CopyButtonInto(b, a);
        CopyButtonInto(copy, b);
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
        button.FirstTopNav = "1";
        button.SideContentScreen = "";
        button.Cards.Clear();
        CheckScreens();
    }

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
        foreach (var card in button.Cards)
        {
            card.PropertyChanged += Card_PropertyChanged;
        }

        CheckScreens();
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
        button.Cards.Insert(insertAt, NewCard(_cardClipboard));
        RefreshCards();
        CardsGrid.SelectedIndex = insertAt;
        CheckScreens();
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
            SetTiaScreens(screens);
            MergeScreens(screens);
            CheckScreens();
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
        var clone = new ScadaNavButton { Index = source.Index };
        CopyButtonInto(source, clone);
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
        var cards = source.Cards.Select(CloneCard).ToList();
        target.Cards.Clear();
        foreach (var card in cards)
        {
            target.Cards.Add(card);
        }
    }

    private static ScadaTopNavCard CloneCard(ScadaTopNavCard source) => new()
    {
        Screen = source.Screen,
        LabelEn = source.LabelEn,
        LabelOther = source.LabelOther,
        IconId = source.IconId,
        ScreenKnown = source.ScreenKnown
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
