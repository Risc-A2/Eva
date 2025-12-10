
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using EvaEngine.RenderPipe;
using EvaEngine.Renders;

using ImGuiNET;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using std;

using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

using Buffer = System.Buffer;

namespace EvaEngine;

public class Player
{
	private const int BufferCount = 3;
	private MidiFile file;
	uint width, height;
	private double midiTime;
	ImGuiRenderer _controller;
	private IRender renderer;
	private readonly RenderSettings settings;
	private Process? ffmpeg;
	private SmartFFProcWrapper Wrapper;
	private readonly VideoWriter writer;
	private DrawFromFBO pipe;
	private readonly Stream ffmpegInput;
	private byte[] Bytes;
	private Sdl2Window window;
	private CommandList CL;
	private GraphicsDevice GD;
	private ResourceFactory RF;
	private readonly FastList<IDisposable> disposeGroup = new();
	private TextRenderer text;
	private double livefps;
	private Framebuffer ffmpegFB;
	private readonly Texture[] staging = new Texture[BufferCount];
	private readonly MappedResource[] stagingMaps = new MappedResource[BufferCount];
	private CommandList CLTX;
	private Texture color;
	private double StopPoint;
	private long frameStartTime;
	private bool KDMAPI_Run;
	private readonly int deltaTimeOnScreen = 300;
	private double lastdt = 0.016666666666666666d;
	private double tempoFrameStep;
	private double lastTempo;
	private double microsecondsPerTick;
	private double mv = 1;
	private readonly int font;
	private long now;
	private readonly LinkedList<double> fpsSamples = new();
	private readonly Fence[] fence = new Fence[BufferCount];
	private int tbI;
	private bool dontRender = false;
	private bool RunningKDMAPI = false;
	private readonly string path;
	private Swapchain _swapchain;
	public Player(RenderSettings cfg, string path)
	{
		settings = cfg;
		this.path = path;
		ExtraSDL.SDL_ShowSimpleMessageBox(0x00000040, "Eva - Engine", "Warning this MIDI Player is still under development, so it will help me alot if you contributed <3", IntPtr.Zero);
		OnLoad();
		//this.midiOutput = midiOutput;
	}

	private static bool SimulateLag(long now, long frameStartTime)
	{
		return now - 10000000 < frameStartTime;
	}

#if KDMAPI_ENABLED
    private void KDMAPI_RealTimePlayback()
    {
        if (!KDMAPI.KDMAPI_Supported)
            return;
        // Docs say that 0 is Failure and 1 is success
        int o = KDMAPI.InitializeKDMAPIStream();
        if (o == 0)
        {
            KDMAPI.KDMAPI_Supported = false;
            return;
        }
        RunningKDMAPI = true;
        PlaybackEvent pe;
        SpinWait spinner = new SpinWait();

        while (KDMAPI_Run)
		{
			file.SemaphoreE.Wait();
            if (!file.Events.TryDequeue(out pe))
			{
				Console.WriteLine("No more events to playback."); // Debug
				file.SemaphoreE.Release();
            	Thread.Sleep(1); // o eventsAvailable.Wait(1) si usas el evento
				continue;
			}
			file.SemaphoreE.Release();
            //Pop a event from stack
            //Get the current time
            now = DateTime.Now.Ticks;
            //DESCRIBED IN THE FUNCTION BODY
            /*if (now - 10000000 > frameStartTime)
            {
                //Most MIDI Players have their Synthetizer event sender in the Main Thread however
                //we use a different thread than the main one so
                //to simulate this we wait until the lag spike ended
                while (!SimulateLag(now, frameStartTime))
                {
                    spinner.SpinOnce();
                }
                
                //slower or faster? IDK
            }*/
            var timeJump = (int)(((pe.pos - midiTime) * microsecondsPerTick - now + frameStartTime) / 10000);
            //After a Lag spike, we should not play older events, we could to simulate PFA even more
            //however i dont know if i should do it, so temporally this check is here until we decide
            //to remove it
            if (timeJump < -1000)
                continue;
            //Wait if the event is more ahead, does this desync if theres a tempo change in between?
            //Maybe but at the moment i dont notice any desync
            if (timeJump > 0)
            {
				Console.WriteLine("Wait. " + timeJump); // Debug
				Thread.Sleep(timeJump);
			}
			//Docs say Sends Directly a U32 to the synthetizer with buffering
			//we could use the NoBuf version to not use buffering however
			//its not supported yet, on OmniMIDIv2 / KDMAPI v2
			//so we are just gonna use buffering
			//if at somepoint KDMAPI v2 implements NoBuf on OmniMIDIv2 we will test it out and decide if use the Normal or the NoBuf version
			KDMAPI.SendDirectData((uint)pe.val);
        }
        //Reset the Stream because we are closing the APP so we should force the synthetizer to stop
        KDMAPI.ResetKDMAPIStream();
        //Terminate the Stream or else i will get Struck by 2458102 Memory Leaks
        KDMAPI.TerminateKDMAPIStream();
        RunningKDMAPI = false;
    }
#endif

	private unsafe void OnLoad()
	{
		//Create the GPU Device
		var GDO = new GraphicsDeviceOptions(false, PixelFormat.R16_UNorm, false, ResourceBindingModel.Improved
		, true, true, false);
		GD = VeldridStartup.GetPlatformDefaultBackend() switch
		{
			GraphicsBackend.Direct3D11 => GraphicsDevice.CreateD3D11(GDO),
			//GraphicsBackend.OpenGL => GraphicsDevice.CreateOpenGL(GDO),
			GraphicsBackend.OpenGL => throw new NotSupportedException("Unsupported platform for Eva Engine (OGL)"),
			GraphicsBackend.OpenGLES => throw new NotSupportedException("Unsupported platform for Eva Engine (OGLES)"),
			GraphicsBackend.Metal => GraphicsDevice.CreateMetal(GDO),
			GraphicsBackend.Vulkan => GraphicsDevice.CreateVulkan(GDO),
			_ => throw new NotSupportedException("Unsupported platform for Eva Engine"),
		};

		RF = GD.ResourceFactory;

		Console.WriteLine("OnLoad();");
		//renderer = new GeometryRender();
		renderer = new ClassicRender();

		#region MIDI
		//Load the palette
		using var palette = Image.Load<Rgba32>("noteColors.png");
		//string path = "/home/ikki/midis/In the hall of the mountain king.mid";
		//Stream s;
		//s = File.OpenRead(path);
		var s = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
		//Fast Scan the MIDI
		file = new(settings, s, palette);
		//Do whatever the hell this is
		lastTempo = file.ZerothTempo;
		microsecondsPerTick = lastTempo / file.PPQ * 10;
		tempoFrameStep = file.PPQ / lastTempo * (1000000.0 / settings.fps);
		midiTime -= tempoFrameStep * (settings.ffRender ? 0d : 3d) * settings.fps;
		StopPoint = file.TotalTicks + (tempoFrameStep * 3d * settings.fps);
		CL = RF.CreateCommandList();
		//bpm = file.BPM;
		//deltaMidi = (file.PPQ * (file.BPM / 60.0) * 0.016666666666666666d);
		#endregion
		Console.WriteLine("Init()");
		//Initialize all the stuff the renderer needs this can change so we dont know what does it do
		Sdl2Native.SDL_SetHint("SDL_APP_NAME", "Eva - Engine");
		Sdl2Native.SDL_SetHint("SDL_AUDIO_DEVICE_APP_NAME", "Eva - Engine");
		window = VeldridStartup.CreateWindow(new WindowCreateInfo(0, 0, 500, 500, WindowState.Normal, "Eva - Engine"));
		window.Resizable = false;
		var idx = Sdl2Native.SDL_GetWindowDisplayIndex(window.SdlWindowHandle);
		//Step 2 Get the current display mode using SDL_GetCurrentDisplayMode(i32, SDL_DisplayMode*)
		SDL_DisplayMode display;

		Sdl2Native.SDL_GetCurrentDisplayMode(idx, &display);

		Stream r = ResourceExtractor.Read("Resources", "Icon.bmp");
		MemoryStream ms = new();
		r.CopyTo(ms);
		byte[] iconData = ms.ToArray();
		ms.Dispose();
		r.Dispose();
		GCHandle pinnedArray = GCHandle.Alloc(iconData, GCHandleType.Pinned);
		IntPtr iconPtr = pinnedArray.AddrOfPinnedObject();

		IntPtr rw = ExtraSDL.SDL_RWFromMem(iconPtr, iconData.Length);
		//IntPtr rw = ExtraSDL.SDL_RWFromFile("Icon.bmp", "rb");

		if (rw != IntPtr.Zero)
		{
			IntPtr iconSurface = ExtraSDL.SDL_LoadBMP_RW(rw, 1);
			if (iconSurface != IntPtr.Zero)
			{
				ExtraSDL.SDL_SetWindowIcon(window.SdlWindowHandle, iconSurface);
				ExtraSDL.SDL_FreeSurface(iconSurface);
			}
			else
			{
				Console.WriteLine("Error: no se pudo cargar el BMP.");
				Console.WriteLine($"ErrCode: {ExtraSDL.SdlGetError()}");
			}
		}
		else
		{
			Console.WriteLine("Error: no se pudo abrir el archivo BMP.");
			Console.WriteLine($"ErrCode: {ExtraSDL.SdlGetError()}");
		}

		pinnedArray.Free();

		//Width calculation
		width = (uint)(display.w / 1.5);
		//Height calculation
		height = (uint)((double)width / settings.Width * settings.Height);
		//and center however this is not as good as i thought :(
		//it doesnt center perfectly i dont know why and im not gonna fix it
		int centerX = (display.w - (int)width) / 2;
		int centerY = (display.h - (int)height) / 2;

		window.Width = (int)width;
		window.Height = (int)height;

		window.X = centerX;
		window.Y = centerY;
		_swapchain = RF.CreateSwapchain(new SwapchainDescription(VeldridStartup.GetSwapchainSource(window),
		width, height, PixelFormat.R16_UNorm, false, false));
		//Create the FFMPEG Render Framebuffer (why is this created even when FFMPEG MODE IS NOT ENABLED?)
		// MSAA technically?
		Texture depth = RF.CreateTexture(new TextureDescription(settings.Width, settings.Height, 1, 1, 1,
			(PixelFormat)(_swapchain.Framebuffer.DepthTarget?.Target.Format), TextureUsage.DepthStencil, TextureType.Texture2D));
		color = RF.CreateTexture(new TextureDescription(settings.Width, settings.Height, 1, 1, 1,
			_swapchain.Framebuffer.ColorTargets[0].Target.Format, TextureUsage.RenderTarget | TextureUsage.Sampled, TextureType.Texture2D));
		for (int i = 0; i < staging.Length; i++)
		{
			var t = RF.CreateTexture(new TextureDescription(settings.Width, settings.Height, 1, 1, 1,
			_swapchain.Framebuffer.ColorTargets[0].Target.Format, TextureUsage.Staging, TextureType.Texture2D));
			staging[i] = t;
			stagingMaps[i] = GD.Map(t, MapMode.Read);
			disposeGroup.Add(t);
		}
		//FFMPEG Render
		if (settings.ffRender)
		{
			/*string args = $"-hide_banner {settings.init} ";
            if (!settings.includeAudio)
            {
                args += $"-y -f rawvideo -pix_fmt bgra -s {settings.Width}x{settings.Height} -r {settings.fps} -i - " +
                    $"-c:v {settings.codec} -vf \"{settings.filter}\" {settings.extra} output.mp4";
            }
            else
            {
                double fstep = ((double)file.PPQ / lastTempo) * (1000000d / settings.fps);
                double offset = -midiTime / fstep / settings.fps;
                offset = Math.Round(offset * 100) / 100;
                args += $"-y -f rawvideo -pix_fmt bgra -s {settings.Width}x{settings.Height} -r {settings.fps} -i - " +
                    $"-itsoffset {offset.ToString().Replace(",", ".")} -i \"{settings.audioPath}\" -c:v {settings.codec} -c:a copy -vf \"{settings.filter}\" {settings.extra} output.mp4";
            }
            ffmpeg = new Process();
            ffmpeg.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };*/

			//ffmpeg.Start();
			Wrapper = new("output.mkv", settings.Width, settings.Height, (uint)settings.fps, true, stagingMaps[0]);

			/*writer = new("output.mkv", (int)settings.Width, (int)settings.Height, (int)settings.fps);
            writer.Initialize("soft");*/
			Bytes = new byte[settings.Width * settings.Height * 4];
			CLTX = RF.CreateCommandList();

			// Some buffering yeah
			//ffmpegInput = ffmpeg.StandardInput.BaseStream;
			//ffmpegInput = new BufferedStream(ffmpeg.StandardInput.BaseStream, Bytes.Length * 10);
		}
		disposeGroup.Add(color);
		disposeGroup.Add(depth);

		_controller = new(GD, _swapchain.Framebuffer.OutputDescription, (int)width, (int)height);
		ffmpegFB = RF.CreateFramebuffer(new FramebufferDescription(depth, color));
		renderer.Initialize(file, settings, GD, RF, ffmpegFB);
		disposeGroup.Add(ffmpegFB);

		//Load the Text Renderer & Font (WHY FONT RENDERING IS LITERALLY HARD AS HELL?)
		text = new(GD, ffmpegFB, ResourceExtractor.ReadAsByte("Resources", "font.ttf"), 40);

		Console.WriteLine("DrawFromFBO();");
		//Draw from a Framebuffer
		pipe = new(GD, _swapchain, RF, RF.CreateTextureView(color));

		//Fence to syncronise FFMPEG Render or else we get artifacts
		for (int i = 0; i < staging.Length; i++)
		{
			var f = RF.CreateFence(false);
			fence[i] = f;
			disposeGroup.Add(f);
		}
		disposeGroup.Add(CL);
		if (settings.ffRender)
			disposeGroup.Add(CLTX);
		//You dont want to initialize a Synthetizer if we are in render mode
		if (!settings.ffRender)
		{
#if KDMAPI_ENABLED
            KDMAPI_Run = true;
            
            Task.Factory.StartNew(KDMAPI_RealTimePlayback, TaskCreationOptions.LongRunning);
            SpinWait spinner = new SpinWait();
			while (!RunningKDMAPI)
			{
				spinner.SpinOnce();
			}
#endif
		}
		else
		{
			KDMAPI.KDMAPI_Supported = false;
		}

		//GL.ClearColor(0.1f, 0.1f, 0.1f, 1.0f); // Fondo más oscuro para mejor contraste
	}


	public void Run()
	{
		long lastTime = Stopwatch.GetTimestamp();
		while (window.Exists)
		{
			var events = window.PumpEvents();

			long currentTime = Stopwatch.GetTimestamp();
			double dt = Stopwatch.GetElapsedTime(lastTime, currentTime).TotalSeconds;

			OnUpdateFrame(dt, events);
			OnRenderFrame(dt);

			lastTime = currentTime;
		}
		OnUnload();
	}


	// called once when game is closed
	void OnUnload()
	{
		dontRender = true; // Avoid rendering while Disposing which is VERY RISKY and some cases can Brick the GPU? or the Vulkan/D3D/OGL Driver
		KDMAPI_Run = false; // Stop KDMAPI

		GD.WaitForIdle(); // Wait for it to finish rendering

		//if (ffmpeg != null && !ffmpeg.HasExited)
		{
			// Flush and close the Input for FFMPEG Render
			try
			{/*
                ffmpegInput.Flush();
                ffmpegInput.Close();
                ffmpeg.WaitForExit();*/
				Wrapper.Dispose();
				//writer.Finish();
				//writer.Dispose();
			}
			catch { }
		}
		for (int i = 0; i < staging.Length; i++)
			GD.Unmap(staging[i]);

		//Dispose ImGui, Renderer
		//_controller.Dispose();
		renderer.Dispose();

		//Same but with other items
		foreach (var trash in disposeGroup)
			trash.Dispose();

		//Same
		text.Dispose();
		pipe.Dispose();

		GD.Dispose();  // Dispose
	}

	unsafe void OnRenderFrame(double args)
	{
		if (dontRender)
			return;
		DrawingInfo.notes = 0;
		DrawingInfo.quads = 0;
		DrawingInfo.flushCount = 0;
		DrawingInfo.verticesCount = 0;
		DrawingInfo.triangleCount = 0;
		frameStartTime = DateTime.Now.Ticks;
		CL.Begin();
		CL.SetFramebuffer(ffmpegFB);
		CL.ClearDepthStencil(0);
		CL.ClearColorTarget(0, new RgbaFloat(48 / 255f, 48 / 255f, 48 / 255f, 1f));
		renderer.Render(file, midiTime, deltaTimeOnScreen, settings, CL);

		DrawPerformanceOverlay();
		CL.End();
		GD.SubmitCommands(CL);

		CL.Begin();
		CL.SetFramebuffer(_swapchain.Framebuffer);
		CL.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 0f));
		pipe.Render(CL);

		_controller.Render(GD, CL);
		CL.End();
		GD.SubmitCommands(CL);

		tbI = (tbI + 1) % BufferCount;
		var s = staging[tbI];
		var se = stagingMaps[tbI];
		if (settings.ffRender && midiTime < StopPoint)
		{
			CLTX.Begin();
			CLTX.CopyTexture(color, s, 0, 0);
			CLTX.End();
			GD.SubmitCommands(CLTX, fence[tbI]);
			int prevTbI = (tbI + BufferCount - 1) % BufferCount; // frame anterior
			if (fence[prevTbI].Signaled)
			{
				ProcessFrame(prevTbI);
				GD.ResetFence(fence[prevTbI]);
			}
		}

		if (dontRender)
			return;
		try
		{
			GD.SwapBuffers(_swapchain);
		}
		catch { }
		lastdt = args;
		/*ErrorCode error = GL.GetError();
		if (error != ErrorCode.NoError)
		    Console.WriteLine($"OpenGL Error: {error}");*/
	}

	unsafe void ProcessFrame(int idx)
	{
		var se = stagingMaps[idx];
		//flip vertically in case of Vulkan, DirectX? maybe even metal
		bool isOriginTL = GD.IsUvOriginTopLeft;
		//FFFrame frame = Wrapper.AllocateFFFrame();

		MappedResource mapped = se; //Map staging
									//FFFrame frame = new(mapped.Data, mapped.RowPitch, (int)settings.Height, mapped.SizeInBytes);
		FFFrame frame = Wrapper.AllocateFFFrame();
		//frame.FrameFrom(se);
		nint frameMap = frame.Map();

		uint rowPitch = mapped.RowPitch;
		IntPtr srcPtr = mapped.Data;
		int height = (int)settings.Height;
		int stride = (int)frame.Stride;
		int heightMinusOne = height - 1;
		byte* srcBase = (byte*)srcPtr.ToPointer();
		byte* dstBase = (byte*)frameMap.ToPointer();
		//Console.WriteLine($"RowPitch: {rowPitch}, FrameStride: {frame.Stride}, flipY: {flipY}");
		if (isOriginTL)
		{
			if (mapped.RowPitch == frame.Stride)
			{
				// Copia directa sin flip
				//Unsafe.CopyBlock(srcBase, dstBase, (uint)frame.Size);
				Buffer.MemoryCopy(srcBase, dstBase, frame.Size, frame.Size);
			}
			else
			{
				// Flip manual secuencial (más eficiente)
				int srcStride = (int)mapped.RowPitch;
				int dstStride = (int)frame.Stride;

				for (int y = 0; y < height; y++)
				{
					int srcY = y;
					byte* src = srcBase + (srcY * srcStride);
					byte* dst = dstBase + (y * dstStride);
					//Unsafe.CopyBlock(src, dst, (uint)dstStride);
					Buffer.MemoryCopy(src, dst, dstStride, dstStride);
				}
			}
		}
		else
		{
			if (mapped.RowPitch == frame.Stride)
			{
				// Stride perfecto PERO necesita flip - haz flip optimizado
				int srcStride = (int)mapped.RowPitch;
				int dstStride = (int)frame.Stride;

				for (int y = 0; y < height; y++)
				{
					int srcY = heightMinusOne - y; // Flip vertical
					byte* src = srcBase + (srcY * srcStride);
					byte* dst = dstBase + (y * dstStride);
					//Unsafe.CopyBlock(src, dst, (uint)dstStride);
					Buffer.MemoryCopy(src, dst, dstStride, dstStride);
				}
			}
			else
			{
				// Flip manual secuencial (más eficiente)
				int srcStride = (int)mapped.RowPitch;
				int dstStride = (int)frame.Stride;

				for (int y = 0; y < height; y++)
				{
					int srcY = heightMinusOne - y;
					byte* src = srcBase + (srcY * srcStride);
					byte* dst = dstBase + (y * dstStride);
					//Unsafe.CopyBlock(src, dst, (uint)dstStride);
					Buffer.MemoryCopy(src, dst, dstStride, dstStride);
				}
			}
		}
		/*if (mapped.RowPitch == frame.Stride && isOriginTL)
        {
        	// Copia directa sin flip
        	Buffer.MemoryCopy(srcBase, dstBase, frame.Size, frame.Size);
        }
        else
        {
        	// Flip manual secuencial (más eficiente)
        	int srcStride = (int)mapped.RowPitch;
        	int dstStride = (int)frame.Stride;
        
        	for (int y = 0; y < height; y++)
        	{
        		int srcY = isOriginTL ? y : heightMinusOne - y;
        		byte* src = srcBase + (srcY * srcStride);
        		byte* dst = dstBase + (y * dstStride);
        		Buffer.MemoryCopy(src, dst, dstStride, dstStride);
        	}
        }*/

		/*for (int y = 0; y < settings.Height; y++)
        {
            int srcOffset = (int)(y * rowPitch); //get Source Offset
            //Get Destionation Offset
            int dstOffset = flipY ? (int)((settings.Height - 1 - y) * settings.Width * 4) : (int)(y * settings.Width * 4);
            //Cross fingers and hope it copies it correctly
            //C.memcpy(ptr.ToPointer(), frameMap.pointer, srcOffset, dstOffset,
            //    (nint)(settings.Width * 4));
            var E = new ReadOnlySpan<byte>((ptr + srcOffset).ToPointer(), (int)(settings.Width * 4));
            var A = new Span<byte>((frameMap + dstOffset).ToPointer(), (int)(settings.Width * 4));
            E.CopyTo(A);
            //Marshal.Copy(ptr + srcOffset, (int)frameMap.safepointer, dstOffset, (int)settings.Width * 4);
        }*/
		//Unmap to avoid Memory Leak
		Wrapper.WriteFrame(frame);
		//GD.Unmap(s);
	}
	private void PerformControlledGC()
	{
		GC.Collect();
		//GC.WaitForPendingFinalizers();
	}
	StringBuilder sb = new();
	private void DrawPerformanceOverlay()
	{
		float width = _swapchain.Framebuffer.Width;
		float height = _swapchain.Framebuffer.Height;

		float x = 0f;
		float y = 0f;
		sb.Clear();
		double avgFps = fpsSamples.Average();
		sb.AppendLine($"FPS: {avgFps:F1} (live: {livefps:F1})");
#if DEBUG
		sb.AppendLine($"Flushes: {DrawingInfo.flushCount:N0}");
		sb.AppendLine($"Notes rendered: {DrawingInfo.notes:N0}");
		sb.AppendLine($"Quads rendered: {DrawingInfo.quads:N0}");
		sb.AppendLine($"Triangles rendered: {DrawingInfo.triangleCount:N0}");
		sb.AppendLine($"Vertices rendered: {DrawingInfo.verticesCount:N0}");
#endif
		text.DrawText(CL, ffmpegFB, sb.ToString(), new(x, y), RgbaFloat.Red, 1f);
		/*text.DrawText(CL, ffmpegFB, $"FPS: {livefps:F1}\nFlushes: {DrawingInfo.flushCount:N0}\n" +
							   $"Notes rendered: {DrawingInfo.notes:N0}\nQuads rendered: {DrawingInfo.quads:N0}\n" +
							   $"Triangles rendered: {DrawingInfo.triangleCount:N0}\nVertices rendered: {DrawingInfo.verticesCount:N0}", new(x, y), RgbaFloat.Red, 1f);*/
		ImGui.Begin("Performance", ImGuiWindowFlags.HorizontalScrollbar);
		if (ImGui.Button("GC Now"))
		{
			PerformControlledGC();
		}

		// Estadísticas básica
		ImGui.Text($"FPS: {avgFps:F2}");
		ImGui.Text($"FPS Aprox: {livefps:F2}");
#if DEBUG
		ImGui.Text($"Flushes: {DrawingInfo.flushCount:N0}");
		ImGui.Text($"Notes rendered: {DrawingInfo.notes:N0}");
		ImGui.Text($"Quads rendered: {DrawingInfo.quads:N0}");
		ImGui.Text($"Triangles rendered: {DrawingInfo.triangleCount:N0}");
#endif
		//ImGui.Image(_controller.GetOrCreateImGuiBinding(GD.ResourceFactory, text.textureV), new(ImGui.GetWindowWidth(), ImGui.GetWindowHeight() - ImGui.GetCursorPosY()));
		ImGui.End();
	}

	// called every frame. All updating happens here
	void OnUpdateFrame(double args, InputSnapshot snap)
	{
		//deltaMidi = (file.PPQ * (bpm / 60.0) * lastdt);
		mv = 1;
		if (!settings.ffRender)
		{
			mv = (lastdt * TimeSpan.TicksPerSecond) / microsecondsPerTick / tempoFrameStep;
			if (mv > settings.fps / 4d)
				mv = settings.fps / 4d;
		}
		while (file.TempoChanges.First != null && midiTime + (tempoFrameStep * mv) > file.TempoChanges.First.Ticks)
		{
			var t = file.TempoChanges.Pop();
			if (t.Tempo == 0)
			{
				Console.WriteLine("Zero tempo event encountered, ignoring");
				continue;
			}
			var _t = ((t.Ticks) - midiTime) / (tempoFrameStep * mv);
			mv *= 1 - _t;
			tempoFrameStep = ((double)file.PPQ / t.Tempo) * (1000000.0 / settings.fps);
			lastTempo = t.Tempo;
			midiTime = t.Ticks;
			microsecondsPerTick = lastTempo / file.PPQ * 10;
		}
		while (file.ColorChanges.First != null && file.ColorChanges.First.pos < midiTime)
		{
			var c = file.ColorChanges.Pop();
			if (c.channel == 0x7F)
			{
				for (int i = 0; i < 16; i++)
				{
					c.track.NoteColors[i].left = c.col1;
					c.track.NoteColors[i].right = c.col2;
					c.track.NoteColors[i].isDefault = false;
				}
			}
			else
			{
				c.track.NoteColors[c.channel].left = c.col1;
				c.track.NoteColors[c.channel].right = c.col2;
				c.track.NoteColors[c.channel].isDefault = false;
			}
		}

		file.Update(midiTime);
		midiTime += mv * tempoFrameStep;

		if (settings.ffRender)
		{
			mv = (lastdt * TimeSpan.TicksPerSecond) / microsecondsPerTick / tempoFrameStep;
			if (mv > settings.fps / 4d)
				mv = settings.fps / 4d;
		}
		if (mv < 1)
			mv = 1;
		//if (midiTime + deltaTimeOnScreen + (tempoFrameStep * 10 * mv) > file.currentSyncTime)
		if (midiTime + deltaTimeOnScreen + tempoFrameStep > file.currentSyncTime)
		{
			//file.ParseUpTo(midiTime + deltaTimeOnScreen + (tempoFrameStep * 20 * mv));
			file.ParseUpTo(midiTime + deltaTimeOnScreen + (tempoFrameStep * mv));
		}
		_controller.Update((float)args, snap);
		double currentFps = 1.0 / args;
		livefps = (livefps * 2 + (10000000.0 / (args * TimeSpan.TicksPerSecond))) / 3;
		fpsSamples.AddLast(currentFps);
		if (fpsSamples.Count > 100)
			fpsSamples.RemoveFirst();
		//Console.Write($"\rProgress: {(midiTime / file.TotalTicks):P2}");
		//_controller.Update((float)args, snap);
	}

}