using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using Veldrid;

namespace EvaEngine;

public sealed unsafe class NativeFramePool
{
    internal ConcurrentQueue<FFFrame> _pool;
    internal readonly nint _frameSize;
    public readonly long Stride;
    public readonly long Rows;

    public NativeFramePool(int capacity, long stride, long rows, long size)
    {
        _frameSize = (nint)size;
        Stride = stride;
        Rows = rows;
        _pool = new();
        for (int i = 0; i < capacity; i++)
            _pool.Enqueue(new(stride, rows, size));
    }

    public FFFrame Rent()
    {
        if (_pool.TryDequeue(out var ptr))
            return ptr;
        // Si el pool se agota, asignamos uno extra
        return new(Stride, Rows, _frameSize);
    }

    public void Return(FFFrame ptr)
    {
        _pool.Enqueue(ptr);
    }

    public void FreeAll()
    {
        while (_pool.TryDequeue(out var ptr))
            ptr.Dispose();
    }
}