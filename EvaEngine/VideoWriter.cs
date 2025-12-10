using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using EvaEngine.ThirdParty;

using FFmpeg.AutoGen;

using SharpGen.Runtime.Win32;

namespace EvaEngine;

public unsafe class VideoWriter : IDisposable
{
    private AVCodecContext* _codecContext;
    /*private AVFrame* _frame;
    private AVFrame* _frameSw;
    private AVFrame* _frameSwFr; */
    private FramePool? _hardwareFramePool;
    private FramePool? _softwareFramePool;
    private FramePool? _intermediaryFramePool;
    private SwsContext* _swsContext;
    private AVPacket* _packet;
    private AVFormatContext* _formatContext;
    private AVStream* _videoStream;
    private readonly AVBufferRef* _hwDeviceContext;
    private int _frameNumber;
    private bool _initialized;
    private string _hardwareDevice;
    private readonly string _outputPath;


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
    private (AVPixelFormat pixelFormat, string? hwConfig) GetOptimalPixelFormat(string hardwareDevice)
    {
        return hardwareDevice switch
        {
            "vaapi" => (AVPixelFormat.AV_PIX_FMT_VAAPI, "vaapi"),
            "cuda" => (AVPixelFormat.AV_PIX_FMT_CUDA, "cuda"),
            "qsv" => (AVPixelFormat.AV_PIX_FMT_QSV, "qsv"),
            "soft" => (AVPixelFormat.AV_PIX_FMT_YUV420P, null),
            _ => (AVPixelFormat.AV_PIX_FMT_YUV420P, null)
        };
    }
    private int SafeAvOptSet(void* obj, string key, string value, int searchFlags = 0)
    {
        int result = ffmpeg.av_opt_set(obj, key, value, searchFlags);
        if (result < 0)
        {
            Console.WriteLine($"AVOption SET ERROR: {key}={value}, Error: {result}");
        }
        else if (result == 0)
        {
            Console.WriteLine($"AVOption SET: {key}={value}");
        }
        // Si es AVERROR_OPTION_NOT_FOUND, simplemente lo ignoramos silenciosamente
        return result;
    }

    private int SafeAvOptSetInt(void* obj, string key, int value, int searchFlags = 0)
    {
        int result = ffmpeg.av_opt_set_int(obj, key, value, searchFlags);
        if (result < 0)
        {
            Console.WriteLine($"AVOption SET ERROR: {key}={value}, Error: {result}");
        }
        else if (result == 0)
        {
            Console.WriteLine($"AVOption SET: {key}={value}");
        }
        // Si es AVERROR_OPTION_NOT_FOUND, simplemente lo ignoramos silenciosamente
        return result;
    }
    public unsafe void Initialize(string hardwareDevice = "cuda")
    {
        if (_initialized) return;
        _hardwareDevice = hardwareDevice.ToLower();

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
            _hardwareDevice = "soft"; // ⚠️ CRÍTICO: Actualizar la variable
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

        _codecContext->time_base = new AVRational { num = 1, den = 1000 };
        //_codecContext->time_base = new AVRational { num = 1, den = FPS };
        _codecContext->framerate = new AVRational { num = FPS, den = 1 };

        _videoStream->avg_frame_rate = new AVRational { num = FPS, den = 1 };
        _videoStream->r_frame_rate = new AVRational { num = FPS, den = 1 };
        // ✅ FORMATOS CORREGIDOS para hardwar
        var (targetPixelFormat, hwConfig) = GetOptimalPixelFormat(_hardwareDevice);

        _codecContext->pix_fmt = targetPixelFormat;
        _codecContext->bit_rate = 0;
        _codecContext->rc_max_rate = 0;   // máximo (18 Mbps)
        _codecContext->rc_buffer_size = 0;      // Buffer mínimo

        // Deshabilitar B-frames para mejor compatibilidad con VAAPI
        _codecContext->max_b_frames = 0;
        _codecContext->gop_size = 1;
        _codecContext->refs = 1;                // Solo 1 frame de referencia

        if (_hardwareDevice == "vaapi")
        {
            // VAAPI requiere configuraciones específicas
            //ffmpeg.av_opt_set(_codecContext->priv_data, "crf", "23", 0);
            SafeAvOptSetInt(_codecContext->priv_data, "quality", 5, 0);

            //SafeAvOptSetInt(_codecContext->priv_data, "bf", 0, 0);        // B-frames = 0
            SafeAvOptSet(_codecContext->priv_data, "profile", "main", 0); // Perfil
        }
        else
        {
            // Configuración normal para otros encoders
            SafeAvOptSet(_codecContext->priv_data, "preset", "slow", 0);
        }

        // Configuraciones específicas por hardware
        ConfigureHardwareEncoder(_codecContext, _hardwareDevice);

        // Configurar device context para hardware
        if (_hardwareDevice != "soft")
        {
            SetupHardwareDeviceContext(_hardwareDevice);
        }

        // Set codec_tag to 0 for compatibility with MKV/MP4
        _codecContext->codec_tag = 0; // Dejar que FFmpeg maneje los tags
        _videoStream->codecpar->codec_tag = 0;

        // Configurar preset para hardware
        if (hardwareDevice.ToLower() == "cuda")
            SafeAvOptSet(_codecContext->priv_data, "preset", "slow", 0);
        if ((_formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        _videoStream->time_base = _codecContext->time_base;
        //SafeAvOptSetInt(_codecContext->priv_data, "rc_mode", 3, 0);

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
        //_frame = CreateHardwareFrame();
        //_frameSw = CreateSoftwareFrame();
        //_frameSwFr = CreateSoftwareIntermediaryFrame();
        AVPixelFormat swFormat = _hardwareDevice switch
        {
            "vaapi" => AVPixelFormat.AV_PIX_FMT_NV12,
            "cuda" => AVPixelFormat.AV_PIX_FMT_NV12,
            "qsv" => AVPixelFormat.AV_PIX_FMT_NV12,
            "soft" => AVPixelFormat.AV_PIX_FMT_YUV420P,
            _ => AVPixelFormat.AV_PIX_FMT_YUV420P
        };

        // Create hardware frame pool
        if (_hardwareDevice != "soft" && _codecContext->hw_frames_ctx != null)
        {
            _hardwareFramePool = new FramePool(
                Width, Height,
                _codecContext->pix_fmt,
                true,
                _codecContext->hw_frames_ctx
            );
        }

        // Create software frame pool (for conversion)
        _softwareFramePool = new FramePool(Width, Height, swFormat);

        // Create intermediary BGRA frame pool  
        _intermediaryFramePool = new FramePool(Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA);

        // Crear contexto de conversión
        AVPixelFormat swsTargetFormat = _hardwareDevice switch
        {
            "vaapi" => AVPixelFormat.AV_PIX_FMT_NV12,    // NV12 para VAAPI
            "cuda" => AVPixelFormat.AV_PIX_FMT_NV12,     // NV12 para CUDA
            "qsv" => AVPixelFormat.AV_PIX_FMT_NV12,      // NV12 para QSV
            "soft" => AVPixelFormat.AV_PIX_FMT_YUV420P,  // YUV420P para software
            _ => AVPixelFormat.AV_PIX_FMT_YUV420P
        };
        /*_swsContext = ffmpeg.sws_alloc_context();
        SafeAvOptSetInt(_swsContext, "srcw", Width, 0);
        SafeAvOptSetInt(_swsContext, "srch", Height, 0);
        SafeAvOptSetInt(_swsContext, "src_format", (int)AVPixelFormat.AV_PIX_FMT_BGRA, 0);
        SafeAvOptSetInt(_swsContext, "dstw", Width, 0);
        SafeAvOptSetInt(_swsContext, "dsth", Height, 0);
        SafeAvOptSetInt(_swsContext, "dst_format", (int)swsTargetFormat, 0);
        SafeAvOptSetInt(_swsContext, "sws_flags", ffmpeg.SWS_FAST_BILINEAR, 0);
        SafeAvOptSetInt(_swsContext, "threads", Environment.ProcessorCount, 0);
        if (ffmpeg.sws_init_context(_swsContext, null, null) < 0)
        {
            ffmpeg.sws_freeContext(_swsContext);
            return;
        }*/
        /*_swsContext = ffmpeg.sws_getContext(
            Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
            Width, Height, swsTargetFormat,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);*/
        _swsContext = ffmpeg.sws_getCachedContext(
            null,
            Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
            Width, Height, swsTargetFormat,
            ffmpeg.SWS_POINT,
            null, null, null
        );


        // Crear packet
        _packet = ffmpeg.av_packet_alloc();

        // Abrir archivo de output
        result = ffmpeg.avio_open(&_formatContext->pb, _outputPath, ffmpeg.AVIO_FLAG_WRITE);
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

    private AVFrame* CreateSoftwareIntermediaryFrame()
    {
        var frame = ffmpeg.av_frame_alloc();
        frame->width = Width;
        frame->height = Height;
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;

        int result = ffmpeg.av_frame_get_buffer(frame, 0);
        if (result < 0)
            throw new Exception("Error al crear frame software temporal");

        return frame;
    }

    private void ConfigureHardwareEncoder(AVCodecContext* codecContext, string hardwareDevice)
    {
        const int crf = 23;
        switch (hardwareDevice)
        {
            case "cuda":
                // ✅ Opciones específicas de NVENC
                SafeAvOptSet(codecContext->priv_data, "rc", "vbr", 0);
                SafeAvOptSet(codecContext->priv_data, "cq", crf.ToString(), 0);
                SafeAvOptSetInt(codecContext->priv_data, "rc_mode", 0, 0); // CQP mode
                break;

            case "qsv":
                // ✅ Opciones específicas de QSV
                SafeAvOptSet(codecContext->priv_data, "preset", "medium", 0);
                SafeAvOptSetInt(codecContext->priv_data, "look_ahead", 1, 0);
                SafeAvOptSetInt(codecContext->priv_data, "global_quality", crf, 0);
                SafeAvOptSetInt(codecContext->priv_data, "qpi", crf, 0); // QP I-frames
                SafeAvOptSetInt(codecContext->priv_data, "qpp", crf, 0); // QP P-frames
                SafeAvOptSetInt(codecContext->priv_data, "qpb", crf, 0); // QP B-frames
                break;

            case "vaapi":
                // ✅ Opciones específicas de VAAPI (ya configuradas arriba)
                SafeAvOptSet(codecContext->priv_data, "quality", "5", 0);
                SafeAvOptSet(codecContext->priv_data, "compression_level", "7", 0);
                SafeAvOptSetInt(codecContext->priv_data, "qp", crf, 0);
                // O "compression_level" + "quality"
                SafeAvOptSetInt(codecContext->priv_data, "quality", 5, 0); // 1-7
                break;
            case "soft":
                // ✅ Opciones para software H264
                SafeAvOptSet(codecContext->priv_data, "preset", "slow", 0);
                SafeAvOptSet(codecContext->priv_data, "tune", "animation", 0);
                SafeAvOptSetInt(codecContext->priv_data, "crf", crf, 0);
                break;
        }
    }

    private void SetupHardwareDeviceContext(string hardwareDevice)
    {
        AVHWDeviceType deviceType = hardwareDevice switch
        {
            "cuda" => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
            "qsv" => AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
            "vaapi" => AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
            _ => throw new Exception($"Dispositivo hardware no soportado: {hardwareDevice}")
        };
        AVPixelFormat swFormat = hardwareDevice switch
        {
            "vaapi" => AVPixelFormat.AV_PIX_FMT_NV12, // VAAPI usa NV12 como formato software
            "cuda" => AVPixelFormat.AV_PIX_FMT_NV12,
            "qsv" => AVPixelFormat.AV_PIX_FMT_NV12,
            _ => AVPixelFormat.AV_PIX_FMT_YUV420P
        };

        // Actualizar el formato del codec context
        //_codecContext->pix_fmt = swFormat;

        // Obtener el método de hardware pix_fmt
        AVPixelFormat hwPixelFormat = GetHardwarePixelFormat(deviceType);
        int result;
        fixed (AVBufferRef** hw = &_hwDeviceContext)
            result = ffmpeg.av_hwdevice_ctx_create(hw, deviceType, null, null, 0);
        if (result < 0)
            throw new Exception($"Error al crear contexto de dispositivo hardware: {result}");

        // Asignar el device context al codec context
        _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceContext);
        _codecContext->hw_frames_ctx = CreateHardwareFramesContext(_hwDeviceContext, deviceType, swFormat);
    }
    private AVPixelFormat GetHardwarePixelFormat(AVHWDeviceType deviceType)
    {
        return deviceType switch
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => AVPixelFormat.AV_PIX_FMT_CUDA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => AVPixelFormat.AV_PIX_FMT_QSV,
            AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => AVPixelFormat.AV_PIX_FMT_VAAPI,
            _ => AVPixelFormat.AV_PIX_FMT_NONE
        };
    }
    private AVBufferRef* CreateHardwareFramesContext(AVBufferRef* deviceContext, AVHWDeviceType deviceType, AVPixelFormat swFormat)
    {
        AVBufferRef* framesContext = null;
        framesContext = ffmpeg.av_hwframe_ctx_alloc(deviceContext);
        if (framesContext == null)
            return null;
        AVHWFramesContext* framesCtx = (AVHWFramesContext*)framesContext->data;
        //Console.WriteLine(framesCtx == null);
        framesCtx->format = GetHardwarePixelFormat(deviceType);
        framesCtx->sw_format = swFormat; // ✅ Usar el formato software correcto
        framesCtx->width = Width;
        framesCtx->height = Height;
        framesCtx->initial_pool_size = 40;

        int result = ffmpeg.av_hwframe_ctx_init(framesContext);
        if (result < 0)
        {
            ffmpeg.av_buffer_unref(&framesContext);
            return null;
        }

        return framesContext;
    }
    private AVFrame* CreateHardwareFrame()
    {
        var frame = ffmpeg.av_frame_alloc();
        frame->width = Width;
        frame->height = Height;

        if (_hardwareDevice != "soft" && _codecContext->hw_frames_ctx != null)
        {
            // Frame hardware
            frame->hw_frames_ctx = ffmpeg.av_buffer_ref(_codecContext->hw_frames_ctx);
            frame->format = (int)_codecContext->pix_fmt;
            int result = ffmpeg.av_hwframe_get_buffer(_codecContext->hw_frames_ctx, frame, 0);
            if (result < 0)
                throw new Exception("Error al obtener buffer de frame hardware");
        }
        else
        {
            // Frame software
            frame->format = (int)_codecContext->pix_fmt;
            int result = ffmpeg.av_frame_get_buffer(frame, 0);
            if (result < 0)
                throw new Exception("Error al allocar buffers del frame software");
        }

        return frame;
    }
    [DllImport("yuv", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ARGBToNV12(
        byte* srcARGB, int srcStrideARGB,
        byte* dstY, int dstStrideY,
        byte* dstUV, int dstStrideUV,
        int width, int height);
    public unsafe void WriteFrame(ReadOnlySpan<byte> rgbaData)
    {
        if (!_initialized)
            throw new Exception("Writer no inicializado");

        if (rgbaData.Length != Width * Height * 4)
            throw new ArgumentException("Tamaño de datos RGBA incorrecto");

        // Preparar frame
        AVFrame* outputFrame = (_hardwareFramePool ?? _softwareFramePool)!.GetFrame();
        AVFrame* softwareFrame = _softwareFramePool!.GetFrame();
        //AVFrame* intermediaryFrame = _intermediaryFramePool!.GetFrame();
        if (outputFrame == null || softwareFrame == null/* || intermediaryFrame == null*/)
            throw new Exception("Failed to get frames from pools");

        outputFrame->pts = (long)(_frameNumber++ * (1000.0 / FPS));
        //outputFrame->pts = _frameNumber++;
        int result = 0;

        // Convertir RGBA al formato target
        fixed (byte* rgbaPtr = rgbaData)
        {
            byte*[] srcData = { rgbaPtr };
            int[] srcLinesize = { Width * 4 };

            if (_hardwareDevice != "soft")
            {
                //_frameSwFr->data.UpdateFrom(srcData);
                // Para hardware: necesitamos un frame software intermedio
                // AVFrame* Shenanigans
                int srcStride = Width * 4;
                /*byte* src = rgbaPtr;
                byte* dst = _frameSwFr->data[0];

                int dstStride = _frameSwFr->linesize[0];

                for (int y = 0; y < Height; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * srcStride,
                        dst + y * dstStride,
                        dstStride,
                        srcStride
                    );
                }*/
                //int srcStride = Width * 4;
                byte* dstY = softwareFrame->data[0];
                byte* dstUV = softwareFrame->data[1];

                /*ARGBToNV12(
                    rgbaPtr, srcStride,
                    dstY, softwareFrame->linesize[0],
                    dstUV, softwareFrame->linesize[1],
                    Width, Height
                );*/
                ARGBToNV12(rgbaPtr, srcStride,
                    dstY, softwareFrame->linesize[0],
                    dstUV, softwareFrame->linesize[1],
                    Width, Height);
                //ffmpeg.sws_scale_frame(_swsContext, _frameSw, _frameSwFr);

                /*ffmpeg.sws_scale(_swsContext, srcData, srcLinesize, 0, Height,
                                _frameSw->data, _frameSw->linesize);*/

                // Copiar del frame software al frame hardware
                result = ffmpeg.av_hwframe_transfer_data(outputFrame, softwareFrame, 1);
                if (result < 0)
                    throw new Exception($"Error en transferencia hardware: {result}");
            }
            else
            {
                // TODO: FIX THIS BUT ONLY IF WE GET TO RELEASE
                // Software directo
                int srcStride = Width * 4;
                byte* src = rgbaPtr;
                byte* dst = softwareFrame->data[0];

                int dstStride = softwareFrame->linesize[0];

                for (int y = 0; y < Height; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * srcStride,
                        dst + y * dstStride,
                        dstStride,
                        srcStride
                    );
                }
                /*ffmpeg.sws_scale(_swsContext, srcData, srcLinesize, 0, Height,
                                _frame->data, _frame->linesize);*/
                ffmpeg.sws_scale_frame(_swsContext, outputFrame, softwareFrame);
            }
        }

        result = ffmpeg.avcodec_send_frame(_codecContext, outputFrame);
        if (result < 0)
            throw new Exception($"Error al enviar frame: {result}");

        ProcessPendingPackets();
        _hardwareFramePool?.ReturnFrame(outputFrame);
        _softwareFramePool?.ReturnFrame(softwareFrame);
        //Console.WriteLine("WWW");
        //_intermediaryFramePool?.ReturnFrame(intermediaryFrame);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void ProcessPendingPackets()
    {
        int result;
        while ((result = ffmpeg.avcodec_receive_packet(_codecContext, _packet)) >= 0)
        {
            _packet->stream_index = _videoStream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _codecContext->time_base, _videoStream->time_base);

            ffmpeg.av_interleaved_write_frame(_formatContext, _packet);
            ffmpeg.av_packet_unref(_packet);
        }
    }

    private AVFrame* CreateSoftwareFrame()
    {
        var frame = ffmpeg.av_frame_alloc();
        frame->width = Width;
        frame->height = Height;
        AVPixelFormat swFormat = _hardwareDevice switch
        {
            "vaapi" => AVPixelFormat.AV_PIX_FMT_NV12,
            "cuda" => AVPixelFormat.AV_PIX_FMT_NV12,
            "qsv" => AVPixelFormat.AV_PIX_FMT_NV12,
            "soft" => AVPixelFormat.AV_PIX_FMT_YUV420P,
            _ => AVPixelFormat.AV_PIX_FMT_YUV420P
        };
        frame->format = (int)swFormat;

        int result = ffmpeg.av_frame_get_buffer(frame, 0);
        if (result < 0)
            throw new Exception("Error al crear frame software temporal");

        return frame;
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

        _initialized = false;
    }

    public unsafe void Dispose()
    {
        if (_initialized)
        {
            Finish();

            // Liberar recursos hardware
            if (_hwDeviceContext != null)
            {
                fixed (AVBufferRef** hw = &_hwDeviceContext)
                    ffmpeg.av_buffer_unref(hw);
            }

            // [Tu código existente de cleanup...]
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

        /*if (_frame != null)
        {
            fixed (AVFrame** a = &_frame)
                ffmpeg.av_frame_free(a);
        }

        if (_frameSw != null)
        {
            fixed (AVFrame** a = &_frameSw)
                ffmpeg.av_frame_free(a);
        }
        if (_frameSwFr != null)
        {
            fixed (AVFrame** a = &_frameSwFr)
                ffmpeg.av_frame_free(a);
        }*/
        _hardwareFramePool?.Dispose();
        _softwareFramePool?.Dispose();
        _intermediaryFramePool?.Dispose();

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

        GC.SuppressFinalize(this);
    }

    ~VideoWriter()
    {
        Dispose();
    }
}