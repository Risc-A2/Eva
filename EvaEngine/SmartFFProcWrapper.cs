using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

using Veldrid;

namespace EvaEngine;

public sealed class SmartFFProcWrapper : IDisposable
{
    private readonly Process _ffmpegProcess;
    private readonly VideoWriter _videoWriter;
    private readonly bool _useDirectEncoding;
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _bytesPerPixel;
    private bool E;
    private readonly ConcurrentQueue<FFFrame> frames;
    //private readonly Channel<FFProcWrapper.FFFrame> frames;
    private readonly NativeFramePool _pool;
    private readonly Task _task;
    public FFFrame AllocateFFFrame()
    {
        return _pool.Rent();
        //return new(r.Data, r.RowPitch, _height, r.SizeInBytes);
    }
    public void WriteFrame(FFFrame frame)
    {
        //frames.Writer.WriteAsync(frame).ConfigureAwait(false).GetAwaiter().GetResult();
        frames.Enqueue(frame);
    }
    public SmartFFProcWrapper(string outputPath, uint width, uint height, uint fps, bool useHardware, MappedResource r)
    {
        _width = width;
        _height = height;
        _bytesPerPixel = 4;
        _pool = new(10, r.RowPitch, height, r.SizeInBytes);
        E = true;
        /*frames = Channel.CreateUnbounded<FFProcWrapper.FFFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true
        });*/
        frames = new();
        if (useHardware)
        {
            // Usar VideoWriter directo para hardware
            _useDirectEncoding = true;
            _videoWriter = new VideoWriter(outputPath, (int)width, (int)height, (int)fps);
            _videoWriter.Initialize("vaapi");
        }
        else
        {
            // Usar proceso FFmpeg externo (más compatible)
            _useDirectEncoding = false;
            _ffmpegProcess = CreateFFmpegProcess(outputPath, (int)width, (int)height, (int)fps);
        }
        _task = Task.Run(FFWrite);
    }

    private async Task FFWrite()
    {
        //await foreach (var frame in frames.Reader.ReadAllAsync())
        while (E)
        {
            if (!frames.TryDequeue(out FFFrame frame))
                continue;
            ReadOnlySpan<byte> memoryOwner = frame.AsSpan();
            //Console.WriteLine($"Writing frame with size {memoryOwner.Length}");

            if (_useDirectEncoding)
                _videoWriter.WriteFrame(memoryOwner);
            else
                WriteToFFmpegProcess(memoryOwner);
            //frame.Dispose();
            _pool.Return(frame);
        }
    }

    private void WriteToFFmpegProcess(ReadOnlySpan<byte> frameData)
    {
        // Implementación similar a tu FFProcWrapper original
        _ffmpegProcess.StandardInput.BaseStream.Write(frameData);
    }

    private Process CreateFFmpegProcess(string outputPath, int width, int height, int fps)
    {
        var arguments = $"-y -f rawvideo -pixel_format bgra -video_size {width}x{height} " +
                       $"-framerate {fps} -i pipe:0 -c:v libx264 -preset slow -crf 23 " +
                       $"-pix_fmt yuv420p \"{outputPath}\"";

        return Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true
        })!;
    }

    public void Dispose()
    {
        E = false;
        //frames.Writer.Complete();
        _task.Wait(); // ✅ Espera a que se vacíe la cola
        if (_useDirectEncoding)
            _videoWriter.Finish();
        if (_useDirectEncoding)
            _videoWriter?.Dispose();
        else
            _ffmpegProcess?.Dispose();
        _pool.FreeAll();
    }
}