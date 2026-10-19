using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxRev.Gdal.Core;
using NDVI.Models;
using OSGeo.GDAL;

namespace NDVI.ViewModels;

public sealed class FileLoadedEventArgs : EventArgs
{
    public FileLoadedEventArgs(IStorageFile file)
    {
        File = file;
    }

    public IStorageFile File { get; }
}

public sealed class ImageUpdatedEventArgs : EventArgs
{
    public ImageUpdatedEventArgs(WriteableBitmap bitmap, int width, int height,double[]? transform, string? crs, WriteableBitmap? ndviBitmap = null)
    {
        Bitmap = bitmap;
        Width = width;
        Height = height;
        Transform = transform;
        Crs = crs;
        NdviBitmap = ndviBitmap;
    }

    public WriteableBitmap Bitmap { get; }
    public WriteableBitmap? NdviBitmap { get; }
    public int Width { get; }
    public int Height { get; }
    public double[]? Transform { get; }
    public string? Crs { get; }
}


public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly float RobustQuantile = (float)0.02;

    private readonly BandProcessing _bandProcessing = new();
    private ObservableCollection<string> _bandChoices;

    private Dictionary<int, (float, float)> _bandMinMax = new();
    private byte[]? _blueBandBuffer;
    private float? _blueNoDataValue;
    private IRasterSource? _currentGeoTiff;

    private bool _isInitializing = false;
    
    [ObservableProperty] private IStorageFile _file;
    [ObservableProperty] private string _filename = "";
    private byte[]? _greenBandBuffer;
    private float? _greenNoDataValue;
    [ObservableProperty] private int _height;

    [ObservableProperty] private Bitmap? _image;
    private bool _isRobust;

    private Dictionary<int, (float, float)> _quantiles = new();
    private byte[]? _redBandBuffer;

    private float? _redNoDataValue;

    private float[]? _reusableBuffer;
    private int _selectedBlueIndex;
    private int _selectedGreenIndex;
    private int _selectedNirIndex;
    private int _selectedRedIndex;
    [ObservableProperty] private int _width;

    public MainWindowViewModel()
    {
        GdalBase.ConfigureAll();
        Gdal.AllRegister();
        BandChoices = ["None"];
    }


    public bool IsRobust
    {
        get => _isRobust;
        set
        {
            if (SetProperty(ref _isRobust, value))
            {
                if (_redBandBuffer == null || _greenBandBuffer == null || _blueBandBuffer == null) return;

                if (value)
                {
                    SetBandQuantile(_selectedRedIndex, _redBandBuffer, _redNoDataValue);
                    SetBandQuantile(_selectedGreenIndex, _greenBandBuffer, _greenNoDataValue);
                    SetBandQuantile(_selectedBlueIndex, _blueBandBuffer, _blueNoDataValue);
                }
                else
                {
                    SetBandMinMax(_selectedRedIndex, _redBandBuffer, _redNoDataValue);
                    SetBandMinMax(_selectedGreenIndex, _greenBandBuffer, _greenNoDataValue);
                    SetBandMinMax(_selectedBlueIndex, _blueBandBuffer, _blueNoDataValue);
                }

                UpdateImage();
            }
        }
    }

    public int SelectedRedIndex
    {
        get => _selectedRedIndex;
        set
        {
            if (SetProperty(ref _selectedRedIndex, value))
                if (_redBandBuffer is not null)
                    LoadBandData(value, ref _redBandBuffer, ref _redNoDataValue);
        }
    }


    public int SelectedGreenIndex
    {
        get => _selectedGreenIndex;
        set
        {
            if (SetProperty(ref _selectedGreenIndex, value))
                if (_greenBandBuffer is not null)
                    LoadBandData(value, ref _greenBandBuffer, ref _greenNoDataValue);
        }
    }

    public int SelectedBlueIndex
    {
        get => _selectedBlueIndex;
        set
        {
            if (SetProperty(ref _selectedBlueIndex, value))
                if (_blueBandBuffer is not null)
                    LoadBandData(value, ref _blueBandBuffer, ref _blueNoDataValue);
        }
    }

    public int SelectedNirIndex
    {
        get => _selectedNirIndex;
        set
        {
            if (SetProperty(ref _selectedNirIndex, value))
                if (!_isInitializing)
                    UpdateImage();
        }
    }

    public ObservableCollection<string> BandChoices
    {
        get => _bandChoices;
        set => SetProperty(ref _bandChoices, value);
    }

    public event EventHandler<FileLoadedEventArgs>? FileLoaded;
    public event EventHandler<ImageUpdatedEventArgs>? ImageUpdated;

    private void Reset()
    {
        _currentGeoTiff?.Dispose();
        _currentGeoTiff = null;
        _redBandBuffer = null;
        _greenBandBuffer = null;
        _blueBandBuffer = null;
        _selectedRedIndex = -1;
        _selectedGreenIndex = -1;
        _selectedBlueIndex = -1;
        _selectedNirIndex = -1;
        _quantiles = new Dictionary<int, (float, float)>();
        _bandMinMax = new Dictionary<int, (float, float)>();
        _bandChoices.Clear();
        _redNoDataValue = null;
        _greenNoDataValue = null;
        _blueNoDataValue = null;
        
        _isRobust = false;
        IsRobust = false;
    }

    private void LoadBandData(int bandIndex, ref byte[] buffer, ref float? noDataValue)
    {
        if (_currentGeoTiff is null || _reusableBuffer is null) return;

        noDataValue = _currentGeoTiff.GetBand(bandIndex, _reusableBuffer, 1f);


        if (IsRobust)
            SetBandQuantile(bandIndex, buffer, noDataValue);
        else
            SetBandMinMax(bandIndex, buffer, noDataValue);

        if(!_isInitializing)  UpdateImage();
    }

    private void SetBandQuantile(int index, byte[] bandBuffer, float? noDataValue = null)
    {
        if (_reusableBuffer is null || _currentGeoTiff is null) return;

        _currentGeoTiff.GetBand(index, _reusableBuffer, 1f);

        float min, max;
        if (_quantiles.TryGetValue(index, out var quantile))
        {
            (min, max) = quantile;
        }
        else
        {
            (min, max) = _bandProcessing.GetQuantiles(_reusableBuffer, RobustQuantile, noDataValue);
            _quantiles[index] = (min, max);
        }

        _bandProcessing.Normalize(_reusableBuffer, bandBuffer, min, max);
    }

    private void SetBandMinMax(int index, byte[] bandBuffer, float? noDataValue = null)
    {
        if (_reusableBuffer is null || _currentGeoTiff is null) return;
        _currentGeoTiff.GetBand(index, _reusableBuffer, 1);

        float min, max;
        if (_bandMinMax.TryGetValue(index, out var value))
        {
            (min, max) = value;
        }
        else
        {
            (min, max) = _bandProcessing.GetBandMinMax(_reusableBuffer, noDataValue);
            _bandMinMax[index] = (min, max);
        }

        _bandProcessing.Normalize(_reusableBuffer, bandBuffer, min, max);
    }

    private void UpdateImage()
    {
        if (_currentGeoTiff is null || _redBandBuffer is null || _greenBandBuffer is null ||
            _blueBandBuffer is null) return;

        Width = _currentGeoTiff.Width;
        Height = _currentGeoTiff.Height;

        var bitmap = new WriteableBitmap(
            new PixelSize(_currentGeoTiff.Width, _currentGeoTiff.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Opaque);

        using (var buffer = bitmap.Lock())
        {
            var totalPixels = _currentGeoTiff.Width * _currentGeoTiff.Height;

            unsafe
            {
                var ptr = (byte*)buffer.Address;

                Parallel.For(0, totalPixels,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                    {
                        var offset = i * 4;
                        ptr[offset + 0] = _redBandBuffer[i];
                        ptr[offset + 1] = _greenBandBuffer[i];
                        ptr[offset + 2] = _blueBandBuffer[i];
                        ptr[offset + 3] = 255;
                    });
            }
        }

        Image = bitmap;
        var geoTransform= _currentGeoTiff.Transform;
        var ndviBitmap = ComputeNdviBitmap();
        ImageUpdated?.Invoke(this, new ImageUpdatedEventArgs(bitmap, Width, Height,geoTransform , _currentGeoTiff.Crs, ndviBitmap));
    }

    /// <summary>
    /// Computes an NDVI = (NIR - Red) / (NIR + Red) image from the raw band
    /// data and renders it with a Red→Yellow→Green colour ramp. Returns null
    /// if no distinct NIR band is selected.
    /// </summary>
    private WriteableBitmap? ComputeNdviBitmap()
    {
        if (_currentGeoTiff is null) return null;
        if (_selectedNirIndex < 0 || _selectedRedIndex < 0) return null;
        if (_selectedNirIndex == _selectedRedIndex) return null;

        var size = _currentGeoTiff.Width * _currentGeoTiff.Height;
        var nir = new float[size];
        var red = new float[size];
        _currentGeoTiff.GetBand(_selectedNirIndex, nir, 1f);
        _currentGeoTiff.GetBand(_selectedRedIndex, red, 1f);

        var ndvi = new float[size];
        _bandProcessing.MapTwoBands<BandProcessing.NdviCalc>(nir, red, ndvi);

        var bitmap = new WriteableBitmap(
            new PixelSize(_currentGeoTiff.Width, _currentGeoTiff.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Opaque);

        using (var buffer = bitmap.Lock())
        {
            unsafe
            {
                var ptr = (byte*)buffer.Address;
                Parallel.For(0, size,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                    {
                        var (r, g, b) = NdviToColor(ndvi[i]);
                        var offset = i * 4;
                        ptr[offset + 0] = r;
                        ptr[offset + 1] = g;
                        ptr[offset + 2] = b;
                        ptr[offset + 3] = 255;
                    });
            }
        }

        return bitmap;
    }

    /// <summary>Maps an NDVI value in [-1, 1] to an RGB colour (red→yellow→green).</summary>
    private static (byte r, byte g, byte b) NdviToColor(float ndvi)
    {
        if (float.IsNaN(ndvi)) return (0, 0, 0);

        var t = (Math.Clamp(ndvi, -1f, 1f) + 1f) / 2f; // 0..1

        if (t < 0.5f)
        {
            var u = t / 0.5f; // red -> yellow
            return (255, (byte)(255 * u), 0);
        }

        var v = (t - 0.5f) / 0.5f; // yellow -> green
        return ((byte)(255 * (1 - v)), 255, 0);
    }

    [RelayCommand]
    public async Task OpenFileDialog()
    {
        var file = await DoOpenFileDialog();
        if (file == null)
            return;

        Reset();
        File = file;
        Filename = file.Name;
        IRasterSource source;
        if (file.Name.EndsWith(".safe", StringComparison.OrdinalIgnoreCase) |
            file.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            source = new SafeFile(file);
        else
            source = new GeoTiff(file);


        _reusableBuffer = new float[source.Width * source.Height];
        var newBands = new List<string>();

        for (var i = 1; i <= source.Count; i++) newBands.Add($"Band: {i}");
        BandChoices = new ObservableCollection<string>(newBands);


        _currentGeoTiff = source;

        _redBandBuffer = new byte[_currentGeoTiff.Width * _currentGeoTiff.Height];
        _greenBandBuffer = new byte[_currentGeoTiff.Width * _currentGeoTiff.Height];
        _blueBandBuffer = new byte[_currentGeoTiff.Width * _currentGeoTiff.Height];

        _isInitializing = true;
        if (source.Count >= 3)
        {
            SelectedRedIndex = 0;
            SelectedGreenIndex = 1;
            SelectedBlueIndex = 2;
        }
        else
        {
            SelectedRedIndex = 0;
            SelectedGreenIndex = 0;
            SelectedBlueIndex = 0;
        }

        // Default the NIR band (used for NDVI on the left side) to band 4 when
        // available; otherwise leave it unset so NDVI is skipped.
        SelectedNirIndex = source.Count >= 4 ? 3 : -1;
        _isInitializing = false;
        UpdateImage();
        FileLoaded?.Invoke(this, new FileLoadedEventArgs(file));
    }

    private async Task<IStorageFile?> DoOpenFileDialog()
    {
        if (Application.Current?.ApplicationLifetime is not
                IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open GeoTiff-File",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Supported") { Patterns = new List<string> { "*.tif", "*.tiff", "*.safe", "*.xml" } },
                new("Geotiff") { Patterns = new List<string> { "*.tif", "*.tiff" } },
                new("SAFE-File") { Patterns = new List<string> { "*.safe", "*.xml" } }
            }
        });
        return files.Count > 0 ? files[0] : null;
    }
}