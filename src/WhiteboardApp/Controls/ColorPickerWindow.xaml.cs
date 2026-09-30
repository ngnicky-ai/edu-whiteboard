using System.Windows;
using System.Windows.Media;

namespace WhiteboardApp.Controls;

public partial class ColorPickerWindow : Window
{
    public Color SelectedColor { get; private set; } = Colors.Black;

    public ColorPickerWindow(Color initial)
    {
        InitializeComponent();
        RSlider.Value = initial.R;
        GSlider.Value = initial.G;
        BSlider.Value = initial.B;
        UpdatePreview();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewRect == null) return;
        SelectedColor = Color.FromRgb((byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value);
        PreviewRect.Fill = new SolidColorBrush(SelectedColor);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
