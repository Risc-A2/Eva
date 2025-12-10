using System.Collections.Concurrent;

using FFmpeg.AutoGen;

namespace EvaEngine;

public unsafe class FramePool : IDisposable
{
    private readonly ConcurrentQueue<IntPtr> _availableFrames = new();
    private readonly HashSet<IntPtr> _allFrames = new();
    private readonly int _width, _height;
    private readonly AVPixelFormat _format;
    private readonly bool _isHardware;
    private readonly AVBufferRef* _hwFramesContext;
    private bool _disposed;

    public FramePool(int width, int height, AVPixelFormat format, bool isHardware = false, AVBufferRef* hwFramesContext = null)
    {
        _width = width;
        _height = height;
        _format = format;
        _isHardware = isHardware;
        _hwFramesContext = hwFramesContext;
    }

    public unsafe AVFrame* GetFrame()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FramePool));
        
        if (_availableFrames.TryDequeue(out var frame))
        {
            // Reset the frame for reuse
            //ffmpeg.av_frame_unref((AVFrame*)frame);
            return (AVFrame*)frame;
        }
        else
        {
            // Create new frame if pool is empty
            var frame2 = ffmpeg.av_frame_alloc();
            frame2->width = _width;
            frame2->height = _height;
            frame2->format = (int)_format;

            if (_isHardware && _hwFramesContext != null)
            {
                frame2->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesContext);
                int result = ffmpeg.av_hwframe_get_buffer(_hwFramesContext, frame2, 0);
                if (result < 0)
                    throw new Exception("Error getting hardware frame buffer");
            }
            else
            {
                int result = ffmpeg.av_frame_get_buffer(frame2, 0);
                if (result < 0)
                    throw new Exception("Error allocating frame buffers");
            }

            _allFrames.Add((IntPtr)frame2);
            return frame2;
        }
    }

    public unsafe void ReturnFrame(AVFrame* frame)
    {
        if (_disposed || frame == null) return;
        
        if (!_allFrames.Contains((IntPtr)frame))
        {
            // Frame not from this pool, dispose it
            ffmpeg.av_frame_free(&frame);
            return;
        }

        // Unref and return to pool
        // nvm bad idea for hwframes
        //ffmpeg.av_frame_unref(frame);
        _availableFrames.Enqueue((IntPtr)frame);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var frame in _allFrames)
        {
            AVFrame* a = (AVFrame*)frame;
            ffmpeg.av_frame_free(&a);
        }
        _allFrames.Clear();
        _availableFrames.Clear();
    }
}