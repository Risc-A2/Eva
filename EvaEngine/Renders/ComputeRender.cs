using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Veldrid.SPIRV;

namespace EvaEngine.Renders;

public class ComputeRender : IRender
{
    bool[] m_BKM = new bool[12]
    {
        false, true, false, true, false, false,
        true, false, true, false, true, false
    };
    bool isBlackNote(int n)
	{
		n = n % 12;
		return m_BKM[n];
	}
    int quadBufferLength = 75000;
    Quad[] quads;
    public bool Initialized { get; set; }
    private FastList<IDisposable> disposables = new();
    public void Dispose()
    {
        foreach (var disposable in disposables)
        {
            disposable.Dispose();
        }
    }
    private Texture outputTexture;
    private Texture FBT;
    public void Initialize(MidiFile file, RenderSettings settings, GraphicsDevice GD, ResourceFactory RF, Framebuffer FB)
    {
        Initialized = true;
        FBT = FB.ColorTargets[0].Target;
        var shader = RF.CreateFromSpirv(new ShaderDescription(ShaderStages.Compute,
        ResourceExtractor.ReadAsByte("Resources", "ComputeRender_CS.spv"), "main"));
        quadBuffer = RF.CreateBuffer(new BufferDescription(
        (uint)(Marshal.SizeOf<Quad>() * quadBufferLength),
        BufferUsage.StructuredBufferReadOnly | BufferUsage.Dynamic, (uint)Marshal.SizeOf<Quad>()));
        quads = new Quad[quadBufferLength];
        outputTexture = RF.CreateTexture(TextureDescription.Texture2D(
        FB.Width, FB.Height, 1, 1, FBT.Format,
        TextureUsage.Sampled | TextureUsage.Storage));
        resourceLayout = RF.CreateResourceLayout(
        new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("OutputImage", ResourceKind.TextureReadWrite, ShaderStages.Compute),
            new ResourceLayoutElementDescription("QuadBuffer", ResourceKind.StructuredBufferReadOnly, ShaderStages.Compute)
        ));
        resset = RF.CreateResourceSet(new ResourceSetDescription(resourceLayout, [outputTexture, quadBuffer]));

        pipeline = RF.CreateComputePipeline(new ComputePipelineDescription(
            shader,
            new[] { resourceLayout },
            1, 1, 1)); // Threads por grupo: 2x2x1
        disposables.Add(pipeline);
        disposables.Add(resourceLayout);
        disposables.Add(outputTexture);
        disposables.Add(quadBuffer);
        disposables.Add(shader);
        disposables.Add(resset);
        blackKeys = new bool[257];
        int firstNote = 0;
        int lastNote = 128;
        for (int i = 0; i < blackKeys.Length; i++) blackKeys[i] = isBlackNote(i);
        int b = 0;
        int w = 0;
        for (int i = 0; i < keynum.Length; i++)
        {
            if (blackKeys[i]) keynum[i] = b++;
            else keynum[i] = w++;
        }
        float knmfn = keynum[firstNote];
        float knmln = keynum[lastNote - 1];
        if (blackKeys[firstNote]) knmfn = keynum[firstNote - 1] + 0.5f;
        if (blackKeys[lastNote - 1]) knmln = keynum[lastNote] - 0.5f;
        for (int i = 0; i < 257; i++)
        {
            if (!blackKeys[i])
            {
                x1array[i] = (keynum[i] - knmfn) / (knmln - knmfn + 1);
                wdtharray[i] = 1.0f / (knmln - knmfn + 1);
            }
            else
            {
                int _i = i + 1;
                wdth = 0.64f / (knmln - knmfn + 1);
                int bknum = keynum[i] % 5;
                float offset = wdth / 2f;
                if (bknum == 0) offset += offset * 0.4f;
                if (bknum == 2) offset += offset * 0.4f;
                if (bknum == 1) offset -= offset * 0.4f;
                if (bknum == 4) offset -= offset * 0.4f;

                //if (bknum == 0 || bknum == 2)
                //{
                //    offset *= 1.3;
                //}
                //else if (bknum == 1 || bknum == 4)
                //{
                //    offset *= 0.7;
                //}
                x1array[i] = (keynum[_i] - knmfn) / (knmln - knmfn + 1) - offset;
                wdtharray[i] = wdth;
            }
        }

        kbfirstNote = (byte)firstNote;
        kblastNote = (byte)lastNote;
        if (blackKeys[firstNote]) kbfirstNote--;
        if (blackKeys[lastNote - 1]) kblastNote++;
        for (int i = 0; i < 256; i++) keyColors[i] = new();
        for (int k = 0; k <= kblastNote; k++)
        {
            if (isBlackNote(k))
                _origColors[k] = O1;
            else
                _origColors[k] = O2;
        }
    }

	private RgbaFloatEva O1 = new RgbaFloatEva(0f, 0f, 0f, 1f);
	private RgbaFloatEva O2 = new RgbaFloatEva(1f, 1f, 1f, 1f);
    bool[] blackKeys = new bool[257];
	float[] x1array = new float[257];
	float[] wdtharray = new float[257];
	private byte kbfirstNote;
	private float wdth;
	private byte kblastNote;
	private RgbaFloatEva[] keyColors = new RgbaFloatEva[514];
	private RgbaFloatEva[] origColors = new RgbaFloatEva[257];
    private RgbaFloatEva[] _origColors = new RgbaFloatEva[257];
	private RgbaFloatEva O3 = new RgbaFloatEva(0f, 0f, 0f, 0f);
	private bool[] keyPressed = new bool[257];
	int[] keynum = new int[257];
    DeviceBuffer quadBuffer;
    ResourceLayout resourceLayout;
    Pipeline pipeline;
    ResourceSet resset;
    int quadcount = 0;
    void Flush(CommandList CL)
    {
        if (quadcount > 0)
        {
            CL.UpdateBuffer(quadBuffer, 0, ref quads[0], (uint)(quadcount * Marshal.SizeOf<Quad>()));
            CL.Dispatch((uint)quadcount, 1, 1);
            quadcount = 0;
        }
    }
	private float pianoHeight = .151f;
	private int dt = -1;
	private float paddingx;
	private float paddingy;
	private float notePosFactor;
	private bool blackAbove;
	private float pixelsPerSecond;
	private float scwidth;
	private float sepwdth;

    public void Render(MidiFile f, double midiTime, int deltaTimeOnScreen, RenderSettings settings, CommandList CL)
    {

        if (dt == -1 || dt != deltaTimeOnScreen)
        {
            dt = deltaTimeOnScreen;
            paddingx = 0.001f;
            paddingy = paddingx * settings.Width / settings.Height;
            notePosFactor = 1f / dt * (1f - pianoHeight); // Zenith
            blackAbove = settings.blackNotesAbove;
            pixelsPerSecond = (1f - pianoHeight) / dt;  // Escala vertical por unidad de tiempo
            scwidth = settings.Width;
            sepwdth = (float)Math.Round(wdtharray[0] * scwidth / 20f);
            if (sepwdth == 0) sepwdth = 1;
        }
        Array.Copy(_origColors, origColors, 257);
        for (int i = 0; i < 514; i++)
        {
            keyColors[i] = O3;
        }

        Array.Clear(keyPressed);

        quadcount = 0;
        CL.SetPipeline(pipeline);
        CL.SetComputeResourceSet(0, resset);
        float r, g, b, a, r2, g2, b2, a2, r3, g3, b3, a3;
        float x1;
        float x2;
        float y1;
        float y2;



        #region Keyboard
        float topRedStart = pianoHeight;
        float topRedEnd = pianoHeight;
        float topBarEnd = pianoHeight;

        float wEndUpT = pianoHeight * 0.03f + pianoHeight * 0.020f;
        float wEndUpB = pianoHeight * 0.03f + pianoHeight * 0.005f;
        float wEndDownT = pianoHeight * 0.01f;
        float bKeyEnd = pianoHeight * .345f;
        float bKeyDownT = topBarEnd + pianoHeight * 0.015f;
        float bKeyDownB = bKeyEnd + pianoHeight * 0.015f;
        float bKeyUpT = topBarEnd + pianoHeight * 0.045f;
        float bKeyUpB = bKeyEnd + pianoHeight * 0.045f;

        float bKeyUSplitLT = pianoHeight * 0.78f;
        float bKeyUSplitRT = pianoHeight * 0.71f;
        float bKeyUSplitLB = pianoHeight * 0.65f;
        float bKeyUSplitRB = pianoHeight * 0.58f;
        float keySpacing = 0;


        float ox1, ox2, oy1, oy2, ix1, ix2, iy1, iy2;
        y2 = 0;
        y1 = topBarEnd;
        for (int n = kbfirstNote; n < kblastNote; n++)
        {
            if (blackKeys[n]) continue;

            x1 = x1array[n];
            wdth = wdtharray[n];
            x2 = x1 + wdth;

            var coll = keyColors[n * 2];
            var colr = keyColors[n * 2 + 1];
            var origcol = origColors[n];

            // Calcular colores mezclados
            float blendfac = coll.A;
            float revblendfac = 1 - blendfac;
            r = coll.R * blendfac + origcol.R * revblendfac;
            g = coll.G * blendfac + origcol.G * revblendfac;
            b = coll.B * blendfac + origcol.B * revblendfac;
            a = 1;

            blendfac = colr.A;
            revblendfac = 1 - blendfac;
            r2 = colr.R * blendfac + origcol.R * revblendfac;
            g2 = colr.G * blendfac + origcol.G * revblendfac;
            b2 = colr.B * blendfac + origcol.B * revblendfac;
            a2 = 1;

            if (keyPressed[n])
            {
                // Tecla blanca presionada
                AddQuad(CL,
                    x1, wEndDownT, x2, wEndDownT, x2, y1, x1, y1,
                    new RgbaFloatEva(r, g, b, a));



                // Parte inferior de la tecla
                AddQuad(CL,
                    x1, y2, x2, y2, x2, wEndDownT, x1, wEndDownT,
                    new RgbaFloatEva(r, g, b, a));


            }
            else
            {
                // Tecla blanca no presionada
                AddQuad(CL,
                    x1, wEndUpT, x2, wEndUpT, x2, y1, x1, y1,
                    new RgbaFloatEva(r, g, b, a));



                // Parte media de la tecla
                AddQuad(CL,
                    x1, wEndUpB, x2, wEndUpB, x2, wEndUpT, x1, wEndUpT,
                    new RgbaFloatEva(0.529f, 0.529f, 0.529f, 1f));



                // Parte inferior de la tecla
                AddQuad(CL,
                    x1, y2, x2, y2, x2, wEndUpB, x1, wEndUpB,
                    new RgbaFloatEva(0.615f, 0.615f, 0.615f, 1f));


            }

            // Separador entre teclas
            float separatorX1 = (float)Math.Floor(x1 * scwidth - sepwdth / 2f) / scwidth;
            float separatorX2 = (float)Math.Floor(x1 * scwidth + sepwdth / 2f) / scwidth;
            if (separatorX1 == separatorX2) separatorX2++;

            AddQuad(CL,
                separatorX1, y2, separatorX2, y2, separatorX2, y1, separatorX1, y1,
                new RgbaFloatEva(0.0431f, 0.0431f, 0.0431f, 1f));

        }
        for (int n = kbfirstNote; n < kblastNote; n++)
        {
            if (!blackKeys[n]) continue;

            ox1 = x1array[n];
            wdth = wdtharray[n];
            ox2 = ox1 + wdth;
            ix1 = ox1 + wdth / 8;
            ix2 = ox2 - wdth / 8;

            var coll = keyColors[n * 2];
            var colr = keyColors[n * 2 + 1];
            var origcol = origColors[n];

            // Calcular colores (similar a teclas blancas)
            // ... (omitiendo código repetitivo de mezcla de colores)
            float blendfac = coll.A;
            float revblendfac = 1 - blendfac;
            coll.R = coll.R * blendfac + origcol.R * revblendfac;
            coll.G = coll.G * blendfac + origcol.G * revblendfac;
            coll.B = coll.B * blendfac + origcol.B * revblendfac;
            coll.A = 1;

            r = coll.R;
            g = coll.G;
            b = coll.B;
            a = coll.A;
            blendfac = colr.A;
            revblendfac = 1 - blendfac;
            colr.R = colr.R * blendfac + origcol.R * revblendfac;
            colr.G = colr.G * blendfac + origcol.G * revblendfac;
            colr.B = colr.B * blendfac + origcol.B * revblendfac;
            colr.A = 1;

            r2 = colr.R;
            g2 = colr.G;
            b2 = colr.B;
            a2 = colr.A;

            r3 = (coll.R + colr.R) / 2;
            g3 = (coll.G + colr.G) / 2;
            b3 = (coll.B + colr.B) / 2;
            a3 = 1;

            if (!keyPressed[n])
            {
                // Tecla negra no presionada
                // Parte superior media
                AddQuad(CL,
                    ix1, bKeyUSplitLT, ix2, bKeyUSplitRT, ix2, bKeyUpT, ix1, bKeyUpT,
                    new RgbaFloatEva(r, g, b, a));



                // Parte media central
                AddQuad(CL,
                    ix1, bKeyUSplitLB, ix2, bKeyUSplitRB, ix2, bKeyUSplitRT, ix1, bKeyUSplitLT,
                    new RgbaFloatEva(r, g, b, a));



                // Parte inferior media
                AddQuad(CL,
                    ix1, bKeyUpB, ix2, bKeyUpB, ix2, bKeyUSplitRB, ix1, bKeyUSplitLB,
                    new RgbaFloatEva(r, g, b, a));



                // Lado izquierdo
                AddQuad(CL,
                    ox1, bKeyEnd, ix1, bKeyUpB, ix1, bKeyUpT, ox1, topBarEnd,
                    new RgbaFloatEva(r, g, b, a));



                // Lado derecho
                AddQuad(CL,
                    ox2, bKeyEnd, ix2, bKeyUpB, ix2, bKeyUpT, ox2, topBarEnd,
                    new RgbaFloatEva(r, g, b, a));



                // Parte inferior
                AddQuad(CL,
                    ox1, bKeyEnd, ox2, bKeyEnd, ix2, bKeyUpB, ix1, bKeyUpB,
                    new RgbaFloatEva(r, g, b, a));


            }
            else
            {
                // Tecla negra presionada - Versión completa con AddQuad

                // Middle Top (Parte superior media)
                AddQuad(CL,
                    ix1, bKeyUSplitLT, ix2, bKeyUSplitRT, ix2, bKeyDownT, ix1, bKeyDownT,
                    new RgbaFloatEva(r3, g3, b3, a3));



                // Middle Middle (Parte media central)
                AddQuad(CL,
                    ix1, bKeyUSplitLB, ix2, bKeyUSplitRB, ix2, bKeyUSplitRT, ix1, bKeyUSplitLT,
                    new RgbaFloatEva(r3, g3, b3, a3));



                // Middle Bottom (Parte inferior media)
                AddQuad(CL,
                    ix1, bKeyDownB, ix2, bKeyDownB, ix2, bKeyUSplitRB, ix1, bKeyUSplitLB,
                    new RgbaFloatEva(r, g, b, a));



                // Left (Lado izquierdo)
                AddQuad(CL,
                    ox1, bKeyEnd, ix1, bKeyDownB, ix1, bKeyDownT, ox1, topBarEnd,
                    new RgbaFloatEva(r, g, b, a));



                // Right (Lado derecho)
                AddQuad(CL,
                    ox2, bKeyEnd, ix2, bKeyDownB, ix2, bKeyDownT, ox2, topBarEnd,
                    new RgbaFloatEva(r, g, b, a));



                // Bottom (Parte inferior)
                AddQuad(CL,
                    ox1, bKeyEnd, ox2, bKeyEnd, ix2, bKeyDownB, ix1, bKeyDownB,
                    new RgbaFloatEva(r, g, b, a));


            }
        }
        #endregion
        //FlushInstancedQuads();


        Flush(CL);
        CL.CopyTexture(outputTexture, FBT);
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Quad
    {
        public Vector2 Position;
        public Vector2 Size;
        public RgbaFloatEva Color;
    }
    private void AddQuad(CommandList CL, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4, 
		RgbaFloatEva color)
	{
        quads[quadcount].Position = new Vector2(x1 * outputTexture.Width, y1 * outputTexture.Height);
        quads[quadcount].Size = new Vector2((x2 - x1) * outputTexture.Width, (y3 - y1) * outputTexture.Height);
        quads[quadcount].Color = color;
        quadcount++;
        if (quadcount >= quadBufferLength)
        {
            Flush(CL);
        }
	}

}
