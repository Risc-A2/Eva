using System.Runtime.InteropServices;

namespace EvaEngine;

public sealed unsafe class FFFrame : IDisposable
{
    public readonly long Stride;
    public readonly long Rows;
    public readonly long Size;
    internal IntPtr _region;
    internal byte* InternalFrame;
    public IntPtr Map()
    {
        return (IntPtr)InternalFrame;
    }
    public ReadOnlySpan<byte> AsSpan()
    {
        unsafe
        {
            return new ReadOnlySpan<byte>(InternalFrame, (int)Size);
        }
    }
    public FFFrame(IntPtr region, long stride, long rows, long size)
    {
        _region = region;
        Stride = stride;
        Rows = rows;
        Size = size;
        InternalFrame = (byte*)NativeMemory.Alloc((UIntPtr)Size);
        NativeMemory.Copy((void*)region, InternalFrame, (UIntPtr)Size);
    }
    internal FFFrame(long stride, long rows, long size)
    {
        _region = new IntPtr(null);
        Stride = stride;
        Rows = rows;
        Size = size;
        InternalFrame = (byte*)NativeMemory.Alloc((UIntPtr)Size);
    }

    private void ReleaseUnmanagedResources()
    {
        NativeMemory.Free(InternalFrame);
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~FFFrame()
    {
        ReleaseUnmanagedResources();
    }
}