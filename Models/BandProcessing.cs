using System;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Algorithms.RadixSortOperations;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;

// Required for CPU fallback

namespace NDVI.Models;

public class BandProcessing : IDisposable
{
    private readonly Accelerator _accelerator;
    private readonly Context _context;

    public BandProcessing()
    {

        // 1. Configure Context to support EVERYTHING
        _context = Context.Create(builder => builder
            .Cuda()
            .OpenCL()
            .CPU() // Enable CPU backend
            .EnableAlgorithms()
        );

        // 2. Search for devices in order of preference
        Device device = null;
        if (_context.GetCudaDevices().Count > 0) device = _context.GetCudaDevice(0);

        Console.WriteLine($"OpenCL Device Count: {_context.GetCLDevices().Count}");

        if ((device == null) && (_context.GetCLDevices().Count > 0)) device = _context.GetCLDevice(0);

        if (device == null)
            // 3. Fallback to CPU if no GPU found
            device = _context.GetCPUDevice(0);

        if (device == null)
            // This should never happen as CPU is always available
            throw new NotSupportedException("No supported device (CUDA, OpenCL, or CPU) found.");

        // 4. Create the accelerator
        _accelerator = device.CreateAccelerator(_context);

        // Optional: Log what we are using
        Console.WriteLine($"BandProcessing initialized on: {_accelerator.Name} ({_accelerator.AcceleratorType})");
    }


    public void Dispose()
    {
        _accelerator.Dispose();
        _context.Dispose();
    }

    // -----------------------------------------------------------------------
    // Init Kernel (Solves MemSet issues)
    // -----------------------------------------------------------------------
    private static void InitFloatKernel(Index1D index, ArrayView<float> view, float value)
    {
        view[index] = value;
    }

    // -----------------------------------------------------------------------
    // 1. Normalize
    // -----------------------------------------------------------------------
    public void Normalize(float[] inputBuffer, byte[] outputBuffer, float min, float max)
    {
        using var deviceInput = _accelerator.Allocate1D(inputBuffer);
        using var deviceOutput = _accelerator.Allocate1D<byte>(inputBuffer.Length);

        var range = max - min;

        var kernel =
            _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<byte>, float, float, float>(
                NormalizeKernel);
        kernel((int)deviceInput.Length, deviceInput.View, deviceOutput.View, min, max, range);

        _accelerator.Synchronize();
        deviceOutput.CopyToCPU(outputBuffer);
    }

    private static void NormalizeKernel(Index1D index, ArrayView<float> input, ArrayView<byte> output, float min,
        float max, float range)
    {
        var value = input[index];
        value = XMath.Clamp(value, min, max);
        var normalized = (value - min) / range;
        output[index] = (byte)(normalized * 255.0f);
    }

    // -----------------------------------------------------------------------
    // 2. Min/Max
    // -----------------------------------------------------------------------
    public (float min, float max) GetBandMinMax(float[] buffer, float? ignoreValue = null)
    {
        var reductionSize = Math.Max(1, buffer.Length / 1024);

        using var deviceInput = _accelerator.Allocate1D(buffer);
        using var deviceResultMin = _accelerator.Allocate1D<float>(reductionSize);
        using var deviceResultMax = _accelerator.Allocate1D<float>(reductionSize);

        // Initialize using our custom kernel to be safe
        var initKernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, float>(InitFloatKernel);
        initKernel(reductionSize, deviceResultMin.View, float.PositiveInfinity);
        initKernel(reductionSize, deviceResultMax.View, float.NegativeInfinity);

        var kernel = _accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView<float>,
            ArrayView<float>,
            ArrayView<float>,
            float,
            int>(MinMaxKernel);

        var ignoreVal = ignoreValue ?? float.NaN;
        var useIgnore = ignoreValue.HasValue ? 1 : 0;

        kernel((int)deviceInput.Length, deviceInput.View, deviceResultMin.View, deviceResultMax.View, ignoreVal,
            useIgnore);

        _accelerator.Synchronize();

        var partialMin = deviceResultMin.GetAsArray1D();
        var partialMax = deviceResultMax.GetAsArray1D();

        var finalMin = float.PositiveInfinity;
        var finalMax = float.NegativeInfinity;

        for (var i = 0; i < partialMin.Length; i++)
        {
            if (partialMin[i] < finalMin) finalMin = partialMin[i];
            if (partialMax[i] > finalMax) finalMax = partialMax[i];
        }

        return (finalMin, finalMax);
    }

    private static void MinMaxKernel(
        Index1D index,
        ArrayView<float> input,
        ArrayView<float> outMin,
        ArrayView<float> outMax,
        float ignoreVal,
        int useIgnore)
    {
        var val = input[index];

        if (useIgnore == 1 && val == ignoreVal)
            return;

        var outIdx = index % outMin.IntLength;

        Atomic.Min(ref outMin[outIdx], val);
        Atomic.Max(ref outMax[outIdx], val);
    }


    // -----------------------------------------------------------------------
    // 3. Quantiles
    // -----------------------------------------------------------------------

    public float[] SampleArray(float[] arr, double fraction = 0.005, float? ignoreValue = null)
    {
        var sampleSize = (int)(arr.Length * fraction);
        var rng = new Random();
        var sampled = new float[sampleSize];

        for (var i = 0; i < sampleSize; i++)
        {
            var index = rng.Next(arr.Length);
            var value = arr[index];

            if (ignoreValue.HasValue)
                while (value == ignoreValue.Value)
                {
                    index = rng.Next(arr.Length);
                    value = arr[index];
                }

            sampled[i] = arr[index];
        }

        return sampled;
    }

    private float[] Sort(float[] data)
    {
        using var deviceData = _accelerator.Allocate1D(data);
        var radixSort = _accelerator.CreateRadixSort<float, Stride1D.Dense, AscendingFloat>();
        var tempMemSize = _accelerator.ComputeRadixSortTempStorageSize<float, AscendingFloat>((int)deviceData.Length);

        using (var tempBuffer = _accelerator.Allocate1D<int>(tempMemSize))
        {
            radixSort(
                _accelerator.DefaultStream,
                deviceData.View,
                tempBuffer.View);
        }

        _accelerator.Synchronize();
        return deviceData.GetAsArray1D();
    }

    public (float, float) GetQuantiles(float[] arr, float q, float? noDataValue = null)
    {
        var sampled = SampleArray(arr, 0.005, noDataValue);
        var sorted = Sort(sampled);
        return InterpolateQuantiles(sorted, q);
    }

    private (float min, float max) InterpolateQuantiles(float[] sorted, float q)
    {
        double pos = (sorted.Length - 1) * q;
        var index = (int)pos;
        var fraction = pos - index;

        float min;
        if (index + 1 < sorted.Length)
            min = (float)(sorted[index] + fraction * (sorted[index + 1] - sorted[index]));
        else
            min = sorted[index];

        pos = (sorted.Length - 1) * (1 - q);
        index = (int)pos;
        fraction = pos - index;

        float max;
        if (index + 1 < sorted.Length)
            max = (float)(sorted[index] + fraction * (sorted[index + 1] - sorted[index]));
        else
            max = sorted[index];

        return (min, max);
    }

    public void MapTwoBands<TOperation>(float[] band1, float[] band2, float[] result)
        where TOperation : struct, IBandOperation
    {
        if (band1.Length != band2.Length || band1.Length != result.Length)
            throw new ArgumentException("Arrays must be same length");

        using var dBand1 = _accelerator.Allocate1D(band1);
        using var dBand2 = _accelerator.Allocate1D(band2);
        using var dResult = _accelerator.Allocate1D(result);

        var kernel = _accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView<float>,
            ArrayView<float>,
            ArrayView<float>,
            TOperation>(MapKernel);

        kernel((int)dBand1.Length, dBand1.View, dBand2.View, dResult.View, new TOperation());

        _accelerator.Synchronize();
        dResult.CopyToCPU(result);
    }

    private static void MapKernel<TOperation>(
        Index1D index,
        ArrayView<float> b1,
        ArrayView<float> b2,
        ArrayView<float> res,
        TOperation op)
        where TOperation : struct, IBandOperation
    {
        res[index] = op.Apply(b1[index], b2[index]);
    }

    // -----------------------------------------------------------------------
    // 3. MapTwoBands
    // -----------------------------------------------------------------------
    public interface IBandOperation
    {
        float Apply(float a, float b);
    }

    public struct NdviCalc : IBandOperation
    {
        public float Apply(float band1, float band2)
        {
            var num = band1 - band2;
            var den = band1 + band2;
            if (XMath.Abs(den) < 1e-6f) return 0;
            return num / den;
        }
    }
}
