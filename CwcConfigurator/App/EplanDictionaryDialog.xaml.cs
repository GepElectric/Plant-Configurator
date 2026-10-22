using System.Collections.ObjectModel;
using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

/// <summary>Edits the Automap dictionary (abbreviations/synonyms) and the learned name pairs of the project.</summary>
public partial class EplanDictionaryDialog : Window
{
    private readonly ObservableCollection<EplanSynonym> _synonyms;
    private readonly ObservableCollection<EplanLearnedName> _learned;

    public EplanDictionaryDialog(EplanImportConfig config)
    {
        InitializeComponent();
        _synonyms = new(config.Synonyms.Select(s => new EplanSynonym { Words = s.Words, Means = s.Means }));
        _learned = new(config.LearnedNames.Select(l => new EplanLearnedName { SchematicName = l.SchematicName, DbName = l.DbName }));
        SynonymGrid.ItemsSource = _synonyms;
        LearnedGrid.ItemsSource = _learned;
    }

    public List<EplanSynonym> Synonyms =>
        _synonyms.Where(s => !string.IsNullOrWhiteSpace(s.Words) && !string.IsNullOrWhiteSpace(s.Means)).ToList();

    public List<EplanLearnedName> LearnedNames =>
        _learned.Where(l => !string.IsNullOrWhiteSpace(l.SchematicName) && !string.IsNullOrWhiteSpace(l.DbName)).ToList();

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in EplanSynonym.Defaults().Where(d => _synonyms.All(s => !string.Equals(s.Words.Trim(), d.Words, StringComparison.OrdinalIgnoreCase))))
        {
            _synonyms.Add(entry);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SynonymGrid.CommitEdit();
        LearnedGrid.CommitEdit();
        DialogResult = true;
    }
}
