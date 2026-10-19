using System;

namespace NDVI.Models;

public interface IRasterSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Count { get; }
    double[]? Transform { get; }
    string? Crs { get; }
    float? GetBand(int index, float[] buffer, float scale);
}