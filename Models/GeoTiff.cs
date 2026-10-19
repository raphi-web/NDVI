using System;
using System.IO;
using Avalonia.Platform.Storage;
using Mapsui.Widgets.ScaleBar;
using OSGeo.GDAL;

namespace NDVI.Models;

public class GeoTiff : IRasterSource
{
    private readonly Dataset _dataset;
    private readonly string? _crs = null;
    private readonly double[] _transform = new double[6];
    private IStorageFile _file;

    
    public void WriteWorldFile()
    {
        var tfwPath = Path.ChangeExtension(_file.Path.AbsolutePath, ".tfw");
        // If you want center-of-pixel, adjust gt0/gt3 by half-pixel:
        var gt = _transform;
        var xScale = gt[1];
        var yScale = gt[5];
        var rotX = gt[2];
        var rotY = gt[4];

        // Top-left pixel center:
        var xTopLeftCenter = gt[0] + xScale * 0.5 + rotX * 0.5;
        var yTopLeftCenter = gt[3] + yScale * 0.5 + rotY * 0.5;

        File.WriteAllLines(tfwPath, new[]
        {
            xScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rotY.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rotX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            yScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            xTopLeftCenter.ToString(System.Globalization.CultureInfo.InvariantCulture),
            yTopLeftCenter.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }
    
    public GeoTiff(IStorageFile file)
    {
        _file = file;
        var path = file.TryGetLocalPath();
        if (path is null)
            throw new ArgumentException("Selected file does not have a local path (e.g., virtual or cloud-only file).");

        _dataset = Gdal.Open(path, Access.GA_ReadOnly);
        if (_dataset == null)
            throw new InvalidOperationException("Could not open the dataset.");
        _dataset.GetGeoTransform(_transform);
        Count = _dataset.RasterCount;
        Width = _dataset.RasterXSize;
        Height = _dataset.RasterYSize;
        _crs = _dataset.GetProjectionRef();
    }

    public int Count { get; }
    public int Height { get; }
    public int Width { get; }
    
    public double[]? Transform => _transform;
    
    public string? Crs => _crs;
    
    public void Dispose()
    {
        _dataset?.Dispose();
        GC.SuppressFinalize(this);
    }


    
    public (double,double,double,double) Extent
    {
        get
        {
            var xMin = _transform[0];
            var yMax = _transform[3];
            var xMax = _transform[0] + _transform[1] * Width;    
            var yMin = _transform[3] + _transform[5] * Height;
            return (xMin, yMin, xMax, yMax);
        }
    }
    
    public float? GetBand(int index, float[] buffer, float scale=1)
    {
        var band = _dataset.GetRasterBand(index + 1);

        double val;
        int hasVal;
        int bufXSize =(int)(Width * scale);
        int bufYSize =(int)(Height * scale);
        band.GetNoDataValue(out val, out hasVal);
        band.ReadRaster(0, 0, Width, Height, buffer, bufXSize, bufYSize, 0, 0);
        band.Dispose();
        return hasVal != 0 ? (float)val : null;
    }


    ~GeoTiff()
    {
        _dataset?.Dispose();
    }
}