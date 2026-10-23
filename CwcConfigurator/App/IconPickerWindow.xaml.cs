using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace CwcConfigurator.App;

/// <summary>
/// Picks an icon id from a SCADA graphic list (Geppert: images of the WinCC Unified graphic list; Prime Volt: the fixed
/// CWC icons). Returns the id as text in <see cref="SelectedValue"/>.
/// </summary>
public partial class IconPickerWindow : Window
{
    private static readonly Dictionary<string, ImageSource?> ImageCache = new();
    private readonly List<IconItem> _items;

    public string SelectedValue { get; private set; } = "";

    public IconPickerWindow(string title, IReadOnlyList<ScadaGraphicEntry> entries, string current, string info)
    {
        InitializeComponent();
        Title = title;
        InfoText.Text = info;
        _items = [new IconItem(0, "(no icon)", null), .. entries.Select(entry => new IconItem(entry.Value, entry.Graphic, ToImage(entry)))];
        IconList.ItemsSource = _items;
        IconList.SelectedItem = _items.FirstOrDefault(item => item.IdText == $"ID {current}") ?? _items[0];
        IconList.ScrollIntoView(IconList.SelectedItem);
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Shows the picker for a list of the SCADA; returns the new id, or null when cancelled or the list is empty.</summary>
    public static string? Pick(Window owner, ScadaDatabase database, string list, string current, string what)
    {
        var entries = ScadaGraphicLists.EntriesFor(database, list);
        if (entries.Count == 0)
        {
            MessageBox.Show(owner,
                database.IsPrimeVolt
                    ? "Prime Volt SCADA has icons only on the main navigation."
                    : $"Graphic list {list} is not loaded yet: pull the SCADA from TIA or click 'Refresh graphic lists'.",
                "Icon", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var info = database.IsPrimeVolt
            ? "Prime Volt SCADA: icons of the CWC (icon_settings)."
            : $"Geppert SCADA: graphic list {list} of the WinCC Unified device.";
        var picker = new IconPickerWindow($"Icon — {what}", entries, current, info) { Owner = owner };
        return picker.ShowDialog() == true ? picker.SelectedValue : null;
    }

    private static ImageSource? ToImage(ScadaGraphicEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Image))
        {
            return null;
        }

        var key = entry.Graphic + "|" + entry.Image.Length;
        if (ImageCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            var bytes = Convert.FromBase64String(entry.Image);
            using var stream = new MemoryStream(bytes);
            if (entry.ImageType == "svg")
            {
                using var reader = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true });
                var drawing = reader.Read(stream);
                if (drawing is not null)
                {
                    image = new DrawingImage(drawing);
                    image.Freeze();
                }
            }
            else
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch
        {
            // A graphic that cannot be drawn is shown by its name only.
        }

        ImageCache[key] = image;
        return image;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IconList.ItemsSource = words.Length == 0
            ? _items
            : _items.Where(item => words.All(word => item.Graphic.Contains(word, StringComparison.OrdinalIgnoreCase) || item.IdText.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void IconList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IconList.SelectedItem is IconItem)
        {
            Ok_Click(sender, e);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not IconItem item)
        {
            return;
        }

        SelectedValue = item.Value.ToString(CultureInfo.InvariantCulture);
        DialogResult = true;
    }

    private sealed class IconItem(int value, string graphic, ImageSource? image)
    {
        public int Value => value;
        public string Graphic => graphic;
        public ImageSource? Image => image;
        public string IdText => $"ID {value}";
        public string Placeholder => image is null && value != 0 ? "?" : "";
    }
}
