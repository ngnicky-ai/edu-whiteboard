using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhiteboardApp.ViewModels;

public enum ToolMode
{
    Pen,
    Highlighter,
    Eraser,
    Select,
    Pan,
    Text
}

public partial class ToolViewModel : ObservableObject
{
    [ObservableProperty]
    private ToolMode currentTool = ToolMode.Pen;

    [ObservableProperty]
    private Color penColor = Colors.Black;

    [ObservableProperty]
    private double penThickness = 3;

    [ObservableProperty]
    private double highlighterThickness = 14;

    [ObservableProperty]
    private double eraserThickness = 20;

    // On-screen font size for the text tool.
    [ObservableProperty]
    private double textSize = 32;
}
