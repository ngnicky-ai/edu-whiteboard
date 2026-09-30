using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WhiteboardApp.Controls;
using WhiteboardApp.Models;
using WhiteboardApp.Services;
using WhiteboardApp.ViewModels;

namespace WhiteboardApp.Views;

public partial class MainWindow : Window
{
    private enum PlacedType { Image, Text }

    private class PlacedItem
    {
        public required ResizableCanvasItem Control { get; init; }
        public PlacedType Type { get; init; }
        public BitmapSource? OriginalImage { get; init; }
        public byte[]? PngBytes { get; set; }
        public TextBox? TextBoxRef { get; init; }
    }

    // ---- undo/redo ----
    private abstract class UndoAction { }

    private class StrokeAddedAction : UndoAction { public required Stroke Stroke; }
    private class StrokeRemovedAction : UndoAction { public required Stroke Stroke; }
    private class ObjectAddedAction : UndoAction { public required PlacedItem Item; }
    private class ObjectRemovedAction : UndoAction { public required PlacedItem Item; public int Index; }

    private readonly Stack<UndoAction> _undoStack = new();
    private readonly Stack<UndoAction> _redoStack = new();
    private bool _isReplayingHistory;

    private readonly List<PlacedItem> _placedItems = new();
    private ResizableCanvasItem? _selectedItem;
    private readonly ToolViewModel _tool = new();

    private string? _currentFilePath;
    private BackgroundTheme _currentBackground = BackgroundTheme.White;
    private bool _isWindowInitialized;

    // The "infinite" board is a very large world (40000 x 40000 units = dozens of screens in
    // every direction) that the viewport pans and zooms over.
    private const double WorldSize = 40000;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;
    private const double LegacyPageWidth = 1600;
    private const double LegacyPageHeight = 900;
    private double _zoom = 1.0;
    private bool _viewInitialized;

    private enum DragMode { None, Pan, Zoom }
    private DragMode _dragMode;
    private MouseButton _dragButton;
    private Point _dragLast;
    private Point _zoomAnchor;
    private bool _spaceHeld;
    private int _pasteCascade;

    private const string HelpText = "휠: 위아래 이동 · Shift+휠: 좌우 이동 · Ctrl+휠: 확대/축소 · 스페이스/가운데 버튼 드래그: 화면 이동 · Ctrl+V: 붙여넣기";

    public MainWindow()
    {
        InitializeComponent();
        _isWindowInitialized = true;

        WorldGrid.Width = WorldSize;
        WorldGrid.Height = WorldSize;

        MainInkCanvas.Strokes.StrokesChanged += Strokes_StrokesChanged;
        Deactivated += (_, _) =>
        {
            _spaceHeld = false;
            UpdatePanCursor();
        };
        ApplyBackgroundTheme(BackgroundTheme.White);
        ApplyToolState();
        UpdateStatus("준비됨 — " + HelpText);
        InitializeBoardLifecycle();
    }

    // ===================== Tool selection =====================

    private void SetTool(ToolMode mode)
    {
        _tool.CurrentTool = mode;

        PenToolButton.IsChecked = mode == ToolMode.Pen;
        HighlighterToolButton.IsChecked = mode == ToolMode.Highlighter;
        EraserToolButton.IsChecked = mode == ToolMode.Eraser;
        SelectToolButton.IsChecked = mode == ToolMode.Select;
        PanToolButton.IsChecked = mode == ToolMode.Pan;
        TextToolButton.IsChecked = mode == ToolMode.Text;

        ApplyToolState();

        if (mode == ToolMode.Text)
        {
            UpdateStatus("텍스트: 칠판을 클릭한 곳에 글자를 입력하세요. Esc 또는 빈 곳 클릭: 입력 마침");
        }
    }

    private void ApplyToolState()
    {
        switch (_tool.CurrentTool)
        {
            case ToolMode.Pen:
                MainInkCanvas.IsHitTestVisible = true;
                MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                MainInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = _tool.PenColor,
                    Width = _tool.PenThickness,
                    Height = _tool.PenThickness,
                    IsHighlighter = false,
                    FitToCurve = true
                };
                ObjectCanvas.IsHitTestVisible = false;
                break;

            case ToolMode.Highlighter:
                MainInkCanvas.IsHitTestVisible = true;
                MainInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                var highlighterColor = Color.FromArgb(90, _tool.PenColor.R, _tool.PenColor.G, _tool.PenColor.B);
                MainInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = highlighterColor,
                    Width = _tool.HighlighterThickness,
                    Height = _tool.HighlighterThickness,
                    IsHighlighter = true,
                    FitToCurve = false
                };
                ObjectCanvas.IsHitTestVisible = false;
                break;

            case ToolMode.Eraser:
                MainInkCanvas.IsHitTestVisible = true;
                MainInkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
                MainInkCanvas.EraserShape = new EllipseStylusShape(_tool.EraserThickness, _tool.EraserThickness);
                ObjectCanvas.IsHitTestVisible = false;
                break;

            case ToolMode.Select:
                MainInkCanvas.IsHitTestVisible = false;
                MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
                ObjectCanvas.IsHitTestVisible = true;
                break;

            case ToolMode.Pan:
                MainInkCanvas.IsHitTestVisible = false;
                MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
                ObjectCanvas.IsHitTestVisible = false;
                break;

            case ToolMode.Text:
                MainInkCanvas.IsHitTestVisible = false;
                MainInkCanvas.EditingMode = InkCanvasEditingMode.None;
                ObjectCanvas.IsHitTestVisible = true;
                break;
        }

        var isText = _tool.CurrentTool == ToolMode.Text;
        SizeLabel.Text = isText ? "글자 크기" : "굵기";
        switch (_tool.CurrentTool)
        {
            case ToolMode.Pen: SetSlider(1, 30, _tool.PenThickness); break;
            case ToolMode.Highlighter: SetSlider(1, 30, _tool.HighlighterThickness); break;
            case ToolMode.Eraser: SetSlider(1, 30, _tool.EraserThickness); break;
            case ToolMode.Text: SetSlider(10, 120, _tool.TextSize); break;
        }

        // Text tool = click into a text box to edit it; every other tool treats text boxes as
        // solid objects, so the select tool can drag them from anywhere inside.
        foreach (var item in _placedItems)
        {
            if (item.TextBoxRef != null) item.TextBoxRef.IsHitTestVisible = isText;
        }

        Viewport.Cursor = _tool.CurrentTool switch
        {
            ToolMode.Pan => Cursors.Hand,
            ToolMode.Text => Cursors.IBeam,
            _ => null
        };
    }

    private bool _updatingSlider;

    // Changing Minimum/Maximum can coerce Value and raise ValueChanged; don't let that
    // programmatic change overwrite the tool's stored size.
    private void SetSlider(double min, double max, double value)
    {
        _updatingSlider = true;
        try
        {
            ThicknessSlider.Minimum = min;
            ThicknessSlider.Maximum = max;
            ThicknessSlider.Value = value;
        }
        finally
        {
            _updatingSlider = false;
        }
    }

    private void PenToolButton_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.Pen);
    private void HighlighterToolButton_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.Highlighter);
    private void EraserToolButton_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.Eraser);
    private void SelectToolButton_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.Select);
    private void PanToolButton_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.Pan);
    private void TextToolButton_Click(object sender, RoutedEventArgs e) => SetTool(ToolMode.Text);

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isWindowInitialized || _updatingSlider) return;

        switch (_tool.CurrentTool)
        {
            case ToolMode.Pen: _tool.PenThickness = e.NewValue; break;
            case ToolMode.Highlighter: _tool.HighlighterThickness = e.NewValue; break;
            case ToolMode.Eraser: _tool.EraserThickness = e.NewValue; break;
            case ToolMode.Text:
                _tool.TextSize = e.NewValue;
                ApplyTextStyleToSelection();
                return;
        }
        ApplyToolState();
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        ApplyPickedColor((Color)ColorConverter.ConvertFromString(hex));
    }

    private void CustomColorButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerWindow(_tool.PenColor) { Owner = this };
        if (dialog.ShowDialog() == true) ApplyPickedColor(dialog.SelectedColor);
    }

    private void ApplyPickedColor(Color color)
    {
        _tool.PenColor = color;
        switch (_tool.CurrentTool)
        {
            case ToolMode.Text:
                ApplyTextStyleToSelection();
                break;
            case ToolMode.Highlighter:
                SetTool(ToolMode.Highlighter);
                break;
            case ToolMode.Eraser:
                break;
            default:
                SetTool(ToolMode.Pen);
                break;
        }
    }

    // With the text tool active, size/color changes restyle the selected text box.
    private void ApplyTextStyleToSelection()
    {
        var textBox = _placedItems.FirstOrDefault(p => p.Control == _selectedItem)?.TextBoxRef;
        if (textBox == null) return;

        textBox.FontSize = _tool.TextSize / _zoom;
        textBox.Foreground = new SolidColorBrush(_tool.PenColor);
        textBox.CaretBrush = textBox.Foreground;
    }

    private void BackgroundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isWindowInitialized) return;

        if (BackgroundCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<BackgroundTheme>(tag, out var theme))
        {
            ApplyBackgroundTheme(theme);
        }
    }

    private void ApplyBackgroundTheme(BackgroundTheme theme)
    {
        if (theme != _currentBackground) MarkDirty();
        _currentBackground = theme;
        Viewport.Background = theme switch
        {
            BackgroundTheme.Blackboard => new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
            BackgroundTheme.GreenBoard => new SolidColorBrush(Color.FromRgb(0x0B, 0x3D, 0x24)),
            _ => Brushes.White
        };
    }

    // ===================== Undo / Redo =====================

    private void Strokes_StrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        MarkDirty();
        if (_isReplayingHistory) return;

        foreach (Stroke s in e.Added)
        {
            _undoStack.Push(new StrokeAddedAction { Stroke = s });
        }
        foreach (Stroke s in e.Removed)
        {
            _undoStack.Push(new StrokeRemovedAction { Stroke = s });
        }
        if (e.Added.Count > 0 || e.Removed.Count > 0)
        {
            _redoStack.Clear();
        }
    }

    private void PushObjectAdded(PlacedItem item)
    {
        MarkDirty();
        if (_isReplayingHistory) return;
        _undoStack.Push(new ObjectAddedAction { Item = item });
        _redoStack.Clear();
    }

    private void PushObjectRemoved(PlacedItem item, int index)
    {
        MarkDirty();
        if (_isReplayingHistory) return;
        _undoStack.Push(new ObjectRemovedAction { Item = item, Index = index });
        _redoStack.Clear();
    }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e) => PerformUndo();
    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e) => PerformRedo();
    private void UndoButton_Click(object sender, RoutedEventArgs e) => PerformUndo();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => PerformRedo();

    private void PerformUndo()
    {
        if (!IsBoardVisible || _undoStack.Count == 0) return;
        MarkDirty();
        var action = _undoStack.Pop();
        _isReplayingHistory = true;
        try
        {
            switch (action)
            {
                case StrokeAddedAction a:
                    MainInkCanvas.Strokes.Remove(a.Stroke);
                    break;
                case StrokeRemovedAction a:
                    MainInkCanvas.Strokes.Add(a.Stroke);
                    break;
                case ObjectAddedAction a:
                    RemoveObjectInternal(a.Item);
                    break;
                case ObjectRemovedAction a:
                    InsertObjectInternal(a.Item, a.Index);
                    break;
            }
            _redoStack.Push(action);
        }
        finally
        {
            _isReplayingHistory = false;
        }
    }

    private void PerformRedo()
    {
        if (!IsBoardVisible || _redoStack.Count == 0) return;
        MarkDirty();
        var action = _redoStack.Pop();
        _isReplayingHistory = true;
        try
        {
            switch (action)
            {
                case StrokeAddedAction a:
                    MainInkCanvas.Strokes.Add(a.Stroke);
                    break;
                case StrokeRemovedAction a:
                    MainInkCanvas.Strokes.Remove(a.Stroke);
                    break;
                case ObjectAddedAction a:
                    InsertObjectInternal(a.Item, ObjectCanvas.Children.Count);
                    break;
                case ObjectRemovedAction a:
                    RemoveObjectInternal(a.Item);
                    break;
            }
            _undoStack.Push(action);
        }
        finally
        {
            _isReplayingHistory = false;
        }
    }

    private void RemoveObjectInternal(PlacedItem item)
    {
        ObjectCanvas.Children.Remove(item.Control);
        _placedItems.Remove(item);
        if (_selectedItem == item.Control) _selectedItem = null;
    }

    private void InsertObjectInternal(PlacedItem item, int index)
    {
        if (index > ObjectCanvas.Children.Count) index = ObjectCanvas.Children.Count;
        ObjectCanvas.Children.Insert(index, item.Control);
        _placedItems.Add(item);
    }

    // ===================== Paste (image / text) =====================

    private void Paste_Executed(object sender, ExecutedRoutedEventArgs e) => DoPaste();

    // WPF's InkCanvas registers its own internal CommandBinding for ApplicationCommands.Paste
    // (for pasting ink/ISF data) which intercepts command routing before it ever reaches the
    // Window-level CommandBinding above. Handling Ctrl+V directly in PreviewKeyDown (a tunneling
    // event that fires before the InkCanvas gets a chance) bypasses that and guarantees our
    // image/text paste logic always runs.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Board shortcuts (paste, delete, space-pan, zoom) don't apply on the home screen.
        if (!IsBoardVisible) return;

        // While a pasted text box is being edited, normal typing/editing keys belong to it.
        var editingText = Keyboard.FocusedElement is TextBox;

        if (e.Key == Key.Escape && editingText)
        {
            EndTextEditing();
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !editingText)
        {
            DoPaste();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selectedItem != null && !editingText)
        {
            DeleteItem(_selectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && !editingText)
        {
            if (!_spaceHeld)
            {
                _spaceHeld = true;
                UpdatePanCursor();
            }
            e.Handled = true;
        }
        else if ((e.Key == Key.D0 || e.Key == Key.NumPad0) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ZoomAt(ViewportCenter, 1.0);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            UpdatePanCursor();
            e.Handled = true;
        }
    }

    // ===================== Infinite canvas: pan & zoom =====================

    private Point ViewportCenter => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    private Point ViewportToWorld(Point p) =>
        new((p.X - WorldTranslate.X) / _zoom, (p.Y - WorldTranslate.Y) / _zoom);

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_viewInitialized)
        {
            _viewInitialized = true;
            CenterViewOn(new Point(WorldSize / 2, WorldSize / 2));
        }
        else
        {
            ApplyViewTransform();
        }
    }

    private void CenterViewOn(Point world)
    {
        var c = ViewportCenter;
        WorldTranslate.X = c.X - world.X * _zoom;
        WorldTranslate.Y = c.Y - world.Y * _zoom;
        ApplyViewTransform();
    }

    private void PanBy(double dx, double dy)
    {
        WorldTranslate.X += dx;
        WorldTranslate.Y += dy;
        ApplyViewTransform();
    }

    // Zooms while keeping the world point under viewportPoint fixed on screen.
    private void ZoomAt(Point viewportPoint, double newZoom)
    {
        var world = ViewportToWorld(viewportPoint);
        _zoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        WorldTranslate.X = viewportPoint.X - world.X * _zoom;
        WorldTranslate.Y = viewportPoint.Y - world.Y * _zoom;
        ApplyViewTransform();
        UpdateStatus($"확대/축소: {_zoom * 100:0}% — Ctrl+0: 100%로 되돌리기");
    }

    private void ApplyViewTransform()
    {
        WorldScale.ScaleX = _zoom;
        WorldScale.ScaleY = _zoom;

        var scaledWorld = WorldSize * _zoom;
        WorldTranslate.X = ClampOffset(WorldTranslate.X, Viewport.ActualWidth, scaledWorld);
        WorldTranslate.Y = ClampOffset(WorldTranslate.Y, Viewport.ActualHeight, scaledWorld);

        ZoomResetButton.Content = $"{_zoom * 100:0}%";
    }

    private static double ClampOffset(double offset, double viewportLength, double scaledWorld) =>
        scaledWorld <= viewportLength
            ? (viewportLength - scaledWorld) / 2
            : Math.Clamp(offset, viewportLength - scaledWorld, 0);

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e) => ZoomAt(ViewportCenter, 1.0);

    private void Viewport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        DragMode mode;
        if (e.ChangedButton == MouseButton.Middle)
        {
            mode = Keyboard.Modifiers == ModifierKeys.Control ? DragMode.Zoom : DragMode.Pan;
        }
        else if (e.ChangedButton == MouseButton.Left && (_spaceHeld || _tool.CurrentTool == ToolMode.Pan))
        {
            mode = DragMode.Pan;
        }
        else
        {
            return;
        }

        var pos = e.GetPosition(Viewport);
        _dragMode = mode;
        _dragButton = e.ChangedButton;
        _dragLast = pos;
        _zoomAnchor = pos;
        Viewport.CaptureMouse();
        UpdatePanCursor();
        e.Handled = true;
    }

    private void Viewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        var pos = e.GetPosition(Viewport);
        var delta = pos - _dragLast;
        _dragLast = pos;

        if (_dragMode == DragMode.Pan)
        {
            PanBy(delta.X, delta.Y);
        }
        else
        {
            // Drag up = zoom in, drag down = zoom out, anchored where the drag started.
            ZoomAt(_zoomAnchor, _zoom * Math.Exp(-delta.Y * 0.005));
        }
        e.Handled = true;
    }

    private void Viewport_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.None || e.ChangedButton != _dragButton) return;

        Viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        _dragMode = DragMode.None;
        UpdatePanCursor();
    }

    private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        switch (Keyboard.Modifiers)
        {
            case ModifierKeys.Control:
                // One wheel notch (Delta = 120) changes zoom by 10%, centered on the mouse pointer.
                ZoomAt(e.GetPosition(Viewport), _zoom * Math.Pow(1.1, e.Delta / 120.0));
                break;
            case ModifierKeys.Shift:
                PanBy(e.Delta, 0);
                break;
            default:
                PanBy(0, e.Delta);
                break;
        }
        e.Handled = true;
    }

    private void UpdatePanCursor()
    {
        Mouse.OverrideCursor = _dragMode == DragMode.Pan ? Cursors.SizeAll
            : _spaceHeld ? Cursors.Hand
            : null;
    }

    private Rect ContentBounds()
    {
        var bounds = MainInkCanvas.Strokes.Count > 0 ? MainInkCanvas.Strokes.GetBounds() : Rect.Empty;
        foreach (var item in _placedItems)
        {
            bounds.Union(new Rect(Canvas.GetLeft(item.Control), Canvas.GetTop(item.Control), item.Control.ActualWidth, item.Control.ActualHeight));
        }
        return bounds;
    }

    private void FitViewToContent()
    {
        var bounds = ContentBounds();
        if (bounds.IsEmpty)
        {
            _zoom = 1.0;
            CenterViewOn(new Point(WorldSize / 2, WorldSize / 2));
            return;
        }

        var fit = Math.Min(Viewport.ActualWidth / bounds.Width, Viewport.ActualHeight / bounds.Height) * 0.9;
        _zoom = Math.Clamp(Math.Min(fit, 1.0), MinZoom, MaxZoom);
        CenterViewOn(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
    }

    // ===================== Placing pasted / captured content =====================

    private void DoPaste()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var image = ClipboardImageReader.TryRead(dpi.PixelsPerInchX, dpi.PixelsPerInchY);
        if (image != null)
        {
            AddImageObject(image);
            UpdateStatus("이미지를 붙여넣었습니다. 그 위에 펜으로 판서할 수 있습니다.");
            return;
        }

        string? text = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard temporarily locked by another app; treat as empty.
        }

        if (!string.IsNullOrEmpty(text))
        {
            AddTextObject(text);
        }
        else
        {
            UpdateStatus("클립보드에 붙여넣을 이미지나 텍스트가 없습니다.");
        }
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        var image = ScreenCaptureService.CaptureRegion(this);
        if (image != null)
        {
            AddImageObject(image);
            UpdateStatus("화면 캡처 이미지를 추가했습니다. 그 위에 펜으로 판서할 수 있습니다.");
        }
    }

    // Centers new content in the current view, nudging repeated pastes so they don't stack exactly.
    private Point NextPlacement(double width, double height)
    {
        var center = ViewportToWorld(ViewportCenter);
        var nudge = (_pasteCascade++ % 5) * 24 / _zoom;
        return new Point(center.X - width / 2 + nudge, center.Y - height / 2 + nudge);
    }

    private void AddImageObject(BitmapSource image)
    {
        var imgControl = new System.Windows.Controls.Image
        {
            Source = image,
            Stretch = Stretch.Fill
        };

        // Show the image at its natural on-screen size regardless of zoom, capped to 80% of the view.
        double w = image.Width, h = image.Height;
        var fit = Math.Min(1.0, Math.Min(Viewport.ActualWidth * 0.8 / w, Viewport.ActualHeight * 0.8 / h));
        w = w * fit / _zoom;
        h = h * fit / _zoom;
        var position = NextPlacement(w, h);

        var host = new ResizableCanvasItem { InnerElement = imgControl, Width = w, Height = h };
        Canvas.SetLeft(host, position.X);
        Canvas.SetTop(host, position.Y);
        WireItemEvents(host);
        ObjectCanvas.Children.Add(host);

        var item = new PlacedItem { Control = host, Type = PlacedType.Image, OriginalImage = image };
        _placedItems.Add(item);
        PushObjectAdded(item);
        SelectItem(host);
    }

    private TextBox CreateTextBox(string text, double fontSize, Brush foreground)
    {
        var textBox = new TextBox
        {
            Text = text,
            FontSize = fontSize,
            Foreground = foreground,
            CaretBrush = foreground,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = _tool.CurrentTool == ToolMode.Text
        };
        textBox.TextChanged += (_, _) => MarkDirty();
        return textBox;
    }

    private void AddTextObject(string text)
    {
        // Sized in world units so it appears at a readable size at the current zoom level,
        // and colored to stay visible on the dark chalkboard themes.
        var textBox = CreateTextBox(text, 24 / _zoom,
            _currentBackground == BackgroundTheme.White ? Brushes.Black : Brushes.White);

        var w = 320 / _zoom;
        var position = NextPlacement(w, 120 / _zoom);

        // Height left automatic so the box grows with its content.
        var host = new ResizableCanvasItem { InnerElement = textBox, Width = w };
        Canvas.SetLeft(host, position.X);
        Canvas.SetTop(host, position.Y);
        WireItemEvents(host);
        ObjectCanvas.Children.Add(host);

        var item = new PlacedItem { Control = host, Type = PlacedType.Text, TextBoxRef = textBox };
        _placedItems.Add(item);
        PushObjectAdded(item);
        SelectItem(host);
    }

    private void WireItemEvents(ResizableCanvasItem item)
    {
        item.Selected += (_, _) => SelectItem(item);
        item.DeleteRequested += (_, _) => DeleteItem(item);
        item.ItemChanged += (_, _) => MarkDirty();
    }

    private void SelectItem(ResizableCanvasItem item)
    {
        if (_selectedItem != null && _selectedItem != item) _selectedItem.SetSelected(false);
        _selectedItem = item;
        item.SetSelected(true);
    }

    private void DeleteItem(ResizableCanvasItem control)
    {
        var item = _placedItems.FirstOrDefault(p => p.Control == control);
        if (item == null) return;
        var index = ObjectCanvas.Children.IndexOf(control);
        PushObjectRemoved(item, index);
        RemoveObjectInternal(item);
    }

    private void DeleteSelected_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsBoardVisible && _selectedItem != null) DeleteItem(_selectedItem);
    }

    private void ObjectCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != ObjectCanvas) return;

        // A click on empty board while typing just finishes the current text (like MS Whiteboard);
        // otherwise, with the text tool, it starts a new text box at that spot.
        var wasEditing = Keyboard.FocusedElement is TextBox;
        if (_selectedItem != null)
        {
            _selectedItem.SetSelected(false);
            _selectedItem = null;
        }

        if (wasEditing)
        {
            EndTextEditing();
        }
        else if (_tool.CurrentTool == ToolMode.Text)
        {
            StartTextAt(e.GetPosition(ObjectCanvas));
        }
        e.Handled = true;
    }

    private void StartTextAt(Point world)
    {
        var textBox = CreateTextBox(string.Empty, _tool.TextSize / _zoom, new SolidColorBrush(_tool.PenColor));
        textBox.MinWidth = 40 / _zoom;

        // Width and height stay automatic so the box grows as the user types.
        var host = new ResizableCanvasItem { InnerElement = textBox };
        Canvas.SetLeft(host, world.X);
        Canvas.SetTop(host, world.Y - textBox.FontSize * 0.7);
        WireItemEvents(host);
        ObjectCanvas.Children.Add(host);

        var item = new PlacedItem { Control = host, Type = PlacedType.Text, TextBoxRef = textBox };
        _placedItems.Add(item);
        SelectItem(host);

        // Only record it for undo once it actually has text; an empty box is discarded.
        var committed = false;
        textBox.LostKeyboardFocus += (_, _) =>
        {
            if (committed) return;
            committed = true;
            if (string.IsNullOrWhiteSpace(textBox.Text))
            {
                RemoveObjectInternal(item);
            }
            else
            {
                PushObjectAdded(item);
            }
        };

        Dispatcher.BeginInvoke(() => Keyboard.Focus(textBox), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndTextEditing()
    {
        Keyboard.Focus(Viewport);
    }

    // ===================== Board lifecycle: home screen, autosave, open / new =====================
    // Boards live in BoardLibrary.Folder and are saved automatically (when leaving a board, when
    // closing the app, and every minute while there are changes), each with a thumbnail for the
    // home screen.

    private string _currentTitle = WhiteboardDocument.DefaultTitle;
    private bool _dirty;
    private bool _loadingBoard;

    private bool IsBoardVisible => BoardView.Visibility == Visibility.Visible;
    private bool IsBoardEmpty => MainInkCanvas.Strokes.Count == 0 && _placedItems.Count == 0;

    private void MarkDirty()
    {
        if (!_loadingBoard) _dirty = true;
    }

    private void InitializeBoardLifecycle()
    {
        var autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        autosave.Tick += (_, _) =>
        {
            if (IsBoardVisible && _dirty && TrySaveBoard()) UpdateStatus($"자동 저장했습니다 ({DateTime.Now:HH:mm})");
        };
        autosave.Start();

        Closing += (_, e) =>
        {
            if (!LeaveCurrentBoard()) e.Cancel = true;
        };

        ShowHome();
    }

    // ---------- home screen ----------

    private void ShowHome()
    {
        BoardView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        Title = "교육용 판서 프로그램";
        RefreshBoardList();
    }

    private void RefreshBoardList()
    {
        var newCard = (Button)FindResource("NewBoardCard");
        newCard.Click += NewButton_Click;

        var items = new List<object> { newCard };
        items.AddRange(BoardLibrary.List().Select(BoardCard.From));
        BoardList.ItemsSource = items;
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (LeaveCurrentBoard()) ShowHome();
    }

    private void BoardCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BoardCard card }) OpenBoard(card.Path);
    }

    private void BoardMenu_Click(object sender, RoutedEventArgs e)
    {
        // Stop the click from also bubbling up to the card and opening the board.
        e.Handled = true;
        if (sender is not Button { Tag: BoardCard card } button) return;

        var rename = new MenuItem { Header = "이름 바꾸기" };
        rename.Click += (_, _) => RenameBoard(card);
        var delete = new MenuItem { Header = "삭제" };
        delete.Click += (_, _) => DeleteBoard(card);

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void RenameBoard(BoardCard card)
    {
        var dialog = new InputDialog("새 이름을 입력하세요.", card.Title) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value == card.Title) return;

        try
        {
            FileService.Rename(card.Path, dialog.Value);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"이름을 바꾸지 못했습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshBoardList();
    }

    private void DeleteBoard(BoardCard card)
    {
        var answer = MessageBox.Show($"'{card.Title}' 판서를 삭제할까요?\n휴지통으로 이동하므로 나중에 복원할 수 있습니다.",
            "판서 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            BoardLibrary.MoveToRecycleBin(card.Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"삭제하지 못했습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshBoardList();
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BoardLibrary.Folder);
        System.Diagnostics.Process.Start("explorer.exe", BoardLibrary.Folder);
    }

    // ---------- switching boards ----------

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!LeaveCurrentBoard()) return;

        _loadingBoard = true;
        try
        {
            ClearBoard();
        }
        finally
        {
            _loadingBoard = false;
        }
        _currentFilePath = null;
        _currentTitle = WhiteboardDocument.DefaultTitle;
        _dirty = false;

        ShowBoard();
        _zoom = 1.0;
        CenterViewOn(new Point(WorldSize / 2, WorldSize / 2));
        UpdateStatus("새 판서를 시작했습니다. 작업 내용은 자동으로 저장됩니다. — " + HelpText);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => ImportBoard();
    private void Open_Executed(object sender, ExecutedRoutedEventArgs e) => ImportBoard();

    // Opening an outside .wbd copies it into the library so it shows up on the home screen.
    private void ImportBoard()
    {
        var dlg = new OpenFileDialog { Filter = "판서 파일 (*.wbd)|*.wbd" };
        if (dlg.ShowDialog() != true || !LeaveCurrentBoard()) return;

        try
        {
            OpenBoard(BoardLibrary.Import(dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"파일을 가져오지 못했습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenBoard(string path)
    {
        LoadedPage loaded;
        try
        {
            loaded = FileService.Load(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"판서를 여는 중 오류가 발생했습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _loadingBoard = true;
        try
        {
            ClearBoard();
            PopulateBoard(loaded);
        }
        finally
        {
            _loadingBoard = false;
        }
        _currentFilePath = path;
        _currentTitle = loaded.Title;
        _dirty = false;

        ShowBoard();
        FitViewToContent();
        UpdateStatus($"'{_currentTitle}' 판서를 열었습니다. — " + HelpText);
    }

    private void ShowBoard()
    {
        HomeView.Visibility = Visibility.Collapsed;
        BoardView.Visibility = Visibility.Visible;
        Title = $"교육용 판서 프로그램 — {_currentTitle}";
        UpdateLayout();
        Keyboard.Focus(Viewport);
    }

    /// <summary>Saves the open board if needed. Returns false only if the user chose to stay after a failed save.</summary>
    private bool LeaveCurrentBoard()
    {
        if (!IsBoardVisible) return true;

        EndTextEditing(); // commits (or discards, if empty) a text box that is still being typed
        if (!_dirty || TrySaveBoard()) return true;

        return MessageBox.Show("판서를 저장하지 못했습니다. 저장하지 않고 계속할까요?", "저장 실패",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ClearBoard()
    {
        MainInkCanvas.Strokes.Clear();
        ObjectCanvas.Children.Clear();
        _placedItems.Clear();
        _selectedItem = null;
        _undoStack.Clear();
        _redoStack.Clear();
        _pasteCascade = 0;
        ApplyBackgroundTheme(BackgroundTheme.White);
        BackgroundCombo.SelectedIndex = 0;
    }

    private void PopulateBoard(LoadedPage loaded)
    {
        // Files from the fixed-page version used 0..1600 x 0..900 page coordinates;
        // move that content to the middle of the infinite world.
        if (loaded.Version < 2)
        {
            var dx = WorldSize / 2 - LegacyPageWidth / 2;
            var dy = WorldSize / 2 - LegacyPageHeight / 2;
            loaded.Strokes.Transform(new Matrix(1, 0, 0, 1, dx, dy), false);
            foreach (var obj in loaded.Objects)
            {
                obj.X += dx;
                obj.Y += dy;
            }
        }

        _isReplayingHistory = true;
        MainInkCanvas.Strokes.Add(loaded.Strokes);
        _isReplayingHistory = false;

        ApplyBackgroundTheme(loaded.Background);
        BackgroundCombo.SelectedIndex = loaded.Background switch
        {
            BackgroundTheme.Blackboard => 1,
            BackgroundTheme.GreenBoard => 2,
            _ => 0
        };

        foreach (var obj in loaded.Objects)
        {
            if (obj.Type == CanvasObjectType.Image && obj.ImageFileName != null &&
                loaded.Images.TryGetValue(obj.ImageFileName, out var bytes))
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();

                var imgControl = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Fill };
                var host = new ResizableCanvasItem { InnerElement = imgControl, Width = obj.Width, Height = obj.Height };
                Canvas.SetLeft(host, obj.X);
                Canvas.SetTop(host, obj.Y);
                WireItemEvents(host);
                ObjectCanvas.Children.Add(host);
                _placedItems.Add(new PlacedItem { Control = host, Type = PlacedType.Image, OriginalImage = bmp, PngBytes = bytes });
            }
            else if (obj.Type == CanvasObjectType.Text)
            {
                var textBox = CreateTextBox(obj.Text ?? string.Empty, obj.FontSize,
                    new SolidColorBrush((Color)ColorConverter.ConvertFromString(obj.FontColor ?? "#FF000000")));
                var host = new ResizableCanvasItem { InnerElement = textBox, Width = obj.Width };
                Canvas.SetLeft(host, obj.X);
                Canvas.SetTop(host, obj.Y);
                WireItemEvents(host);
                ObjectCanvas.Children.Add(host);
                _placedItems.Add(new PlacedItem { Control = host, Type = PlacedType.Text, TextBoxRef = textBox });
            }
        }
    }

    // ---------- saving ----------

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveNow();

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsBoardVisible) SaveNow();
    }

    private void SaveNow()
    {
        if (_currentFilePath == null && IsBoardEmpty)
        {
            UpdateStatus("아직 저장할 내용이 없습니다.");
            return;
        }
        if (TrySaveBoard()) UpdateStatus($"저장했습니다 — 홈 화면과 '{BoardLibrary.Folder}' 폴더에서 볼 수 있습니다.");
    }

    private bool TrySaveBoard()
    {
        // A brand-new board nobody drew on isn't worth a card on the home screen.
        if (_currentFilePath == null && IsBoardEmpty)
        {
            _dirty = false;
            return true;
        }

        try
        {
            var (objects, images) = BuildObjectModels();
            var path = _currentFilePath ?? BoardLibrary.NewBoardPath();
            FileService.Save(path, _currentTitle, _currentBackground, MainInkCanvas.Strokes, objects, images, RenderThumbnail());
            _currentFilePath = path;
            _dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus($"저장하지 못했습니다: {ex.Message}");
            return false;
        }
    }

    private (List<CanvasObjectModel> Objects, Dictionary<string, byte[]> Images) BuildObjectModels()
    {
        var objects = new List<CanvasObjectModel>();
        var images = new Dictionary<string, byte[]>();

        for (var i = 0; i < _placedItems.Count; i++)
        {
            var item = _placedItems[i];
            var left = Canvas.GetLeft(item.Control);
            var top = Canvas.GetTop(item.Control);

            if (item.Type == PlacedType.Image && item.OriginalImage != null)
            {
                // Encoding is cached: autosave shouldn't re-compress every image each minute.
                item.PngBytes ??= EncodePng(item.OriginalImage);
                var fileName = $"img{i}.png";
                images[fileName] = item.PngBytes;
                objects.Add(new CanvasObjectModel
                {
                    Type = CanvasObjectType.Image,
                    X = left,
                    Y = top,
                    Width = item.Control.ActualWidth,
                    Height = item.Control.ActualHeight,
                    ImageFileName = fileName
                });
            }
            else if (item.Type == PlacedType.Text && item.TextBoxRef != null)
            {
                objects.Add(new CanvasObjectModel
                {
                    Type = CanvasObjectType.Text,
                    X = left,
                    Y = top,
                    Width = item.Control.ActualWidth,
                    Height = item.Control.ActualHeight,
                    Text = item.TextBoxRef.Text,
                    FontSize = item.TextBoxRef.FontSize,
                    FontColor = (item.TextBoxRef.Foreground as SolidColorBrush)?.Color.ToString() ?? "#FF000000"
                });
            }
        }

        return (objects, images);
    }

    // Renders an overview of everything on the board (not just the current view) for the home screen.
    private byte[] RenderThumbnail()
    {
        const int width = 480, height = 270;

        var wasSelected = _selectedItem;
        wasSelected?.SetSelected(false);
        try
        {
            UpdateLayout();
            var bounds = ContentBounds();

            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
            {
                ctx.DrawRectangle(Viewport.Background, null, new Rect(0, 0, width, height));
                if (!bounds.IsEmpty)
                {
                    // Pad the content and enforce a minimum area so a single small mark isn't blown up to fill the card.
                    var viewW = Math.Max(bounds.Width * 1.1, 1200);
                    var viewH = Math.Max(bounds.Height * 1.1, 675);
                    var view = new Rect(bounds.X + bounds.Width / 2 - viewW / 2, bounds.Y + bounds.Height / 2 - viewH / 2, viewW, viewH);

                    foreach (Visual layer in new Visual[] { ObjectCanvas, MainInkCanvas })
                    {
                        var brush = new VisualBrush(layer) { Viewbox = view, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Uniform };
                        ctx.DrawRectangle(brush, null, new Rect(0, 0, width, height));
                    }
                }
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return EncodePng(rtb);
        }
        finally
        {
            wasSelected?.SetSelected(true);
        }
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private void ExportPngButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PNG 이미지 (*.png)|*.png", FileName = "판서.png" };
        if (dlg.ShowDialog() != true) return;

        var wasSelected = _selectedItem;
        wasSelected?.SetSelected(false);
        try
        {
            ExportService.ExportToPng(Viewport, dlg.FileName);
            UpdateStatus($"현재 화면에 보이는 영역을 PNG로 내보냈습니다: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"내보내기 중 오류가 발생했습니다:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            wasSelected?.SetSelected(true);
        }
    }

    private void UpdateStatus(string text) => StatusText.Text = text;
}
