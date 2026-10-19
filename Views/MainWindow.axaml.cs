using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Extensions.Provider;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Providers;
using Mapsui.Styles;
using Mapsui.UI.Avalonia;
using Mapsui.Widgets;
using Mapsui.Widgets.ButtonWidgets;
using Mapsui.Widgets.InfoWidgets;
using Mapsui.Widgets.ScaleBar;
using NDVI.Models;
using NDVI.ViewModels;
using NetTopologySuite.Geometries;

namespace NDVI.Views;

public partial class MainWindow : Window
{
    private MapControl? _leftMapControl;
    private MapControl? _rightMapControl;
    private bool _isSynchronizing = false;

    private Grid? _compareContainer;
    private Avalonia.Controls.Shapes.Rectangle? _sliderLine;
    private Border? _sliderHandle;
    private bool _isDragging = false;
    private double _splitPosition = -1; // X position of the divider in pixels

    public MainWindow()
    {
        InitializeComponent();
    }


    protected override void OnOpened(EventArgs e)
    {
        var checkbox = this.FindControl<CheckBox>("RobustCheckBox");
        if (checkbox != null)
        {
            checkbox.IsChecked = false;
        }

        Console.WriteLine("Hello");
        base.OnOpened(e);

        _leftMapControl = this.FindControl<MapControl>("LeftMapControl");
        _rightMapControl = this.FindControl<MapControl>("RightMapControl");
        _compareContainer = this.FindControl<Grid>("CompareContainer");
        _sliderLine = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("SliderLine");
        _sliderHandle = this.FindControl<Border>("SliderHandle");

        if (_compareContainer != null)
        {
            // Re-apply the split whenever the view is resized.
            _compareContainer.SizeChanged += (_, _) => UpdateSplit(_splitPosition);
        }

        if (_leftMapControl != null)
        {
            _leftMapControl.Map.BackColor = new Color(0,0,0);
            _leftMapControl.Map.Widgets.Add(new ZoomInOutWidget { Margin = new MRect(20, 40) });
            _leftMapControl.Map.Widgets.Add(new MouseCoordinatesWidget());
            _leftMapControl.Map.Navigator.ViewportChanged += (s, args) => SynchronizeMaps(_leftMapControl, _rightMapControl);
        }

        if (_rightMapControl != null)
        {
            _rightMapControl.Map.BackColor = new Color(0,0,0);
            _rightMapControl.Map.Widgets.Add(new ZoomInOutWidget { Margin = new MRect(20, 40) });
            _rightMapControl.Map.Widgets.Add(new MouseCoordinatesWidget());
            _rightMapControl.Map.Navigator.ViewportChanged += (s, args) => SynchronizeMaps(_rightMapControl, _leftMapControl);
        }
    }

    private void SynchronizeMaps(MapControl? source, MapControl? target)
    {
        if (_isSynchronizing || source == null || target == null) return;

        _isSynchronizing = true;
        try
        {
            var sourceViewport = source.Map.Navigator.Viewport;
            target.Map.Navigator.CenterOn(sourceViewport.CenterX, sourceViewport.CenterY);
            target.Map.Navigator.ZoomTo(sourceViewport.Resolution);
            target.Map.Navigator.RotateTo(sourceViewport.Rotation);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }



    // --- Before/after swipe slider ---------------------------------------

    private void UpdateSplit(double x)
    {
        if (_compareContainer is null || _leftMapControl is null) return;

        var width = _compareContainer.Bounds.Width;
        var height = _compareContainer.Bounds.Height;
        if (width <= 0 || height <= 0) return;

        // First layout: start the divider in the middle.
        if (x < 0) x = width / 2;
        x = Math.Clamp(x, 0, width);
        _splitPosition = x;

        // Reveal only the left portion of the top map; the bottom map shows
        // through everywhere to the right of the divider.
        _leftMapControl.Clip =
            new Avalonia.Media.RectangleGeometry(new Avalonia.Rect(0, 0, x, height));

        if (_sliderLine != null)
        {
            _sliderLine.Height = height;
            Canvas.SetLeft(_sliderLine, x - _sliderLine.Width / 2);
            Canvas.SetTop(_sliderLine, 0);
        }

        if (_sliderHandle != null)
        {
            Canvas.SetLeft(_sliderHandle, x - _sliderHandle.Width / 2);
            Canvas.SetTop(_sliderHandle, height / 2 - _sliderHandle.Height / 2);
        }
    }

    private void OnHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDragging = true;
        e.Pointer.Capture(_sliderHandle);
        e.Handled = true;
    }

    private void OnHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(_compareContainer);
        UpdateSplit(pos.X);
        e.Handled = true;
    }

    private void OnHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel oldVm)
        {
            Console.WriteLine("Unsubscribing old VM");
            oldVm.FileLoaded -= OnFileLoaded;
            oldVm.ImageUpdated -= OnImageUpdated;
        }

        if (DataContext is MainWindowViewModel newVm)
        {
            Console.WriteLine("Subscribing new VM");
            newVm.FileLoaded += OnFileLoaded;
            newVm.ImageUpdated += OnImageUpdated;
        }
    }

    
    
    private void OnImageUpdated(object? sender, ImageUpdatedEventArgs e)
    {
        Console.WriteLine($"Image updated: {e.Width}x{e.Height}");

        if (_leftMapControl is null || _rightMapControl is null) return;

        // Top (left, clipped) side shows NDVI when available; the bottom side
        // always shows the RGB composite.
        var topBitmap = e.NdviBitmap ?? e.Bitmap;
        DisplayBitmapOnMap(topBitmap, e.Width, e.Height, e.Transform, e.Crs, _leftMapControl);
        DisplayBitmapOnMap(e.Bitmap, e.Width, e.Height, e.Transform, e.Crs, _rightMapControl);
    }
    
    private void DisplayBitmapOnMap(WriteableBitmap bitmap, int width, int height, double[]? transform, string? crs, MapControl mapControl)
    {
        if (mapControl is null) return;

        // Remove existing raster layer if present
        var existingLayer = mapControl.Map.Layers.FindLayer("RasterImage").FirstOrDefault();
        if (existingLayer != null)
        {
            mapControl.Map.Layers.Remove(existingLayer);
        }

        // Set the map CRS to match the raster's CRS (no reprojection needed)
        if (!string.IsNullOrEmpty(crs))
        {
            mapControl.Map.CRS = crs;
        }

        MRect extent;
        if (transform is { Length: >= 6 })
        {
            var originX = transform[0];
            var pixelWidth = transform[1];
            var originY = transform[3];
            var pixelHeight = transform[5]; // Usually negative

            var minX = originX;
            var maxX = originX + (width * pixelWidth);
            var maxY = originY;
            var minY = originY + (height * pixelHeight);

            extent = new MRect(minX, minY, maxX, maxY);
        }
        else
        {
            // Fallback: place image at origin with arbitrary size
            extent = new MRect(0, 0, width * 100, height * 100);
        }

        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream);
        var imageBytes = memoryStream.ToArray();
        imageBytes = memoryStream.ToBytes();
        // Create a MemoryLayer with a raster style
        try
        {

            var rasterFeature = new RasterFeature(new MRaster(imageBytes, extent)) { Styles = { new RasterStyle() } };
            var rasterLayer = new MemoryLayer
            {
                Name = "RasterImage",
                Features = new[] { rasterFeature },
                Style = null
            };


            mapControl.Map.Layers.Add(rasterLayer);

            // Zoom to the raster extent (only for the first map to avoid double zooming)
            if (mapControl == _leftMapControl)
            {
                mapControl.Map.Navigator.ZoomToBox(extent.Grow(extent.Width * 0.1));
            }
            mapControl.Refresh();

            Console.WriteLine($"Raster layer added with extent: {extent}");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }


    }
    
    private void OnFileLoaded(object? sender, FileLoadedEventArgs e)
    {
  
       
    }
    


}