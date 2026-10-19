using System;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;

namespace NDVI.Models;

public static class GpuKernels
{
    public static void PackRgbKernel(
        Index1D i,
        ArrayView<byte> r,
        ArrayView<byte> g,
        ArrayView<byte> b,
        ArrayView<byte> rgba)
    {
        int o = i * 4;
        rgba[o + 0] = b[i]; // B
        rgba[o + 1] = g[i]; // G
        rgba[o + 2] = r[i]; // R
        rgba[o + 3] = 255; // A
    }
}

public class GpuPacker : IDisposable
{
    private readonly Accelerator _accel;
    private readonly Context _ctx;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>> _kernel;

    public GpuPacker()
    {
        _ctx = Context.Create(b => b.Cuda().OpenCL().CPU().EnableAlgorithms());
        var dev = _ctx.GetPreferredDevice(preferCuda: true);
        _accel = dev.CreateAccelerator(_ctx);
        _kernel = _accel.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>>(GpuKernels.PackRgbKernel);
    }

    public void Dispose()
    {
        _accel.Dispose();
        _ctx.Dispose();
    }

    public void Pack(byte[] r, byte[] g, byte[] b, byte[] rgbaOut)
    {
        var pixels = r.Length;
        using var dR = _accel.Allocate1D(r);
        using var dG = _accel.Allocate1D(g);
        using var dB = _accel.Allocate1D(b);
        using var dRGBA = _accel.Allocate1D<byte>(pixels * 4);

        _kernel(pixels, dR.View, dG.View, dB.View, dRGBA.View);
        _accel.Synchronize();
        dRGBA.CopyToCPU(rgbaOut);
    }
}

internal static class IlgpuDevicePick
{
    public static Device GetPreferredDevice(this Context ctx, bool preferCuda = true)
    {
        if (preferCuda && ctx.GetCudaDevices().Count > 0) return ctx.GetCudaDevice(0);
        if (ctx.GetCLDevices().Count > 0) return ctx.GetCLDevice(0);
        return ctx.GetCPUDevice(0);
    }
}