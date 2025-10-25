using System;
using System.Numerics;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;
using Veldrid.SPIRV;

namespace EvaEngine.Renders;

public class GeometryRender : IRender
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
    public unsafe void Initialize(MidiFile file, RenderSettings settings, GraphicsDevice GD, ResourceFactory RF, Framebuffer FB)
    {
        Initialized = true;
        var shaderG = RF.CreateFromSpirv(new ShaderDescription(ShaderStages.Geometry,
        ResourceExtractor.ReadAsByte("Resources", "GeometryRender_GS.gs"), "main"));
        var shaderV = RF.CreateFromSpirv(new ShaderDescription(ShaderStages.Vertex,
        ResourceExtractor.ReadAsByte("Resources", "GeometryRender_VS.vs"), "main"));
        var shaderF = RF.CreateFromSpirv(new ShaderDescription(ShaderStages.Fragment,
        ResourceExtractor.ReadAsByte("Resources", "GeometryRender_FS.fs"), "main"));
        var a = Image<Rgba32>.Load<Rgba32>(ResourceExtractor.ReadAsByte("Resources", "GeometryRender_T.png"));
        var t = RF.CreateTexture(TextureDescription.Texture2D((uint)a.Width, (uint)a.Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
        a.DangerousTryGetSinglePixelMemory(out var mem);
        fixed (void* pin = &MemoryMarshal.GetReference(mem.Span))
            GD.UpdateTexture(t, (IntPtr)pin, (uint)((sizeof(byte) * 4) * a.Width * a.Height),
                        0,
                        0,
                        0,
                        (uint)a.Width,
                        (uint)a.Height,
                        1,
                        (uint)0,
                        0);
        var v = RF.CreateTextureView(new TextureViewDescription(t, PixelFormat.R8_G8_B8_A8_UNorm));

        var layout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.Position,
                VertexElementFormat.Float2),
            new VertexElementDescription("Size", VertexElementSemantic.Position, VertexElementFormat.Float2),
            new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new("flags", VertexElementFormat.UInt1, VertexElementSemantic.TextureCoordinate)
        );
        resourceLayout = RF.CreateResourceLayout(new ResourceLayoutDescription(
        // Agrega aquí tus recursos uniformes si los necesitas
        new ResourceLayoutElementDescription("Tex", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
        new ResourceLayoutElementDescription("Tex", ResourceKind.Sampler, ShaderStages.Fragment)
        ));
        resourceSet = RF.CreateResourceSet(new(resourceLayout, v, GD.LinearSampler));
        var shaderSet = new ShaderSetDescription(
            new[] { layout },
            [shaderV, shaderG, shaderF]);
        quadBuffer = RF.CreateBuffer(new BufferDescription(
        (uint)(Marshal.SizeOf<Quad>() * quadBufferLength),
        BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        quads = new Quad[quadBufferLength];
        pipeline = RF.CreateGraphicsPipeline(new GraphicsPipelineDescription(
BlendStateDescription.SingleOverrideBlend,
new DepthStencilStateDescription(depthTestEnabled: true,
                depthWriteEnabled: true,
                comparisonKind: ComparisonKind.LessEqual),
new RasterizerStateDescription(cullMode: FaceCullMode.None,
                fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false), PrimitiveTopology.PointList,
            shaderSet,
            [resourceLayout], FB.OutputDescription));
        disposables.Add(pipeline);
        disposables.Add(quadBuffer);
        disposables.Add(resourceLayout);
        disposables.Add(shaderG);
        disposables.Add(shaderF);
        disposables.Add(shaderV);
        disposables.Add(t);
        disposables.Add(v);
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
    private ResourceSet resourceSet;
    Pipeline pipeline;
    int quadcount = 0;
    void Flush(CommandList CL)
    {
        if (quadcount > 0)
        {
            CL.UpdateBuffer(quadBuffer, 0, ref quads[0], (uint)(quadcount * Marshal.SizeOf<Quad>()));
            CL.SetVertexBuffer(0, quadBuffer);
            CL.Draw((uint)quadcount, 1, 0, 0);
            DrawingInfo.quads += quadcount;
            DrawingInfo.verticesCount += quadcount;
            DrawingInfo.triangleCount += quadcount * 2;
            DrawingInfo.flushCount++;
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
    public static void BlendColorSIMD(ref RgbaFloatEva orig, RgbaFloatEva src)
    {
        var srcVec = new Vector4(src.R, src.G, src.B, src.A);
        var dstVec = new Vector4(orig.R, orig.G, orig.B, orig.A);

        var a = srcVec.W;
        var oneMinusA = 1f - a;

        var outRGB = new Vector3(
            srcVec.X * a + dstVec.X * oneMinusA,
            srcVec.Y * a + dstVec.Y * oneMinusA,
            srcVec.Z * a + dstVec.Z * oneMinusA
        );

        orig.R = outRGB.X;
        orig.G = outRGB.Y;
        orig.B = outRGB.Z;
        orig.A = 1f; // forzado
    }
    void RenderNote(MidiNote n, float d, float renderCutoff, CommandList CL, RenderSettings settings)
    {
        float y1, y2, x2;
        var k = n.Key;
        /*
		x1 = x1array[k];
		wdth = wdtharray[k];
		x2 = x1 + wdth;*/
        ref readonly var x1 = ref x1array[k];
        ref readonly var wdth = ref wdtharray[k];
        x2 = x1 + wdth;
        float timeFromEnd = renderCutoff - n.EndTime;    // Tiempo desde el final de la nota
        float timeFromStart = renderCutoff - n.StartTime; // Tiempo desde el inicio de la nota
                                                          //y1 = 1 - (renderCutoff - n.EndTime) * notePosFactor;

        if (!n.hasEnd)
            y1 = 1f + paddingy; // sometimes When this happens the outline appears at the top of the screen when it shouldn't so we add the Padding Y
        else
        {
            y1 = 1f - timeFromEnd * pixelsPerSecond;
            if (y1 > 1)
                y1 = 1;
        }
        //y2 = 1 - (renderCutoff - n.StartTime) * notePosFactor;
        y2 = 1f - timeFromStart * pixelsPerSecond;
        //y1 = Math.Clamp(y1, pianoHeight, 1);
        //y2 = Math.Clamp(y2, pianoHeight, 1);
        if (y2 < pianoHeight)
            y2 = pianoHeight;

        var cc = n.Color;
        var coll = cc.left;
        var colr = cc.right;
        if (n.StartTime < d)
        //if (false)
        {
            ref var origcoll = ref keyColors[k * 2];
            ref var origcolr = ref keyColors[k * 2 + 1];
            if (!blackKeys[k])
            {
                BlendColorSIMD(ref origcoll, coll);
                BlendColorSIMD(ref origcolr, colr);
            }
            else
            {
                origcoll = coll;
                origcolr = colr;
            }
            keyPressed[k] = true;
        }
        //float pixels = (y1 - y2) * settings.Height;
        //float minNoteHeight = 0.001f * (1f / dt); // Scale threshold with deltaTimeOnScreen
        if (y1 - y2 < (paddingy))
        {
            return; // Skip small notes
        }

        DrawingInfo.notes++;


        AddQuad(CL, x2, y2, x2, y1, x1, y1, x1, y2, coll, 6);

        quadcount++;/*
        //float pixels = (y1 - y2) * settings.Height;
        if (y1 - y2 > paddingy * 2)
        //if (pixels > 15f)
        //if (false)
        {
            //x1 += paddingx;
            x2 -= paddingx;
            y1 -= paddingy;
            y2 += paddingy;

            AddQuad(CL, x2, y2, x2, y1, x1 + paddingx, y1, x1 + paddingx, y2, coll);

            quadcount++;
        }*/
    }

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
        CL.SetGraphicsResourceSet(0, resourceSet);
        float r, g, b, a, r2, g2, b2, a2, r3, g3, b3, a3;
        float x1;
        float x2;
        float y1;
        float y2;
        float renderCutoff = (float)midiTime + dt;
        float d = (float)midiTime;
        //foreach (var t in f.Tracks)
        {
            if (blackAbove)
            {
                foreach (var n in f.Notes)
                {
                    if (n.EndTime >= d || !n.hasEnd)
                    {
                        if (n.StartTime < renderCutoff)
                        {
                            var k = n.Key;
                            bool isBlack = blackKeys[k];
                            if (isBlack)
                                continue;
                            RenderNote(n, d, renderCutoff, CL, settings);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                foreach (var n in f.Notes)
                {
                    if (n.EndTime >= d || !n.hasEnd)
                    {
                        if (n.StartTime < renderCutoff)
                        {
                            var k = n.Key;
                            bool isBlack = blackKeys[k];
                            if (!isBlack)
                                continue;
                            RenderNote(n, d, renderCutoff, CL, settings);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
            else
            {
                foreach (var n in f.Notes)
                {
                    if (n.EndTime >= d || !n.hasEnd)
                    {
                        if (n.StartTime < renderCutoff)
                        {
                            RenderNote(n, d, renderCutoff, CL, settings);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }



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
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Quad
    {
        public Vector2 Position;
        public Vector2 Size;
        public RgbaFloatEva Color;
        public uint flags;
    }
    private void AddQuad(CommandList CL, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        RgbaFloatEva color, uint f = 6)
    {
        if (quadcount >= quadBufferLength)
        {
            Flush(CL);
        }

        // Calcular posición y tamaño correctos para el geometry shader
        float minX = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
        float minY = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
        float maxX = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
        float maxY = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));

        quads[quadcount].Position = new Vector2(minX, minY);
        quads[quadcount].Size = new Vector2(maxX - minX, maxY - minY);
        quads[quadcount].Color = color;
        quads[quadcount].flags = f;
        quadcount++;
    }

}
