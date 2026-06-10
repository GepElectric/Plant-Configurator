using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DbStringRowConfigDialog : Window
{
    private readonly List<FieldEditor> _editors = [];
    private readonly Dictionary<string, TextBox> _textBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly DescriptionTemplateCatalog? _templates;
    private readonly string _templateScope;
    private readonly CheckBox _applySystemEnglishToStructureCheck = new()
    {
        Content = "Apply system English name to whole structure",
        Margin = new Thickness(0, 0, 0, 8),
        Visibility = Visibility.Collapsed
    };

    private readonly CheckBox _applySystemOtherToStructureCheck = new()
    {
        Content = "Apply system other name to whole structure",
        Margin = new Thickness(0, 0, 0, 8),
        Visibility = Visibility.Collapsed
    };

    public IReadOnlyDictionary<string, string> Values => _editors.ToDictionary(editor => editor.Key, editor => editor.Value);
    public bool ApplyToStructure => ApplySystemEnglishToStructure || ApplySystemOtherToStructure;
    public bool ApplySystemEnglishToStructure => _applySystemEnglishToStructureCheck.IsChecked == true;
    public bool ApplySystemOtherToStructure => _applySystemOtherToStructureCheck.IsChecked == true;
    public string AppliedTemplateKey { get; private set; } = "";

    public DbStringRowConfigDialog(
        string title,
        IEnumerable<DbStringRowConfigField> fields,
        bool showApplyToStructure = false,
        DescriptionTemplateCatalog? templates = null,
        string templateScope = "")
    {
        InitializeComponent();
        Title = title;
        _templates = templates;
        _templateScope = templateScope;
        ApplyToStructureCheck.Visibility = Visibility.Collapsed;
        if (ApplyToStructureCheck.Parent is Panel originalParent)
        {
            originalParent.Children.Remove(ApplyToStructureCheck);
        }

        var rowIndex = 0;
        var englishApplyInserted = false;
        var otherApplyInserted = false;
        foreach (var field in fields)
        {
            FieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = field.Label,
                Margin = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, rowIndex);
            FieldsGrid.Children.Add(label);

            Control editorControl;
            FieldEditor editor;
            if (field.Options.Count > 0)
            {
                var combo = new ComboBox
                {
                    ItemsSource = field.Options,
                    DisplayMemberPath = nameof(DbValidationOption.Text),
                    SelectedValuePath = nameof(DbValidationOption.Value),
                    SelectedValue = field.Value,
                    IsEditable = false,
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                editorControl = combo;
                editor = new FieldEditor(field.Key, () => combo.SelectedValue?.ToString() ?? "");
            }
            else
            {
                var isDescription = field.Key is "DescriptionEnglish" or "DescriptionOther";
                var textBox = new TextBox
                {
                    Text = field.Value,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    MinHeight = isDescription ? 120 : 30,
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = isDescription ? VerticalAlignment.Top : VerticalAlignment.Center,
                    VerticalScrollBarVisibility = isDescription ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
                };
                editorControl = textBox;
                editor = new FieldEditor(field.Key, () => textBox.Text);
                _textBoxes[field.Key] = textBox;
            }

            Grid.SetRow(editorControl, rowIndex);
            Grid.SetColumn(editorControl, 1);
            FieldsGrid.Children.Add(editorControl);
            _editors.Add(editor);
            rowIndex++;

            if (showApplyToStructure && !englishApplyInserted && field.Key == "SystemNameEnglish")
            {
                InsertApplyToStructureCheck(rowIndex, _applySystemEnglishToStructureCheck);
                rowIndex++;
                englishApplyInserted = true;
            }

            if (showApplyToStructure && !otherApplyInserted && field.Key == "SystemNameOther")
            {
                InsertApplyToStructureCheck(rowIndex, _applySystemOtherToStructureCheck);
                rowIndex++;
                otherApplyInserted = true;
            }
        }

        if (showApplyToStructure && !englishApplyInserted)
        {
            InsertApplyToStructureCheck(rowIndex, _applySystemEnglishToStructureCheck);
            rowIndex++;
        }

        if (showApplyToStructure && !otherApplyInserted)
        {
            InsertApplyToStructureCheck(rowIndex, _applySystemOtherToStructureCheck);
        }

        if (_templates is not null
            && _textBoxes.ContainsKey(nameof(DbStringRow.DescriptionEnglish))
            && _textBoxes.ContainsKey(nameof(DbStringRow.DescriptionOther)))
        {
            TemplatePanel.Visibility = Visibility.Visible;
            RefreshTemplateCombo();
        }
    }

    private void InsertApplyToStructureCheck(int rowIndex, CheckBox checkBox)
    {
        FieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        checkBox.Visibility = Visibility.Visible;
        Grid.SetRow(checkBox, rowIndex);
        Grid.SetColumn(checkBox, 1);
        FieldsGrid.Children.Add(checkBox);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void ApplyTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateCombo.SelectedItem is not DescriptionTemplateEntry template)
        {
            return;
        }

        if (_textBoxes.TryGetValue(nameof(DbStringRow.DescriptionEnglish), out var englishBox))
        {
            englishBox.Text = template.TextEnglish;
        }

        if (_textBoxes.TryGetValue(nameof(DbStringRow.DescriptionOther), out var otherBox))
        {
            otherBox.Text = template.TextOther;
        }

        if (_textBoxes.TryGetValue(nameof(DbStringRow.DescriptionTemplateKey), out var keyBox))
        {
            keyBox.Text = template.Key;
        }

        AppliedTemplateKey = template.Key;
    }

    private void AddCurrentTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_templates is null)
        {
            return;
        }

        var key = PromptForTemplateKey(CreateTemplateKey());
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var existing = _templates.Templates.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new DescriptionTemplateEntry { Key = key, Scope = string.IsNullOrWhiteSpace(_templateScope) ? "Both" : _templateScope };
            _templates.Templates.Add(existing);
        }

        existing.TextEnglish = _textBoxes.GetValueOrDefault(nameof(DbStringRow.DescriptionEnglish))?.Text ?? "";
        existing.TextOther = _textBoxes.GetValueOrDefault(nameof(DbStringRow.DescriptionOther))?.Text ?? "";
        if (_textBoxes.TryGetValue(nameof(DbStringRow.DescriptionTemplateKey), out var keyBox))
        {
            keyBox.Text = existing.Key;
        }

        AppliedTemplateKey = existing.Key;

        RefreshTemplateCombo();
        TemplateCombo.SelectedItem = existing;
    }

    private void RefreshTemplateCombo()
    {
        if (_templates is null)
        {
            return;
        }

        TemplateCombo.ItemsSource = _templates.ForScope(_templateScope).ToList();
    }

    private string CreateTemplateKey()
    {
        var index = _templates?.Templates.Count + 1 ?? 1;
        string key;
        do
        {
            key = $"{(string.IsNullOrWhiteSpace(_templateScope) ? "DESC" : _templateScope)}_TEMPLATE_{index++}";
        } while (_templates?.Templates.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) == true);

        return key;
    }

    private string PromptForTemplateKey(string defaultValue)
    {
        var dialog = new Window
        {
            Title = "Description template",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock { Text = "Template key", Margin = new Thickness(0, 0, 0, 6) };
        var textBox = new TextBox
        {
            Text = defaultValue,
            MinWidth = 340,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 82, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 82, IsCancel = true };
        ok.Click += (_, _) => dialog.DialogResult = true;

        Grid.SetRow(label, 0);
        Grid.SetRow(textBox, 1);
        Grid.SetRow(buttons, 2);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(label);
        root.Children.Add(textBox);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };
        return dialog.ShowDialog() == true ? textBox.Text.Trim() : "";
    }

    private sealed record FieldEditor(string Key, Func<string> Read)
    {
        public string Value => Read();
    }
}

public sealed record DbStringRowConfigField(string Key, string Label, string Value, IReadOnlyList<DbValidationOption> Options);
