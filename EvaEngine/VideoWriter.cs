using System;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SharpGen.Runtime.Win32;

namespace EvaEngine;

public unsafe class VideoWriter : IDisposable
{
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private SwsContext* _swsContext;
    private AVPacket* _packet;
    private AVFormatContext* _formatContext;
    private AVStream* _videoStream;
    private int _frameNumber;
    private bool _initialized;
    private string _outputPath;

    public int Width { get; }
    public int Height { get; }
    public int FPS { get; }
    void RegisterFFmpegBinaries()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var current = Environment.CurrentDirectory;
            var probe = Path.Combine("FFmpeg", "bin", Environment.Is64BitProcess ? "x64" : "x86");

            while (current != null)
            {
                var ffmpegBinaryPath = Path.Combine(current, probe);

                if (Directory.Exists(ffmpegBinaryPath))
                {
                    Console.WriteLine($"FFmpeg binaries found in: {ffmpegBinaryPath}");
                    ffmpeg.RootPath = ffmpegBinaryPath;
                    return;
                }

                current = Directory.GetParent(current)?.FullName;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            ffmpeg.RootPath = "/lib/x86_64-linux-gnu/";
        else
            throw new NotSupportedException(); // fell free add support for platform of your choose
    }

    public VideoWriter(string outputPath, int width, int height, int fps = 30)
    {
        RegisterFFmpegBinaries();
        _outputPath = outputPath;
        Width = width;
        Height = height;
        FPS = fps;
        _frameNumber = 0;

        ffmpeg.avdevice_register_all();
        ffmpeg.avformat_network_init();
    }
    private string hardwareDevice;
    public unsafe void Initialize(string hardwareDevice = "cuda")
    {
        if (_initialized) return;

        // Determinar el formato de output basado en la extensión del archivo
        string extension = Path.GetExtension(_outputPath).ToLower();
        string formatName = extension switch
        {
            ".mp4" => "mp4",
            ".mkv" => "matroska",
            ".avi" => "avi",
            _ => "matroska" // default
        };
        // Crear contexto de formato de output
        AVOutputFormat* outputFormat = ffmpeg.av_guess_format(formatName, null, null);
        if (outputFormat == null)
            throw new Exception($"Formato {formatName} no soportado");

        // Crear contexto de formato
        int result;
        fixed (AVFormatContext** a = &_formatContext)
            result = ffmpeg.avformat_alloc_output_context2(a, outputFormat, null, _outputPath);
        if (result < 0)
            throw new Exception("Error al crear contexto de output");

        // Buscar codec, fallback a software si hardware no está disponible
        AVCodec* codec = null;
        switch (hardwareDevice.ToLower())
        {
            case "cuda":
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
                break;
            case "qsv":
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_qsv");
                break;
            case "vaapi":
                codec = ffmpeg.avcodec_find_encoder_by_name("h264_vaapi");
                break;
            case "soft":
                codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
                break;
        }
        if (codec == null)
        {
            codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
                throw new Exception("Codec H264 no encontrado (ni hardware ni software)");
            Console.WriteLine($"Hardware encoder '{hardwareDevice}' not found, falling back to software H264 encoder.");
        }

        // Crear stream de video
        _videoStream = ffmpeg.avformat_new_stream(_formatContext, codec);
        if (_videoStream == null)
            throw new Exception("Error al crear video stream");

        // Configurar contexto del codec
        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
            throw new Exception("Error al allocar contexto del codec");

        _codecContext->width = Width;
        _codecContext->height = Height;
        _codecContext->time_base = new AVRational { num = 1, den = FPS };
        _codecContext->framerate = new AVRational { num = FPS, den = 1 };
        _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV444P;
        //_codecContext->bit_rate = 4000000;
        _codecContext->bit_rate = 0;
        ffmpeg.av_opt_set(_codecContext->priv_data, "crf", 23.ToString(), 0);
    
        // Configuraciones recomendadas para CRF
        ffmpeg.av_opt_set(_codecContext->priv_data, "preset", "slow", 0);
        ffmpeg.av_opt_set(_codecContext->priv_data, "tune", "film", 0);
    
        _codecContext->gop_size = 10;
        _codecContext->max_b_frames = 1;
        // Set global header flag if required by format
        if ((_formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        // Set stream time_base to match codec context
        _videoStream->time_base = _codecContext->time_base;

        // Set codec_tag to 0 for compatibility with MKV/MP4
        _codecContext->codec_tag = 0;
        _videoStream->codecpar->codec_tag = 0;

        // Configurar preset para hardware
        if (hardwareDevice.ToLower() == "cuda")
            ffmpeg.av_opt_set(_codecContext->priv_data, "preset", "slow", 0);

        // Abrir codec
        result = ffmpeg.avcodec_open2(_codecContext, codec, null);
        if (result < 0)
            throw new Exception($"Error al abrir codec: {result}");

        // Copiar parámetros del codec al stream (después de abrir el codec)
        result = ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecContext);
        if (result < 0)
            throw new Exception("Error al copiar parámetros del codec");

        // Set additional codecpar fields for compatibility
        _videoStream->codecpar->codec_id = _codecContext->codec_id;
        _videoStream->codecpar->format = (int)_codecContext->pix_fmt;
        _videoStream->codecpar->width = _codecContext->width;
        _videoStream->codecpar->height = _codecContext->height;

        // Crear frame
        _frame = ffmpeg.av_frame_alloc();
        _frame->width = Width;
        _frame->height = Height;
        _frame->format = (int)_codecContext->pix_fmt;

        result = ffmpeg.av_frame_get_buffer(_frame, 32);
        if (result < 0)
            throw new Exception("Error al allocar buffers del frame");

        // Crear contexto de conversión
        _swsContext = ffmpeg.sws_getContext(
            Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
            Width, Height, _codecContext->pix_fmt,
            ffmpeg.SWS_BILINEAR, null, null, null);

        // Crear packet
        _packet = ffmpeg.av_packet_alloc();

        // Abrir archivo de output
        result = ffmpeg.avio_open(&_formatContext->pb, _outputPath, ffmpeg.AVIO_FLAG_READ_WRITE);
        if (result < 0)
            throw new Exception("Error al abrir archivo de output");

        // Escribir header del archivo
        result = ffmpeg.avformat_write_header(_formatContext, null);
        if (result < 0)
        {
            var errBuf = new byte[1024];
            fixed (byte* errPtr = errBuf)
            {
                ffmpeg.av_strerror(result, errPtr, (ulong)errBuf.Length);
                string errMsg = System.Text.Encoding.UTF8.GetString(errBuf).TrimEnd('\0');
                throw new Exception($"Error al escribir header: {errMsg} (code {result})");
            }
        }

        _initialized = true;
    }

    public unsafe void WriteFrame(byte[] rgbaData)
    {
        if (!_initialized)
            throw new Exception("Writer no inicializado");

        if (rgbaData.Length != Width * Height * 4)
            throw new ArgumentException("Tamaño de datos RGBA incorrecto");

        // Preparar frame
        _frame->pts = _frameNumber++;

        // Convertir RGBA to NV12
        fixed (byte* rgbaPtr = rgbaData)
        {
            byte*[] srcData = { rgbaPtr };
            int[] srcLinesize = { Width * 4 };

            ffmpeg.sws_scale(_swsContext, srcData, srcLinesize, 0, Height,
                            _frame->data, _frame->linesize);
        }

        // Enviar frame al encoder
        int result = ffmpeg.avcodec_send_frame(_codecContext, _frame);
        if (result < 0)
            throw new Exception($"Error al enviar frame: {result}");

        // Recibir y escribir packets
        while (result >= 0)
        {
            result = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                break;

            if (result < 0)
                throw new Exception($"Error al recibir packet: {result}");

            // Configurar timestamp del packet
            _packet->stream_index = _videoStream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _codecContext->time_base, _videoStream->time_base);

            // Escribir packet al archivo
            result = ffmpeg.av_write_frame(_formatContext, _packet);
            if (result < 0)
                throw new Exception($"Error al escribir packet: {result}");

            ffmpeg.av_packet_unref(_packet);
        }
    }

    public unsafe void Finish()
    {
        if (!_initialized) return;

        // Flush del encoder
        int result = ffmpeg.avcodec_send_frame(_codecContext, null);
        if (result < 0)
            throw new Exception($"Error en flush: {result}");

        // Recibir todos los packets restantes
        while (result >= 0)
        {
            result = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                break;

            if (result < 0)
                throw new Exception($"Error al recibir packet en flush: {result}");

            _packet->stream_index = _videoStream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _codecContext->time_base, _videoStream->time_base);

            result = ffmpeg.av_interleaved_write_frame(_formatContext, _packet);
            if (result < 0)
                throw new Exception($"Error al escribir packet final: {result}");

            ffmpeg.av_packet_unref(_packet);
        }

        // Escribir trailer del archivo
        ffmpeg.av_write_trailer(_formatContext);
    }

    public unsafe void Dispose()
    {
        if (_initialized)
        {
            Finish();

            if (_formatContext != null && _formatContext->pb != null)
            {
                ffmpeg.avio_closep(&_formatContext->pb);
            }

            if (_formatContext != null)
            {
                ffmpeg.avformat_free_context(_formatContext);
                _formatContext = null;
            }
        }

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** a = &_frame)
                ffmpeg.av_frame_free(a);
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** a = &_codecContext)
                ffmpeg.avcodec_free_context(a);
        }

        if (_packet != null)
        {
            fixed (AVPacket** a = &_packet)
                ffmpeg.av_packet_free(a);
        }

        _initialized = false;
        GC.SuppressFinalize(this);
    }

    ~VideoWriter()
    {
        Dispose();
    }
}
