using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DescriptionTemplatesDialog : Window
{
    private readonly DescriptionTemplateCatalog _catalog;
    private readonly ObservableCollection<DescriptionTemplateEntry> _templates;
    private bool _loadingSelection;

    public bool Changed { get; private set; }

    public DescriptionTemplatesDialog(DescriptionTemplateCatalog catalog)
    {
        InitializeComponent();
        _catalog = catalog;
        _catalog.Templates ??= [];
        _templates = new ObservableCollection<DescriptionTemplateEntry>(_catalog.Templates.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase));
        ScopeFilter.ItemsSource = new[] { "All", "ALM", "PAR" };
        ScopeFilter.SelectedIndex = 0;
        ScopeText.ItemsSource = new[] { "ALM", "PAR", "Both" };
        PopulateGrid();
    }

    private void PopulateGrid(string? keepKey = null)
    {
        var search = (SearchText.Text ?? "").Trim();
        var scope = ScopeFilter.SelectedItem as string ?? "All";
        var selectedKey = keepKey
            ?? (TemplatesGrid.SelectedItem as DescriptionTemplateEntry)?.Key;

        _loadingSelection = true;
        TemplatesGrid.ItemsSource = _templates
            .Where(template => MatchesScope(template, scope))
            .Where(template => MatchesSearch(template, search))
            .OrderBy(template => template.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        TemplatesGrid.SelectedItem = TemplatesGrid.Items
            .OfType<DescriptionTemplateEntry>()
            .FirstOrDefault(template => template.Key.Equals(selectedKey ?? "", StringComparison.OrdinalIgnoreCase))
            ?? TemplatesGrid.Items.OfType<DescriptionTemplateEntry>().FirstOrDefault();
        _loadingSelection = false;
        LoadSelectedTemplate();
    }

    private static bool MatchesScope(DescriptionTemplateEntry template, string scope)
    {
        return scope == "All"
            || template.Scope.Equals("Both", StringComparison.OrdinalIgnoreCase)
            || template.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearch(DescriptionTemplateEntry template, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return string.Join(" ", template.Key, template.Scope, template.Notes, template.TextEnglish, template.TextOther)
            .Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadSelectedTemplate()
    {
        if (_loadingSelection)
        {
            return;
        }

        if (TemplatesGrid.SelectedItem is not DescriptionTemplateEntry item)
        {
            KeyText.Text = "";
            ScopeText.SelectedItem = null;
            EnglishText.Text = "";
            OtherText.Text = "";
            return;
        }

        KeyText.Text = item.Key ?? "";
        ScopeText.SelectedItem = string.IsNullOrWhiteSpace(item.Scope) ? "Both" : item.Scope;
        EnglishText.Text = item.TextEnglish ?? "";
        OtherText.Text = item.TextOther ?? "";
    }

    private void SaveEditor()
    {
        if (TemplatesGrid.SelectedItem is not DescriptionTemplateEntry item)
        {
            return;
        }

        item.Key = KeyText.Text.Trim();
        item.Scope = ScopeText.SelectedItem as string ?? "Both";
        item.TextEnglish = EnglishText.Text ?? "";
        item.TextOther = OtherText.Text ?? "";
        Changed = true;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor();
        var item = new DescriptionTemplateEntry { Key = CreateTemplateKey(), Scope = "Both" };
        _templates.Add(item);
        Changed = true;
        PopulateGrid(item.Key);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TemplatesGrid.SelectedItem is DescriptionTemplateEntry item)
        {
            _templates.Remove(item);
            Changed = true;
            PopulateGrid();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor();
        SaveCatalog();
        PopulateGrid(KeyText.Text.Trim());
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor();
        SaveCatalog();
        DialogResult = true;
    }

    private void SaveCatalog()
    {
        _catalog.Templates = _templates
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var item = group.First();
                item.Key = item.Key.Trim();
                item.Scope = string.IsNullOrWhiteSpace(item.Scope) ? "Both" : item.Scope.Trim();
                return item;
            })
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Changed = true;
    }

    private void TemplatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadSelectedTemplate();
    }

    private void SearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        PopulateGrid();
    }

    private void ScopeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateGrid();
    }

    private string CreateTemplateKey()
    {
        var index = _templates.Count + 1;
        string key;
        do
        {
            key = $"TEMPLATE_{index++}";
        } while (_templates.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));

        return key;
    }
}
