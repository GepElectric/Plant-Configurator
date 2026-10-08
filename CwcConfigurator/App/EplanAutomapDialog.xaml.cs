using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;

namespace CwcConfigurator.App;

/// <summary>
/// Tools → Automap IDs: proposes hardware IDs from the EPLAN signal names, lets the user tick / correct them,
/// and returns the rows to write. Writing is done by the caller.
/// </summary>
public partial class EplanAutomapDialog : Window
{
    public sealed class Item : INotifyPropertyChanged
    {
        private readonly IReadOnlyList<AutomapCandidate> _candidates;
        private bool _use;
        private string _idText;
        private string _dbSignal;
        private string _dbStructure;
        private string _dbPlcName;
        private string _matchText;
        private string _note;

        public Item(AutomapProposal proposal, IReadOnlyList<AutomapCandidate> candidates)
        {
            Proposal = proposal;
            _candidates = candidates;
            _idText = proposal.Proposed?.Id.ToString(CultureInfo.InvariantCulture) ?? "";
            _dbSignal = proposal.Proposed?.Name ?? "";
            _dbStructure = proposal.Proposed?.PlcStructure ?? "";
            _dbPlcName = proposal.Proposed?.PlcName ?? "";
            _matchText = proposal.Match == AutomapMatch.None ? "" : proposal.Match.ToString();
            _note = proposal.Note;
            if (proposal.Proposed is not null && proposal.Proposed.Id == proposal.CurrentId)
            {
                _note = string.IsNullOrEmpty(_note) ? "already set" : _note;
            }
        }

        public AutomapProposal Proposal { get; }
        public string Tab => EplanHardwareMapper.KindName(Proposal.Kind);
        public int ModuleNumber => Proposal.ModuleNumber;
        public string ModuleName => Proposal.ModuleName;
        public int? Port => Proposal.Port;
        public string EplanSignal => Proposal.EplanSignal;
        public int CurrentId => Proposal.CurrentId;
        public bool HasProposal => Proposal.Proposed is not null;
        public bool ChangesId => Proposal.Proposed is not null && Proposal.Proposed.Id != Proposal.CurrentId;

        public bool Use { get => _use; set => Set(ref _use, value); }
        public string DbSignal { get => _dbSignal; private set => Set(ref _dbSignal, value); }
        public string DbStructure { get => _dbStructure; private set => Set(ref _dbStructure, value); }
        public string DbPlcName { get => _dbPlcName; private set => Set(ref _dbPlcName, value); }
        public string MatchText { get => _matchText; private set => Set(ref _matchText, value); }
        public string Note { get => _note; private set => Set(ref _note, value); }

        /// <summary>Typed ID: looked up in the DB table of this tab; an unknown ID is rejected.</summary>
        public string IdText
        {
            get => _idText;
            set
            {
                var text = (value ?? "").Trim();
                if (text == _idText)
                {
                    return;
                }

                if (text.Length == 0)
                {
                    Proposal.Proposed = null;
                    Set(ref _idText, "");
                    DbSignal = "";
                    DbStructure = "";
                    DbPlcName = "";
                    MatchText = "";
                    Use = false;
                    return;
                }

                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                    || _candidates.FirstOrDefault(c => c.Id == id) is not { } candidate)
                {
                    Note = $"ID {text} is not in the {(Proposal.Kind is EplanModuleKind.DigitalInput ? "DB DI" : Proposal.Kind is EplanModuleKind.DigitalOutput ? "DB DO" : Proposal.Kind is EplanModuleKind.AnalogOutput ? "DB AO" : "DB MEAS")} table";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IdText)));
                    return;
                }

                Proposal.Proposed = candidate;
                Proposal.Match = AutomapMatch.Likely;
                Set(ref _idText, text);
                DbSignal = candidate.Name;
                DbStructure = candidate.PlcStructure;
                DbPlcName = candidate.PlcName;
                MatchText = "Manual";
                Note = "";
                Use = candidate.Id != Proposal.CurrentId;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private readonly EplanImportConfig _config;
    private List<Item> _items = [];

    public PlcDatabase? Database => DatabaseCombo.SelectedItem as PlcDatabase;

    /// <summary>Ticked rows with an ID to write.</summary>
    public IReadOnlyList<AutomapProposal> Chosen => _items.Where(i => i.Use && i.Proposal.Proposed is not null).Select(i => i.Proposal).ToList();

    /// <summary>True when the dictionary or the learned names were edited (they are already stored in the config).</summary>
    public bool DictionaryChanged { get; private set; }

    public EplanAutomapDialog(IReadOnlyList<PlcDatabase> databases, string activeDatabaseId, EplanImportConfig config)
    {
        InitializeComponent();
        _config = config;
        DatabaseCombo.ItemsSource = databases;
        DatabaseCombo.SelectedItem = databases.FirstOrDefault(d => d.Id == activeDatabaseId) ?? databases.FirstOrDefault();
    }

    private void DatabaseCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => Propose();

    private void Propose()
    {
        if (Database is not { } database)
        {
            return;
        }

        var candidates = Enum.GetValues<EplanModuleKind>().ToDictionary(k => k, k => EplanIdAutomap.Candidates(database, k));
        var matcher = new EplanNameMatcher(_config.Synonyms, _config.LearnedNames);
        _items = EplanIdAutomap.Propose(database, matcher)
            .Select(p => new Item(p, candidates[p.Kind]) { Use = p.Match == AutomapMatch.Exact && p.Proposed!.Id != p.CurrentId })
            .ToList();
        RefreshView();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshView();

    private void Dictionary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EplanDictionaryDialog(_config) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _config.Synonyms = dialog.Synonyms;
        _config.LearnedNames = dialog.LearnedNames;
        DictionaryChanged = true;
        Propose();      // propose again with the new words
    }

    private void RefreshView()
    {
        if (!IsInitialized || CountText is null)
        {
            return;
        }

        ProposalGrid.ItemsSource = OnlyProposalsCheck.IsChecked == true ? _items.Where(i => i.HasProposal).ToList() : _items;
        var exact = _items.Count(i => i.Proposal.Match == AutomapMatch.Exact);
        var likely = _items.Count(i => i.Proposal.Match == AutomapMatch.Likely);
        CountText.Text = _items.Count == 0
            ? "No hardware row of this database has EPLAN data – run Tools → Import hardware from EPLAN first."
            : $"{_items.Count} rows with EPLAN data: {exact} exact, {likely} likely, {_items.Count - exact - likely} without proposal; " +
              $"{_items.Count(i => i.ChangesId)} would change the ID.";
    }

    private void TickExact_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.Use = item.Proposal.Match == AutomapMatch.Exact && item.ChangesId;
        }
    }

    private void TickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.Use = item.ChangesId;
        }
    }

    private void UntickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.Use = false;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ProposalGrid.CommitEdit();
        if (Chosen.Count == 0)
        {
            MessageBox.Show(this, "No row is ticked.", "Automap IDs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var duplicate = Chosen.GroupBy(p => (Table: p.Kind is EplanModuleKind.AnalogInput or EplanModuleKind.RtdInput or EplanModuleKind.Counter ? "MEAS" : p.Kind.ToString(), p.Proposed!.Id))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            MessageBox.Show(this, $"ID {duplicate.Key.Id} is ticked on more than one channel.", "Automap IDs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
