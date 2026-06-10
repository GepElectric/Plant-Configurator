using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CwcConfigurator.App;

public partial class ColorPickerDialog : Window
{
    public string SelectedColor { get; private set; } = "#FFFFFF";

    public ColorPickerDialog(string initialColor)
    {
        InitializeComponent();
        var color = TryParseColor(initialColor, Colors.White);
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        UpdatePreview();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePreview();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = CurrentHex();
        DialogResult = true;
    }

    private void UpdatePreview()
    {
        if (Preview is null)
        {
            return;
        }

        RedText.Text = ((int)RedSlider.Value).ToString(CultureInfo.InvariantCulture);
        GreenText.Text = ((int)GreenSlider.Value).ToString(CultureInfo.InvariantCulture);
        BlueText.Text = ((int)BlueSlider.Value).ToString(CultureInfo.InvariantCulture);
        Preview.Background = new SolidColorBrush(CurrentColor());
    }

    private string CurrentHex()
    {
        var color = CurrentColor();
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private Color CurrentColor()
    {
        return Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
    }

    private static Color TryParseColor(string value, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }
}
