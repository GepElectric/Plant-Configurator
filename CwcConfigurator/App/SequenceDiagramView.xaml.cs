using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class SequenceDiagramView : UserControl
{
    private static readonly List<ConditionCountOption> ConditionCountOptions =
    [
        new(1, "1"),
        new(2, "2"),
        new(4, "4"),
        new(8, "8")
    ];

    private static readonly List<ConditionCountOption> StepCountOptions =
    [
        new(1, "1"),
        new(2, "2"),
        new(3, "3"),
        new(4, "4"),
        new(5, "5"),
        new(6, "6"),
        new(7, "7"),
        new(8, "8"),
        new(9, "9"),
        new(10, "10")
    ];

    private static readonly List<GateOption> GateOptions =
    [
        new(0, "AND"),
        new(1, "OR"),
        new(2, "XOR"),
        new(3, "XNOR")
    ];

    private static readonly List<InvertOption> InvertOptions =
    [
        new(false, "direct"),
        new(true, "NOT")
    ];

    private static readonly List<ConditionCountOption> CommandCountOptions =
    [
        new(1, "1"),
        new(2, "2"),
        new(3, "3"),
        new(4, "4"),
        new(5, "5"),
        new(6, "6"),
        new(7, "7"),
        new(8, "8")
    ];

    private static readonly List<OnOffOption> OnOffOptions =
    [
        new(false, "0 - OFF"),
        new(true, "1 - ON")
    ];

    private bool _updatingConditionCount;
    private bool _updatingCommandCount;
    private bool _updatingStepCount;

    public static readonly DependencyProperty SequenceProperty = DependencyProperty.Register(
        nameof(Sequence),
        typeof(SequenceDefinition),
        typeof(SequenceDiagramView),
        new PropertyMetadata(null, OnSequenceChanged));

    public static readonly DependencyProperty ControlStatusRowsProperty = DependencyProperty.Register(
        nameof(ControlStatusRows),
        typeof(IEnumerable<DbStringRow>),
        typeof(SequenceDiagramView),
        new PropertyMetadata(Array.Empty<DbStringRow>(), OnReferenceRowsChanged));

    public static readonly DependencyProperty CommandRowsProperty = DependencyProperty.Register(
        nameof(CommandRows),
        typeof(IEnumerable<DbStringRow>),
        typeof(SequenceDiagramView),
        new PropertyMetadata(Array.Empty<DbStringRow>(), OnReferenceRowsChanged));

    public static readonly DependencyProperty AllSequencesProperty = DependencyProperty.Register(
        nameof(AllSequences),
        typeof(IEnumerable<SequenceDefinition>),
        typeof(SequenceDiagramView),
        new PropertyMetadata(Array.Empty<SequenceDefinition>(), OnAllSequencesChanged));

    public SequenceDiagramView()
    {
        InitializeComponent();
        StepCountCombo.ItemsSource = StepCountOptions;
        StepCountCombo.SelectedValuePath = nameof(ConditionCountOption.Value);
        StepCountCombo.DisplayMemberPath = nameof(ConditionCountOption.Display);
        ConditionCountCombo.ItemsSource = ConditionCountOptions;
        ConditionCountCombo.SelectedValuePath = nameof(ConditionCountOption.Value);
        ConditionCountCombo.DisplayMemberPath = nameof(ConditionCountOption.Display);
        ConditionCountCombo.SelectedValue = 8;
        CommandCountCombo.ItemsSource = CommandCountOptions;
        CommandCountCombo.SelectedValuePath = nameof(ConditionCountOption.Value);
        CommandCountCombo.DisplayMemberPath = nameof(ConditionCountOption.Display);
        CommandCountCombo.SelectedValue = 1;
        CommandOnOffColumn.ItemsSource = OnOffOptions;
    }

    public SequenceDefinition? Sequence
    {
        get => (SequenceDefinition?)GetValue(SequenceProperty);
        set => SetValue(SequenceProperty, value);
    }

    public IEnumerable<DbStringRow> ControlStatusRows
    {
        get => (IEnumerable<DbStringRow>)GetValue(ControlStatusRowsProperty);
        set => SetValue(ControlStatusRowsProperty, value);
    }

    public IEnumerable<DbStringRow> CommandRows
    {
        get => (IEnumerable<DbStringRow>)GetValue(CommandRowsProperty);
        set => SetValue(CommandRowsProperty, value);
    }

    public IEnumerable<SequenceDefinition> AllSequences
    {
        get => (IEnumerable<SequenceDefinition>)GetValue(AllSequencesProperty);
        set => SetValue(AllSequencesProperty, value);
    }

    private static void OnSequenceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((SequenceDiagramView)dependencyObject).BindSequence();
    }

    private static void OnReferenceRowsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((SequenceDiagramView)dependencyObject).BindReferenceOptions();
    }

    private static void OnAllSequencesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((SequenceDiagramView)dependencyObject).RefreshStepTools();
    }

    private void BindSequence()
    {
        if (Sequence is null)
        {
            StepsList.ItemsSource = null;
            CommandSlotsGrid.ItemsSource = null;
            RefreshStepTools();
            StepCountCombo.SelectedValue = null;
            SequenceTitleText.Text = "";
            SelectedStepText.Text = "";
            ConditionDiagramGrid.Children.Clear();
            return;
        }

        Sequence.EnsureDefaults();
        SequenceTitleText.Text = $"{Sequence.Title} sequence";
        _updatingStepCount = true;
        StepCountCombo.SelectedValue = Math.Clamp(Sequence.LastStepNumber, 1, 10);
        _updatingStepCount = false;
        RefreshStepList(keepSelection: false);
        RefreshStepTools();
        BindSelectedStep();
    }

    private void RefreshStepList(bool keepSelection)
    {
        if (Sequence is null)
        {
            return;
        }

        var selectedStepNumber = keepSelection && StepsList.SelectedItem is SequenceStepConfig selected
            ? selected.StepNumber
            : 1;
        var visibleSteps = Sequence.VisibleSteps.ToList();
        StepsList.ItemsSource = visibleSteps;
        StepsList.SelectedItem = visibleSteps.FirstOrDefault(step => step.StepNumber == selectedStepNumber)
                                 ?? visibleSteps.FirstOrDefault();
        RefreshStepTools();
    }

    private void StepCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingStepCount || Sequence is null)
        {
            return;
        }

        if (StepCountCombo.SelectedValue is int selectedValue)
        {
            Sequence.LastStepNumber = Math.Clamp(selectedValue, 1, 10);
        }

        RefreshStepList(keepSelection: true);
        BindSelectedStep();
    }

    private void BindReferenceOptions()
    {
        CommandColumn.ItemsSource = BuildOptions(CommandRows);
        RenderConditionDiagram();
    }

    private static List<SequenceReferenceOption> BuildOptions(IEnumerable<DbStringRow>? rows)
    {
        return (rows ?? [])
            .OrderBy(row => row.Id)
            .Select(row => new SequenceReferenceOption(row.Id, FormatOption(row)))
            .ToList();
    }

    private static string FormatOption(DbStringRow row)
    {
        var text = string.IsNullOrWhiteSpace(row.PlcName)
            ? row.NameEnglish
            : row.PlcName;
        return string.IsNullOrWhiteSpace(text)
            ? row.Id.ToString()
            : $"{row.Id} - {text}";
    }

    private void StepsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BindSelectedStep();
    }

    private void BindSelectedStep()
    {
        if (StepsList.SelectedItem is not SequenceStepConfig step)
        {
            CommandSlotsGrid.ItemsSource = null;
            CommandCountCombo.SelectedValue = 1;
            SelectedStepText.Text = "";
            ConditionDiagramGrid.Children.Clear();
            return;
        }

        step.EnsureDefaults();
        RefreshCommandSlots(step);
        SelectedStepText.Text = $"Selected step: {step.StepNumber}";
        var normalizedCount = NormalizeConditionCount(step.ConditionNumber);
        _updatingConditionCount = true;
        ConditionCountCombo.SelectedValue = normalizedCount;
        _updatingConditionCount = false;
        _updatingCommandCount = true;
        CommandCountCombo.SelectedValue = SequenceStepConfig.NormalizeCommandCount(step.CommandNumber);
        _updatingCommandCount = false;
        RenderConditionDiagram();
    }

    private void ConditionCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingConditionCount || StepsList.SelectedItem is not SequenceStepConfig step)
        {
            return;
        }

        var count = ConditionCountCombo.SelectedValue is int selectedValue
            ? selectedValue
            : NormalizeConditionCount(step.ConditionNumber);
        step.ConditionNumber = count;
        RenderConditionDiagram();
    }

    private void CommandCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCommandCount || StepsList.SelectedItem is not SequenceStepConfig step)
        {
            return;
        }

        var count = CommandCountCombo.SelectedValue is int selectedValue
            ? selectedValue
            : SequenceStepConfig.NormalizeCommandCount(step.CommandNumber);
        step.CommandNumber = SequenceStepConfig.NormalizeCommandCount(count);
        RefreshCommandSlots(step);
    }

    private void RefreshCommandSlots(SequenceStepConfig step)
    {
        step.EnsureDefaults();
        var commandCount = SequenceStepConfig.NormalizeCommandCount(step.CommandNumber);
        step.CommandNumber = commandCount;
        CommandSlotsGrid.ItemsSource = step.Commands.Take(commandCount).ToList();
    }

    private void RenderConditionDiagram()
    {
        ConditionDiagramGrid.Children.Clear();
        ConditionDiagramGrid.RowDefinitions.Clear();
        ConditionDiagramGrid.ColumnDefinitions.Clear();

        if (StepsList.SelectedItem is not SequenceStepConfig step)
        {
            return;
        }

        step.EnsureDefaults();
        var conditionCount = NormalizeConditionCount(step.ConditionNumber);
        step.ConditionNumber = conditionCount;

        for (var index = 0; index < conditionCount; index++)
        {
            ConditionDiagramGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        }

        var columnWidths = new[] { 340d, 54d, 92d, 54d, 118d, 64d, 118d, 64d, 118d, 64d };
        foreach (var width in columnWidths)
        {
            ConditionDiagramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        ConditionDiagramGrid.Width = columnWidths.Sum();

        var statusOptions = BuildOptions(ControlStatusRows);
        for (var index = 0; index < conditionCount; index++)
        {
            var slot = step.Conditions[index];
            AddConditionInput(slot, statusOptions, index);
            AddConnector(index, 1);
            AddInvertCombo(slot, index);
            AddConnector(index, 3);
        }

        if (conditionCount >= 2)
        {
            AddGateCombo(step, nameof(SequenceStepConfig.FirstCondition1), 0, 4, 2);
            AddConnector(0, 5, 2);
        }

        if (conditionCount >= 4)
        {
            AddGateCombo(step, nameof(SequenceStepConfig.FirstCondition2), 2, 4, 2);
            AddConnector(2, 5, 2);
            AddGateCombo(step, nameof(SequenceStepConfig.SecondCondition1), 0, 6, 4);
            AddConnector(0, 7, 4);
        }

        if (conditionCount >= 8)
        {
            AddGateCombo(step, nameof(SequenceStepConfig.FirstCondition3), 4, 4, 2);
            AddConnector(4, 5, 2);
            AddGateCombo(step, nameof(SequenceStepConfig.FirstCondition4), 6, 4, 2);
            AddConnector(6, 5, 2);
            AddGateCombo(step, nameof(SequenceStepConfig.SecondCondition2), 4, 6, 4);
            AddConnector(4, 7, 4);
            AddGateCombo(step, nameof(SequenceStepConfig.FinalCondition), 0, 8, 8);
            AddConnector(0, 9, 8);
        }
        else
        {
            AddFinalConnector(conditionCount);
        }
    }

    private void AddConditionInput(SequenceConditionSlot slot, IReadOnlyList<SequenceReferenceOption> options, int row)
    {
        var combo = new ComboBox
        {
            ItemsSource = options,
            SelectedValuePath = nameof(SequenceReferenceOption.Id),
            DisplayMemberPath = nameof(SequenceReferenceOption.Display),
            Height = 32,
            MinWidth = 320,
            Margin = new Thickness(0, 6, 12, 6),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        combo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(SequenceConditionSlot.ControlStatusId))
        {
            Source = slot,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        AddElement(combo, row, 0);
    }

    private void AddInvertCombo(SequenceConditionSlot slot, int row)
    {
        var combo = new ComboBox
        {
            ItemsSource = InvertOptions,
            SelectedValuePath = nameof(InvertOption.Value),
            DisplayMemberPath = nameof(InvertOption.Display),
            Height = 32,
            Width = 96,
            Margin = new Thickness(5, 6, 5, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        combo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(SequenceConditionSlot.Invert))
        {
            Source = slot,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        combo.SelectionChanged += (_, _) => RenderConditionDiagram();
        AddElement(combo, row, 2);
    }

    private void AddGateCombo(SequenceStepConfig step, string propertyName, int row, int column, int rowSpan)
    {
        var combo = new ComboBox
        {
            ItemsSource = GateOptions,
            SelectedValuePath = nameof(GateOption.Value),
            DisplayMemberPath = nameof(GateOption.Display),
            Height = Math.Min(78, 34 + rowSpan * 10),
            Width = 102,
            Margin = new Thickness(8, 6, 8, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        combo.SetBinding(Selector.SelectedValueProperty, new Binding(propertyName)
        {
            Source = step,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        AddElement(combo, row, column, rowSpan);
    }

    private void AddConnector(int row, int column, int rowSpan = 1)
    {
        var line = new Border
        {
            Height = 3,
            MinWidth = 36,
            Margin = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(0, 124, 148)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(2)
        };
        AddElement(line, row, column, rowSpan);
    }

    private void AddFinalConnector(int conditionCount)
    {
        var column = conditionCount switch
        {
            1 => 3,
            2 => 5,
            4 => 7,
            _ => 9
        };
        AddConnector(0, column, conditionCount);
    }

    private void AddElement(UIElement element, int row, int column, int rowSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
        ConditionDiagramGrid.Children.Add(element);
    }

    private static int NormalizeConditionCount(int value)
    {
        return SequenceStepConfig.NormalizeLogicCount(value);
    }

    private void RefreshStepTools()
    {
        var allChoices = (AllSequences ?? [])
            .Where(sequence => SequenceDefinition.VisibleKeys.Contains(sequence.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(sequence => SequenceDefinition.SortOrder(sequence.Key))
            .SelectMany(sequence =>
            {
                sequence.EnsureDefaults();
                return sequence.VisibleSteps.Select(step => new StepChoice(sequence, step, $"{sequence.Title} / {step.StepNumber} - {FirstNonEmpty(step.Name, step.NameOther, "Step")}"));
            })
            .ToList();

        CopySourceStepCombo.ItemsSource = allChoices;
        CopyTargetStepCombo.ItemsSource = allChoices;
        CopySourceStepCombo.DisplayMemberPath = nameof(StepChoice.Display);
        CopyTargetStepCombo.DisplayMemberPath = nameof(StepChoice.Display);

        if (CopySourceStepCombo.SelectedItem is not StepChoice source || !allChoices.Contains(source))
        {
            CopySourceStepCombo.SelectedItem = allChoices.FirstOrDefault(choice => ReferenceEquals(choice.Sequence, Sequence));
        }

        if (CopyTargetStepCombo.SelectedItem is not StepChoice target || !allChoices.Contains(target))
        {
            CopyTargetStepCombo.SelectedItem = allChoices.FirstOrDefault(choice => ReferenceEquals(choice.Sequence, Sequence));
        }

        var localChoices = Sequence?.VisibleSteps
            .Select(step => new LocalStepChoice(step, step.StepNumber.ToString()))
            .ToList() ?? [];
        SwapStepACombo.ItemsSource = localChoices;
        SwapStepBCombo.ItemsSource = localChoices;
        SwapStepACombo.DisplayMemberPath = nameof(LocalStepChoice.Display);
        SwapStepBCombo.DisplayMemberPath = nameof(LocalStepChoice.Display);

        if (SwapStepACombo.SelectedItem is not LocalStepChoice stepA || !localChoices.Contains(stepA))
        {
            SwapStepACombo.SelectedItem = localChoices.FirstOrDefault();
        }

        if (SwapStepBCombo.SelectedItem is not LocalStepChoice stepB || !localChoices.Contains(stepB))
        {
            SwapStepBCombo.SelectedItem = localChoices.Skip(1).FirstOrDefault() ?? localChoices.FirstOrDefault();
        }
    }

    private void CopyStep_Click(object sender, RoutedEventArgs e)
    {
        if (CopySourceStepCombo.SelectedItem is not StepChoice source
            || CopyTargetStepCombo.SelectedItem is not StepChoice target)
        {
            return;
        }

        target.Step.CopyContentFrom(source.Step);
        RefreshAfterStepTool(target.Sequence, target.Step.StepNumber);
    }

    private void SwapStep_Click(object sender, RoutedEventArgs e)
    {
        if (Sequence is null
            || SwapStepACombo.SelectedItem is not LocalStepChoice first
            || SwapStepBCombo.SelectedItem is not LocalStepChoice second
            || ReferenceEquals(first.Step, second.Step))
        {
            return;
        }

        var firstCopy = first.Step.CloneForStepNumber(first.Step.StepNumber);
        first.Step.CopyContentFrom(second.Step);
        second.Step.CopyContentFrom(firstCopy);
        RefreshAfterStepTool(Sequence, first.Step.StepNumber);
    }

    private void RefreshAfterStepTool(SequenceDefinition sequence, int selectedStepNumber)
    {
        RefreshStepTools();
        if (ReferenceEquals(sequence, Sequence))
        {
            RefreshStepList(keepSelection: true);
            StepsList.SelectedItem = Sequence?.VisibleSteps.FirstOrDefault(step => step.StepNumber == selectedStepNumber);
            BindSelectedStep();
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private sealed record ConditionCountOption(int Value, string Display);
    private sealed record GateOption(int Value, string Display);
    private sealed record InvertOption(bool Value, string Display);
    private sealed record OnOffOption(bool Value, string Display);
    private sealed record SequenceReferenceOption(int Id, string Display);
    private sealed record StepChoice(SequenceDefinition Sequence, SequenceStepConfig Step, string Display);
    private sealed record LocalStepChoice(SequenceStepConfig Step, string Display);
}
