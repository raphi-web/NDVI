using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Platform.Storage;
using OSGeo.GDAL;

namespace NDVI.Models;

public class SafeFile : IRasterSource
{
    
    private readonly string? _crs = null;
    private readonly double[] _transform = new double[6];
    private readonly Dataset _dataset;
    private readonly List<string> _subdatasetNames = new();
    public readonly int Count10M;
    public readonly int Count20M;
    public readonly int Count60M;
    public readonly List<(int, int)> Dimensions = new();

    private IStorageFile _file;

    public SafeFile(IStorageFile file)
    {
        _file = file;
        var path = file.TryGetLocalPath();
        if (path is null)
            throw new ArgumentException("Selected file does not have a local path (e.g., virtual or cloud-only file).");

        _dataset = Gdal.Open(path, Access.GA_ReadOnly);
        if (_dataset == null)
            throw new InvalidOperationException("Could not open the dataset.");
        _dataset.GetGeoTransform(_transform);
        _crs = _dataset.GetProjectionRef();
        _subdatasetNames = _dataset.GetMetadata("SUBDATASETS")
            .Where(s => s.Contains("_NAME="))
            .Select(s => s.Split('=')[1]) // This strips the Key= part!
            .ToList();

        var totalBands = 0;

        for (var i = 0; i < _subdatasetNames.Count; i++)
        {
            var subDsName = _subdatasetNames[i];

            // On Linux/GDAL, ensure the path is clean.
            var subdataset = Gdal.Open(subDsName, Access.GA_ReadOnly);

            if (subdataset == null)
            {
                Console.WriteLine($"Failed to open subdataset: {subDsName}");
                continue;
            }

            totalBands += subdataset.RasterCount;
            Dimensions.Add((subdataset.RasterXSize, subdataset.RasterYSize));

            if (i is 0)
                Count10M = subdataset.RasterCount;

            else if (i is 1)
                Count20M = subdataset.RasterCount;
            else if (i is 2) Count60M = subdataset.RasterCount;

            subdataset.Dispose();
        }

        Count = totalBands;

        if (Dimensions.Count > 0)
        {
            Width = Dimensions[0].Item1;
            Height = Dimensions[0].Item2;
        }
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


    public float? GetBand(int globalIndex, float[] buffer, float scale = 1)
    {
        if (globalIndex < Count10M) return _getBand(0, globalIndex, buffer);
        if (globalIndex < Count10M + Count20M) return _getBand(1, globalIndex - Count10M, buffer);
        return _getBand(2, globalIndex - Count10M - Count20M, buffer, scale);
    }

    private float? _getBand(int subDatasetIndex, int bandIndex, float[] buffer, float scale = 1)
    {
        var subDsString = _subdatasetNames[subDatasetIndex];
        using var subdataset = Gdal.Open(subDsString, Access.GA_ReadOnly);
        var band = subdataset.GetRasterBand(bandIndex + 1);

        var (nativeWidth, nativeHeight) = Dimensions[subDatasetIndex];
        var targetWidth = (int)(Width * scale);
        var targetHeight = (int)(Height * scale);

        double val;
        int hasVal;
        band.GetNoDataValue(out val, out hasVal);


        band.ReadRaster(
            0,
            0,
            nativeWidth,
            nativeHeight,
            buffer, targetWidth, targetHeight, 0, 0);
        band.Dispose();
        return hasVal != 0 ? (float)val : null;
    }

    ~SafeFile()
    {
        _dataset?.Dispose();
    }
}