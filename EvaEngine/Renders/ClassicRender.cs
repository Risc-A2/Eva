//#define VERTEX_USE_INDEX_BUFFER

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using EvaEngine;

using Veldrid;
using Veldrid.OpenGLBinding;
using Veldrid.SPIRV;

using Buffer = System.Buffer;


namespace EvaEngine.Renders;

public class ClassicRender : IRender
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public static readonly int SizeInBytes = ((2 * 4) + (4 * 4) + (2 * 4));
        public Vector2 Position;
        public RgbaFloatEva Color;
        public Vector2 Attrib;

        public Vertex()
        {
            Position = new Vector2();
            Color = new RgbaFloatEva();
            Attrib = new Vector2();
        }
    }

    private readonly FastList<IDisposable> disposeGroup = new();
    private readonly Pipeline pipe;
#if VERTEX_USE_INDEX_BUFFER
	DeviceBuffer indexBufferId;
	uint[] indexes;
#endif
    private readonly DeviceBuffer[] VerticesBuffer = new DeviceBuffer[3];
    private Vertex[] Vertices;
    private int _currentBufferIndex = 0;
    readonly uint noteShader;
    bool[] blackKeys = new bool[257];
    readonly float[] x1array = new float[257];
    readonly float[] wdtharray = new float[257];
    private byte kbfirstNote;
    private float wdth;
    private byte kblastNote;
    private readonly RgbaFloatEva[] keyColors = new RgbaFloatEva[514];
    private readonly RgbaFloatEva[] origColors = new RgbaFloatEva[257];
    private readonly RgbaFloatEva[] _origColors = new RgbaFloatEva[257];
    private readonly bool[] keyPressed = new bool[257];
    readonly int[] keynum = new int[257];
    readonly int quadBufferLength = 75000;
    private GraphicsDevice GD;
    int quadBufferPos = 0;
    private readonly float pianoHeight = .151f;


    void Assert(bool val, string msg)
    {
        if (val)
            return;
        throw new Exception(msg);
    }

    /// <summary>
    /// Flushes the quad buffer by uploading vertex data to the GPU and issuing draw commands.
    /// This method handles both indexed and non-indexed rendering modes, updates drawing statistics,
    /// and rotates the active vertex buffer for subsequent draws.
    /// </summary>
    /// <param name="CL">The command list used to issue GPU commands.</param>
    /// <param name="check">
    /// If true, the method checks if the buffer is full before flushing; otherwise, it flushes regardless of buffer state.
    /// </param>
    unsafe void FlushQuadBuffer(CommandList CL, bool check = true)
    {
        if (quadBufferPos < quadBufferLength && check) return;
        if (quadBufferPos == 0) return;
        int verticesPerQuad;

#if VERTEX_USE_INDEX_BUFFER
		verticesPerQuad = 4;
#else
        verticesPerQuad = 6;
#endif

        var buffer = VerticesBuffer[_currentBufferIndex];
        /*var map = GD.Map(buffer, MapMode.Write);
        Span<Vertex> vert = new((void*)map.Data, pos);
        Vertices.AsSpan(0, pos).CopyTo(vert);
        GD.Unmap(buffer);*/

        CL.UpdateBuffer(buffer, 0, ref Vertices[0], (uint)(pos * Vertex.SizeInBytes));
        CL.SetVertexBuffer(0, buffer);
        //CL.UpdateBuffer(buffer, 0, _pinnedPtr, (uint)(quadBufferPos * verticesPerQuad * Vertex.SizeInBytes));
#if VERTEX_USE_INDEX_BUFFER
		//CL.UpdateBuffer(buffer, 0, ref Vertices[0], (uint)(quadBufferPos * 4 * Vertex.SizeInBytes));
		CL.DrawIndexed((uint)(quadBufferPos * 6), 1, 0, 0, 0);
#else
        //CL.UpdateBuffer(buffer, 0, ref Vertices[0], (uint)(quadBufferPos * 6 * Vertex.SizeInBytes));
        CL.Draw((uint)(quadBufferPos * 6), 1, 0, 0);
#endif
        DrawingInfo.quads += quadBufferPos;
        DrawingInfo.verticesCount += quadBufferPos * verticesPerQuad;
        DrawingInfo.triangleCount += quadBufferPos * 2;
        quadBufferPos = 0;
        pos = 0;
        DrawingInfo.flushCount++;
        _currentBufferIndex = (_currentBufferIndex + 1) % VerticesBuffer.Length; // Rota entre 0, 1, 2
    }

    bool isBlackNote(int n)
    {
        n = n % 12;
        return n == 1 || n == 3 || n == 6 || n == 8 || n == 10;
    }

    private Pipeline pipeline;
    public bool Initialized { get; set; }
    private int dt = -1;
    private int pos;

    public static void BlendColorSIMD(ref RgbaFloatEva orig, RgbaFloatEva src)
    {
        /*var srcVec = new Vector4(src.R, src.G, src.B, src.A);
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
        orig.A = 1f; // forzado*/
        // Cargar los píxeles como vectores

        var srcVec = Vector128.Create(src.R, src.G, src.B, src.A);
        var dstVec = Vector128.Create(orig.R, orig.G, orig.B, orig.A);

        // Crear vector alpha: [a, a, a, a]
        var alpha = Vector128.Create(src.A);
        var oneMinusAlpha = Vector128.Create(1f - src.A);

        // src * alpha + dest * (1 - alpha)
        var result = srcVec * alpha + dstVec * oneMinusAlpha;

        // Guardar resultado
        orig.R = result.GetElement(0);
        orig.G = result.GetElement(1);
        orig.B = result.GetElement(2);
        orig.A = 1f;
    }

    private Vector2 NoteShadow = new Vector2(0, 0.2f);
    private Vector2 NoteGradient0 = new Vector2(0, 0.5f);
    private Vector2 NoteGradient1 = new Vector2(0, 1f);
    private Vector2 S1 = new Vector2(0, 1);
    private RgbaFloatEva O1 = new RgbaFloatEva(0f, 0f, 0f, 1f);
    private RgbaFloatEva O2 = new RgbaFloatEva(1f, 1f, 1f, 1f);
    private RgbaFloatEva O3 = new RgbaFloatEva(0f, 0f, 0f, 0f);
    private float paddingx;
    private float paddingy;
    private float notePosFactor;
    private bool blackAbove;
    private float pixelsPerSecond;
    private float scwidth;
    private float sepwdth;

    void RenderNote(MidiNote n, float d, float renderCutoff, CommandList CL, RenderSettings settings, ref MidiFile f)
    {
        if ((quadBufferPos + 2) >= quadBufferLength)
            FlushQuadBuffer(CL, false);
        float y1, y2, x2;
        var k = n.Key;
        /*
        x1 = x1array[k];
        wdth = wdtharray[k];
        x2 = x1 + wdth;*/
        ref readonly var x1 = ref x1array[k];
        ref readonly var wdth = ref wdtharray[k];
        x2 = x1 + wdth;
        float timeFromEnd = renderCutoff - n.EndTime; // Tiempo desde el final de la nota
        float timeFromStart = renderCutoff - n.StartTime; // Tiempo desde el inicio de la nota
        //y1 = 1 - (renderCutoff - n.EndTime) * notePosFactor;

        if (!n.hasEnd)
            y1 = 1f + paddingy; // sometimes When this happens the outline appears at the top of the screen when it shouldn't so we add the Padding Y
        else
        {
            y1 = 1f - timeFromEnd * pixelsPerSecond;
            //if (y1 > 1)
            //    y1 = 1;
        }

        //y2 = 1 - (renderCutoff - n.StartTime) * notePosFactor;
        y2 = 1f - timeFromStart * pixelsPerSecond;
        //y1 = Math.Clamp(y1, pianoHeight, 1);
        //y2 = Math.Clamp(y2, pianoHeight, 1);
        /*if (y2 < pianoHeight)
            y2 = pianoHeight;*/
        ref readonly var trkClr = ref f.Tracks[n.Track].NoteColors[n.Channel];
        //var cc = n.Color;
        ref readonly var cc = ref trkClr;
        var coll = cc.left;
        var colr = cc.right;
        if (n.StartTime < d)
        //if (false)
        {
            ref var origcoll = ref keyColors[k * 2];
            ref var origcolr = ref keyColors[k * 2 + 1];
            /*if (!blackKeys[k])
            {
                BlendColorSIMD(ref origcoll, coll);
                BlendColorSIMD(ref origcolr, colr);
            }
            else
            {*/
            origcoll = coll;
            origcolr = colr;
            //}
            keyPressed[k] = true;
        }
        //float pixels = (y1 - y2) * settings.Height;
        //float minNoteHeight = 0.001f * (1f / dt); // Scale threshold with deltaTimeOnScreen
        /*if (y1 - y2 < (paddingy))
        {
            return; // Skip small notes
        }*/ //Die Sucker

        DrawingInfo.notes++;


        AddQuad(ref pos, x2, y2, x2, y1, x1, y1, x1, y2, coll, coll, colr, colr, NoteShadow, NoteShadow, NoteShadow,
            NoteShadow);

        quadBufferPos++;
        //float pixels = (y1 - y2) * settings.Height;
        if (y1 - y2 > paddingy * 2)
        //if (pixels > 15f)
        //if (false)
        {
            //x1 += paddingx;
            x2 -= paddingx;
            y1 -= paddingy;
            y2 += paddingy;

            AddQuad(ref pos, x2, y2, x2, y1, x1 + paddingx, y1, x1 + paddingx, y2, coll, coll, colr, colr,
                NoteGradient0, NoteGradient0,
                NoteGradient1, NoteGradient1);

            quadBufferPos++;
        }
    }
    static int LastStartBefore(Span<MidiNote> notes, float renderCutoff)
    {
        int left = 0;
        int right = notes.Length - 1;
        int result = -1;

        while (left <= right)
        {
            int mid = (left + right) >> 1;
            if (notes[mid].StartTime < renderCutoff)
            {
                result = mid;
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        return result;
    }

    static int FirstEndAfter(Span<MidiNote> notes, float d)
    {
        int left = 0;
        int right = notes.Length - 1;
        int result = notes.Length;

        while (left <= right)
        {
            int mid = (left + right) >> 1;

            if (!notes[mid].hasEnd || notes[mid].EndTime >= d)
            {
                result = mid;
                right = mid - 1;
            }
            else
            {
                left = mid + 1;
            }
        }

        return result;
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
            pixelsPerSecond = (1f - pianoHeight) / dt; // Escala vertical por unidad de tiempo
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

        CL.SetPipeline(pipeline);
        //CL.SetVertexBuffer(0, VerticesBuffer);
#if VERTEX_USE_INDEX_BUFFER
		CL.SetIndexBuffer(indexBufferId, IndexFormat.UInt32);
#endif
        //CL.SetScissorRect(0, 0, 0, settings.Width, (uint)(settings.Height * pianoHeight));

        float r, g, b, a, r2, g2, b2, a2, r3, g3, b3, a3;
        float x1;
        float x2;
        float y1;
        float y2;
        quadBufferPos = 0;
        float renderCutoff = (float)midiTime + dt;
        float d = (float)midiTime;
        pos = 0;
        var t = f;
        var Notes = CollectionsMarshal.AsSpan(t.Notes);
        // Search last note with StartTime < renderCutoff
        /*int startIndex = Notes.Length - 1; // Por defecto, la última nota
        // Binary Search the last note O(log n) for R
        // O(System.Diagnostics.Debug.Assert();
        //if (Notes.Length > 0)
        {
            // Usar búsqueda binaria para encontrar el límite
            int left = 0;
            int right = Notes.Length - 1;
            // left is lower if right == -1 (Notes.Length == 0)
            while (left <= right)
            {
                // Saves 1 Division! and Subtraction!
                int mid = (int)(((uint)right + (uint)left) >> 1);
                //int mid = left + (right - left) / 2;

                if (Notes[mid].StartTime < renderCutoff)
                {
                    // Esta nota cumple, pero podrían haber más después
                    startIndex = mid;
                    left = mid + 1; // Buscar en la mitad derecha
                }
                else
                {
                    // Esta nota no cumple, buscar en la mitad izquierda
                    right = mid - 1;
                }
            }

            if (Notes[0].StartTime >= renderCutoff)
            {
                startIndex = ~left;
            }
        }*/
        int s = 0;
        //int s = FirstEndAfter(Notes, d);
        int e = LastStartBefore(Notes, renderCutoff);
        //foreach (var t in f.Tracks)
        //if (s <= e)
        if (e > 0)
        {
            if (blackAbove)
            {
                //t.Notes.BinarySearch()?
                //foreach (var n in t.Notes)
                for (int i = s; i <= e; i++)
                {
                    ref var n = ref Notes[i];
                    var k = n.Key;
                    bool isBlack = blackKeys[k];
                    if (isBlack)
                        continue;

                    //if (n.StartTime >= renderCutoff) break;
                    //if (n.EndTime < d && n.hasEnd) continue;
                    /*if (n.EndTime >= d || !n.hasEnd)
                    {
                        if (n.StartTime < renderCutoff)
                        {*/
                    RenderNote(n, d, renderCutoff, CL, settings, ref f);
                    /*}
                    else
                    {
                        break;
                    }
                }*/
                }

                for (int i = s; i <= e; i++)
                {
                    ref var n = ref Notes[i];
                    var k = n.Key;
                    bool isBlack = blackKeys[k];
                    if (!isBlack)
                        continue;
                    //if (n.StartTime >= renderCutoff) break;
                    //if (n.EndTime < d && n.hasEnd) continue;
                    /*if (n.EndTime >= d || !n.hasEnd)
                    {
                        if (n.StartTime < renderCutoff)
                        {*/
                    RenderNote(n, d, renderCutoff, CL, settings, ref f);
                    /*}
                    else
                    {
                        break;
                    }
                }*/
                }
            }
            else
            {
                for (int i = s; i <= e; i++)
                {
                    ref var n = ref Notes[i];
                    //if (n.StartTime >= renderCutoff) break;
                    //if (n.EndTime < d && n.hasEnd) continue;
                    /*if (n.EndTime >= d || !n.hasEnd)
                    {
                        if (n.StartTime < renderCutoff)
                        {*/
                    RenderNote(n, d, renderCutoff, CL, settings, ref f);
                    /*}
                    else
                    {
                        break;
                    }
                }*/
                }
            }
        }

        FlushQuadBuffer(CL);

        #region Keyboard

        float topRedStart = pianoHeight * .99f;
        float topRedEnd = pianoHeight * .94f;
        float topBarEnd = pianoHeight * .927f;

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

        #region Decorations

        r = .086f;
        g = .086f;
        b = .086f;
        a = 1f;
        r2 = .0196f;
        g2 = .0196f;
        b2 = .0196f;
        a2 = 1f;
        var c1 = new RgbaFloatEva(r, g, b, a);
        var c2 = new RgbaFloatEva(r2, g2, b2, a2);
        //Grey thing
        AddQuad(ref pos, 0, pianoHeight, 1, pianoHeight, 1, topRedStart, 0, topRedStart, c1, c1, c2, c2,
            S1, S1, S1, S1);
        quadBufferPos++;
        FlushQuadBuffer(CL);

        // Red thing
        r2 = .585f;
        g2 = .0392f;
        b2 = .0249f;
        a2 = 1f;
        r = r2 * 0.5f;
        g = g2 * 0.5f;
        b = b2 * 0.5f;
        a = 1f;
        c1 = new RgbaFloatEva(r, g, b, a);
        c2 = new RgbaFloatEva(r2, g2, b2, a2);

        AddQuad(ref pos, 0, topRedStart, 1, topRedStart, 1, topRedEnd, 0, topRedEnd, c1, c1, c2, c2,
            S1, S1, S1, S1);

        quadBufferPos++;
        FlushQuadBuffer(CL);

        // Small grey thing
        r = .239f;
        g = .239f;
        b = .239f;
        a = 1f;
        r2 = .239f;
        g2 = .239f;
        b2 = .239f;
        a2 = 1f;
        c1 = new RgbaFloatEva(r, g, b, a);
        c2 = new RgbaFloatEva(r2, g2, b2, a2);


        AddQuad(ref pos, 0, topRedEnd, 1, topRedEnd, 1, topBarEnd, 0, topBarEnd, c1, c1, c2, c2,
            S1, S1, S1, S1);
        quadBufferPos++;
        FlushQuadBuffer(CL);

        #endregion

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
                AddQuad(ref pos,
                    x1, wEndDownT, x2, wEndDownT, x2, y1, x1, y1,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    S1, S1, new Vector2(0, 0.5f), new Vector2(0, 0.5f));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Parte inferior de la tecla
                AddQuad(ref pos,
                    x1, y2, x2, y2, x2, wEndDownT, x1, wEndDownT,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    new Vector2(0, 0.6f), new Vector2(0, 0.6f), new Vector2(0, 0.6f), new Vector2(0, 0.6f));
                quadBufferPos++;
                FlushQuadBuffer(CL);
            }
            else
            {
                // Tecla blanca no presionada
                AddQuad(ref pos,
                    x1, wEndUpT, x2, wEndUpT, x2, y1, x1, y1,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    S1, S1, new Vector2(0, 0.8f), new Vector2(0, 0.8f));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Parte media de la tecla
                AddQuad(ref pos,
                    x1, wEndUpB, x2, wEndUpB, x2, wEndUpT, x1, wEndUpT,
                    new RgbaFloatEva(0.529f, 0.529f, 0.529f, 1f),
                    new RgbaFloatEva(0.529f, 0.529f, 0.529f, 1f),
                    new RgbaFloatEva(0.329f, 0.329f, 0.329f, 1f),
                    new RgbaFloatEva(0.329f, 0.329f, 0.329f, 1f),
                    S1, S1, S1, S1);
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Parte inferior de la tecla
                AddQuad(ref pos,
                    x1, y2, x2, y2, x2, wEndUpB, x1, wEndUpB,
                    new RgbaFloatEva(0.615f, 0.615f, 0.615f, 1f),
                    new RgbaFloatEva(0.615f, 0.615f, 0.615f, 1f),
                    new RgbaFloatEva(0.729f, 0.729f, 0.729f, 1f),
                    new RgbaFloatEva(0.729f, 0.729f, 0.729f, 1f),
                    S1, S1, S1, S1);
                quadBufferPos++;
                FlushQuadBuffer(CL);
            }

            // Separador entre teclas
            float separatorX1 = (float)Math.Floor(x1 * scwidth - sepwdth / 2f) / scwidth;
            float separatorX2 = (float)Math.Floor(x1 * scwidth + sepwdth / 2f) / scwidth;
            if (separatorX1 == separatorX2) separatorX2++;

            AddQuad(ref pos,
                separatorX1, y2, separatorX2, y2, separatorX2, y1, separatorX1, y1,
                new RgbaFloatEva(0.0431f, 0.0431f, 0.0431f, 1f),
                new RgbaFloatEva(0.556f, 0.556f, 0.556f, 1f),
                new RgbaFloatEva(0.556f, 0.556f, 0.556f, 1f),
                new RgbaFloatEva(0.0431f, 0.0431f, 0.0431f, 1f),
                S1, S1, S1, S1);
            quadBufferPos++;
            FlushQuadBuffer(CL);
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
                AddQuad(ref pos,
                    ix1, bKeyUSplitLT, ix2, bKeyUSplitRT, ix2, bKeyUpT, ix1, bKeyUpT,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    new Vector2(0.25f, 1), new Vector2(0.25f, 1), new Vector2(0.15f, 1), new Vector2(0.15f, 1));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Parte media central
                AddQuad(ref pos,
                    ix1, bKeyUSplitLB, ix2, bKeyUSplitRB, ix2, bKeyUSplitRT, ix1, bKeyUSplitLT,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    S1, S1, new Vector2(0.25f, 1), new Vector2(0.25f, 1));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Parte inferior media
                AddQuad(ref pos,
                    ix1, bKeyUpB, ix2, bKeyUpB, ix2, bKeyUSplitRB, ix1, bKeyUSplitLB,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    S1, S1, S1, S1);
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Lado izquierdo
                AddQuad(ref pos,
                    ox1, bKeyEnd, ix1, bKeyUpB, ix1, bKeyUpT, ox1, topBarEnd,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    S1, new Vector2(0.3f, 1), new Vector2(0.3f, 1), S1);
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Lado derecho
                AddQuad(ref pos,
                    ox2, bKeyEnd, ix2, bKeyUpB, ix2, bKeyUpT, ox2, topBarEnd,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    S1, new Vector2(0.3f, 1), new Vector2(0.3f, 1), S1);
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Parte inferior
                AddQuad(ref pos,
                    ox1, bKeyEnd, ox2, bKeyEnd, ix2, bKeyUpB, ix1, bKeyUpB,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    S1, S1, new Vector2(0.3f, 1), new Vector2(0.3f, 1));
                quadBufferPos++;
                FlushQuadBuffer(CL);
            }
            else
            {
                // Tecla negra presionada - Versión completa con AddQuad

                // Middle Top (Parte superior media)
                AddQuad(ref pos,
                    ix1, bKeyUSplitLT, ix2, bKeyUSplitRT, ix2, bKeyDownT, ix1, bKeyDownT,
                    new RgbaFloatEva(r3, g3, b3, a3), new RgbaFloatEva(r3, g3, b3, a3),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    new Vector2(0, 0.85f), new Vector2(0, 0.85f),
                    new Vector2(0, 0.85f), new Vector2(0, 0.85f));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Middle Middle (Parte media central)
                AddQuad(ref pos,
                    ix1, bKeyUSplitLB, ix2, bKeyUSplitRB, ix2, bKeyUSplitRT, ix1, bKeyUSplitLT,
                    new RgbaFloatEva(r3, g3, b3, a3), new RgbaFloatEva(r3, g3, b3, a3),
                    new RgbaFloatEva(r3, g3, b3, a3), new RgbaFloatEva(r3, g3, b3, a3),
                    new Vector2(0, 0.7f), new Vector2(0, 0.7f),
                    new Vector2(0, 0.85f), new Vector2(0, 0.85f));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Middle Bottom (Parte inferior media)
                AddQuad(ref pos,
                    ix1, bKeyDownB, ix2, bKeyDownB, ix2, bKeyUSplitRB, ix1, bKeyUSplitLB,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r3, g3, b3, a3), new RgbaFloatEva(r3, g3, b3, a3),
                    new Vector2(0, 0.7f), new Vector2(0, 0.7f),
                    new Vector2(0, 0.7f), new Vector2(0, 0.7f));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Left (Lado izquierdo)
                AddQuad(ref pos,
                    ox1, bKeyEnd, ix1, bKeyDownB, ix1, bKeyDownT, ox1, topBarEnd,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    new Vector2(0, 0.7f), S1,
                    S1, new Vector2(0, 0.7f));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Right (Lado derecho)
                AddQuad(ref pos,
                    ox2, bKeyEnd, ix2, bKeyDownB, ix2, bKeyDownT, ox2, topBarEnd,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r2, g2, b2, a2), new RgbaFloatEva(r2, g2, b2, a2),
                    new Vector2(0, 0.7f), S1,
                    S1, new Vector2(0, 0.7f));
                quadBufferPos++;
                FlushQuadBuffer(CL);

                // Bottom (Parte inferior)
                AddQuad(ref pos,
                    ox1, bKeyEnd, ox2, bKeyEnd, ix2, bKeyDownB, ix1, bKeyDownB,
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new RgbaFloatEva(r, g, b, a), new RgbaFloatEva(r, g, b, a),
                    new Vector2(0, 0.7f), new Vector2(0, 0.7f),
                    S1, S1);
                quadBufferPos++;
                FlushQuadBuffer(CL);
            }
        }

        #endregion

        //FlushInstancedQuads();
        FlushQuadBuffer(CL, false);
    }

    private void AddQuad(ref int pos, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        RgbaFloatEva color1, RgbaFloatEva color2, RgbaFloatEva color3, RgbaFloatEva color4,
        Vector2 attrib1, Vector2 attrib2, Vector2 attrib3, Vector2 attrib4)
    {
#if !VERTEX_USE_INDEX_BUFFER
        // Triángulo 1
        Vertices[pos].Position.X = x1;
        Vertices[pos].Position.Y = y1;
        Vertices[pos].Color = color1;
        Vertices[pos].Attrib = attrib1;
        pos++;

        Vertices[pos].Position.X = x2;
        Vertices[pos].Position.Y = y2;
        Vertices[pos].Color = color2;
        Vertices[pos].Attrib = attrib2;
        pos++;

        Vertices[pos].Position.X = x4;
        Vertices[pos].Position.Y = y4;
        Vertices[pos].Color = color4;
        Vertices[pos].Attrib = attrib4;
        pos++;

        // Triángulo 2
        Vertices[pos].Position.X = x2;
        Vertices[pos].Position.Y = y2;
        Vertices[pos].Color = color2;
        Vertices[pos].Attrib = attrib2;
        pos++;

        Vertices[pos].Position.X = x4;
        Vertices[pos].Position.Y = y4;
        Vertices[pos].Color = color4;
        Vertices[pos].Attrib = attrib4;
        pos++;

        Vertices[pos].Position.X = x3;
        Vertices[pos].Position.Y = y3;
        Vertices[pos].Color = color3;
        Vertices[pos].Attrib = attrib3;
        pos++;
#else
		// Triángulo 1
		Vertices[pos].Position.X = x1;
		Vertices[pos].Position.Y = y1;
		Vertices[pos].Color = color1;
		Vertices[pos].Attrib = attrib1;
		pos++;
		
		Vertices[pos].Position.X = x2;
		Vertices[pos].Position.Y = y2;
		Vertices[pos].Color = color2;
		Vertices[pos].Attrib = attrib2;
		pos++;

		Vertices[pos].Position.X = x3;
		Vertices[pos].Position.Y = y3;
		Vertices[pos].Color = color3;
		Vertices[pos].Attrib = attrib3;
		pos++;

		Vertices[pos].Position.X = x4;
		Vertices[pos].Position.Y = y4;
		Vertices[pos].Color = color4;
		Vertices[pos].Attrib = attrib4;
		pos++;
#endif
    }

    public void Initialize(MidiFile file, RenderSettings settings, GraphicsDevice GD, ResourceFactory RF,
        Framebuffer FB)
    {
        this.GD = GD;
        Initialized = true;
#if VERTEX_USE_INDEX_BUFFER
		Vertices = new Vertex[quadBufferLength * 4];
#else
        Vertices = new Vertex[quadBufferLength * 6];
#endif
        for (var i = 0; i < VerticesBuffer.Length; i++)
        {
            var buffer = RF.CreateBuffer(new((uint)(Vertices.Length * Vertex.SizeInBytes),
                BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            disposeGroup.Add(buffer);
            VerticesBuffer[i] = buffer;
        }

        for (int i = 0; i < Vertices.Length; i++)
        {
            Vertices[i] = new();
        }
#if VERTEX_USE_INDEX_BUFFER
		indexes = new uint[quadBufferLength * 6];
		for (uint i = 0; i < indexes.Length / 6; i++)
		{
			indexes[i * 6 + 0] = i * 4 + 0;
			indexes[i * 6 + 1] = i * 4 + 1;
			indexes[i * 6 + 2] = i * 4 + 3;
			indexes[i * 6 + 3] = i * 4 + 1;
			indexes[i * 6 + 4] = i * 4 + 3;
			indexes[i * 6 + 5] = i * 4 + 2;
		}
		indexBufferId = RF.CreateBuffer(new((uint)(quadBufferLength * 6 * sizeof(uint)), BufferUsage.IndexBuffer));
		GD.UpdateBuffer(indexBufferId, 0, indexes);
		disposeGroup.Add(indexBufferId);
#endif

        var vertexShaderDesc = new ShaderDescription(
            ShaderStages.Vertex,
            ResourceExtractor.ReadAsByte("Resources", "ClassicRender_VS.spv"),
            "main"
        );

        var fragmentShaderDesc = new ShaderDescription(
            ShaderStages.Fragment,
            ResourceExtractor.ReadAsByte("Resources", "ClassicRender_FS.spv"),
            "main"
        );

        Shader[] shaders = RF.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);
        var layout = new VertexLayoutDescription(
            new VertexElementDescription("position", VertexElementSemantic.Position,
                VertexElementFormat.Float2),
            new VertexElementDescription("glColor", VertexElementSemantic.Color, VertexElementFormat.Float4),
            new VertexElementDescription("attrib", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2)
        );
        var shaderSet = new ShaderSetDescription(
            new[] { layout },
            shaders);
        foreach (var shader in shaders)
        {
            disposeGroup.Add(shader);
        }

        pipeline = RF.CreateGraphicsPipeline(new(BlendStateDescription.SingleOverrideBlend,
            new DepthStencilStateDescription(false, false, ComparisonKind.Greater),
            new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, true),
            PrimitiveTopology.TriangleList, shaderSet, [], FB.OutputDescription));
        disposeGroup.Add(pipeline);
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

    public void Dispose()
    {
        foreach (var trash in disposeGroup)
        {
            trash.Dispose();
        }

        Vertices = null;
#if VERTEX_USE_INDEX_BUFFER
		indexes = null;
#endif
    }
}