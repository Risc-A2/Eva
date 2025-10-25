using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

using FFmpeg.AutoGen;

using std;

namespace EvaEngine;
public sealed unsafe class NativeFramePool
{
    internal readonly ConcurrentBag<IntPtr> _pool;
    internal readonly nint _frameSize;

    public NativeFramePool(int capacity, nint frameSize)
    {
        _frameSize = frameSize;
        _pool = new ConcurrentBag<IntPtr>();
        for (int i = 0; i < capacity; i++)
            _pool.Add(Marshal.AllocHGlobal(frameSize));
    }

    public IntPtr Rent()
    {
        if (_pool.TryTake(out var ptr))
            return ptr;
        // Si el pool se agota, asignamos uno extra
        return Marshal.AllocHGlobal(_frameSize);
    }

    public void Return(IntPtr ptr)
    {
        _pool.Add(ptr);
    }

    public void FreeAll()
    {
        while (_pool.TryTake(out var ptr))
            Marshal.FreeHGlobal(ptr);
    }
}
public sealed class FFProcWrapper : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentBag<FFFrame> frames;
    private readonly NativeFramePool _pool;
    private readonly Task _task;
    public bool Encode;
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _bytesPerPixel;
    public FFProcWrapper(Process ff, uint width, uint height, uint BytesPerPixel)
    {
        _process = ff;
        Encode = true;
        frames = new();
        _task = Task.Run(FFWrite);
        _width = width;
        _height = height;
        _bytesPerPixel = BytesPerPixel;
        _pool = new(10, (nint)(_width * _height * _bytesPerPixel));
    }
    public FFFrame AllocateFFFrame()
    {
        return new(_pool.Rent(), _width * _bytesPerPixel, _height);
    }
    public void WriteFrame(FFFrame frame)
    {
        frames.Add(frame);
    }
    private async unsafe Task FFWrite()
    {
        while (Encode || !frames.IsEmpty)
        {
            FFFrame frame = default!;
            if (!frames.TryTake(out frame!))
                continue;
            ReadOnlySpan<byte> view = new(frame._region.ToPointer(), (int)_pool._frameSize);
            _process.StandardInput.BaseStream.Write(view);
            _pool.Return(frame._region);
        }
    }

    public void Dispose()
    {
        Encode = false;
        Task.WaitAny(_task);
        _pool.FreeAll();
        _process.StandardInput.Close();
    }
    public sealed class FFFrame
    {
        public readonly long Stride;
        public readonly long Rows;
        public readonly long Size;
        internal IntPtr _region;
        public IntPtr Map()
        {
            return _region;
        }
        internal FFFrame(IntPtr region, long stride = 0, long rows = 0)
        {
            _region = region;
            Stride = stride;
            Rows = rows;
            Size = stride * rows;
        }
    }
}
