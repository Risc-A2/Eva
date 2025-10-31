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
    internal readonly ConcurrentQueue<IntPtr> _pool;
    internal readonly nint _frameSize;

    public NativeFramePool(int capacity, nint frameSize)
    {
        _frameSize = frameSize;
        _pool = new ConcurrentQueue<IntPtr>();
        for (int i = 0; i < capacity; i++)
            _pool.Enqueue(Marshal.AllocHGlobal(frameSize));
    }

    public IntPtr Rent()
    {
        if (_pool.TryDequeue(out var ptr))
            return ptr;
        // Si el pool se agota, asignamos uno extra
        return Marshal.AllocHGlobal(_frameSize);
    }

    public void Return(IntPtr ptr)
    {
        _pool.Enqueue(ptr);
    }

    public void FreeAll()
    {
        while (_pool.TryDequeue(out var ptr))
            Marshal.FreeHGlobal(ptr);
    }
}
public sealed class FFProcWrapper : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<FFFrame> frames;
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
        frames.Enqueue(frame);
    }
    private async Task FFWrite()
    {
        while (Encode || !frames.IsEmpty)
        {
            if (frames.TryDequeue(out var frame))
            {
                var memoryOwner = ArrayPool<byte>.Shared.Rent((int)_pool._frameSize);
                Marshal.Copy(frame._region, memoryOwner, 0, (int)_pool._frameSize);

                await _process.StandardInput.BaseStream.WriteAsync(memoryOwner, 0,
                    (int)_pool._frameSize);
                ArrayPool<byte>.Shared.Return(memoryOwner);
                _pool.Return(frame._region);
            }
            else
            {
                await Task.Delay(1); // Evitar CPU spinning
            }
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
        public ReadOnlySpan<byte> AsSpan()
        {
            unsafe
            {
                return new ReadOnlySpan<byte>(_region.ToPointer(), (int)Size);
            }
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