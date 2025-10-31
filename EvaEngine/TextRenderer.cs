using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using FreeTypeSharp;

using LinticsPacker;

using SharpGen.Runtime;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using std;

using Veldrid;
using Veldrid.SPIRV;
using Veldrid.Utilities;

namespace EvaEngine
{
    public class GlyphInfo
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public float Advance;
        public float BearingX;
        public float BearingY;
        public float U0;
        public float V0;
        public float U1;
        public float V1;
    }
    // I hope this code is so ugly that i will never be able to look at it again
    // This is a Text Renderer that uses Veldrid and StbTrueType and StbRectPack to render
    // Text on the screen
    // It uses a texture atlas to store the glyphs and a vertex buffer to render the
    // Text on the screen
    // It uses a shader to render the text and a command list to draw the text
    // It uses a resource layout to bind the texture and the sampler to the shader
    // It uses a resource set to bind the texture and the sampler to the command list
    // Ugly as hell and uses unsafe, but its better than fucking deal with TTF fonts
    // + Its Lazy Loaded!
    public unsafe class TextRenderer : IDisposable
    {
        private readonly GraphicsDevice _gd;
        private readonly ResourceFactory _factory;
        private readonly Texture _fontTexture;
        // Ugly piece of shit
        // This is a dictionary that stores the glyph information for each character
        // The key is the character and the value is the GlyphInfo struct
        // The GlyphInfo struct contains the position, size, advance, and bearing of the glyph
        // This is used to render the text on the screen
        // But fucking char is not a good key for a dictionary
        // because it can be a unicode character and not an ASCII character
        // and also its a Struct, and remember i FUCKING HATE STRUCTS
        // But for now its okay and it makes sense
        // Because of unicode and it makes my life easier and my buddies happier
        // but i will change it to a string later
        // Joke, i will change it to a int later
        // because int is a better key for a dictionary and its faster for hashing
        // because char is a 16 bit value and int is a 32 bit value
        // and also its a better key for a dictionary because its a value type
        // and its faster for hashing and its a better key for a dictionary
        // char is a 16 bit value and int is a 32 bit value
        private readonly Dictionary<ushort, GlyphInfo> _charInfo;
        private DeviceBuffer _vertexBuffer;
        private readonly Pipeline _pipeline;
        private readonly ResourceSet _resourceSet;

        // 1024x1024 atlas size
        // This is a constant because we dont want to change it
        // and we want to keep it simple
        // The atlas size is 1024x1024 because its a good size for most fonts
        // and it fits well in most GPUs
        // The padding is 2 pixels to avoid bleeding between glyphs
        // The padding is needed to avoid bleeding between glyphs
        // The padding is needed because the glyphs are packed tightly together
        private const int AtlasSize = 1024;
        private readonly int _fontSize; // Not used?
        private readonly float _baseline;
        private readonly float newLineHeight;
        private bool dirty;
        private Memory<L8> pixels;
        // No Ñ because some fucking idiot decided to use ASCII on the font and not UTF-8
        // So we use the ASCII characters only
        private const string DefaultChars = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
        private unsafe void RasterizeGlyph(char c, int fontSize)
        {
            if (_charInfo.ContainsKey(c))
                return;
            dirty = true;
            var result = FT.FT_Get_Char_Index(_faceHandle, (UIntPtr)c);
            var err = FT.FT_Load_Glyph(_faceHandle, result, FT_LOAD.FT_LOAD_DEFAULT);
            if (err != FT_Error.FT_Err_Ok)
                throw new FreeTypeException(err);

            var glyph = Marshal.PtrToStructure<FT_GlyphSlotRec_>((IntPtr)_faceHandle->glyph);

            int advance = (int)glyph.advance.x >> 6;
            int width = (int)(glyph.metrics.width >> 6);
            int height = (int)(glyph.metrics.height >> 6);

            // Insertar en el packer con padding
            packer.Insert(width + 2, height + 2, out var rect, out var solved, true);
            if (!solved)
            {
                throw new InvalidOperationException("No se pudo insertar el glifo en el atlas");
            }

            int xO = rect.X + 1; // +1 para padding
            int yO = rect.Y + 1; // +1 para padding

            // Renderizar el glifo
            FT.FT_Render_Glyph(_faceHandle->glyph, FT_Render_Mode_.FT_RENDER_MODE_NORMAL);
            glyph = Marshal.PtrToStructure<FT_GlyphSlotRec_>((IntPtr)_faceHandle->glyph);
            var ftbmp = glyph.bitmap;

            byte* bitmap = null;
            try
            {
                // Asignar memoria para el bitmap
                bitmap = (byte*)NativeMemory.Alloc((nuint)(ftbmp.pitch * ftbmp.rows));

                // Copiar datos del bitmap FreeType
                for (int y = 0; y < ftbmp.rows; y++)
                {
                    byte* dst = bitmap + (y * ftbmp.pitch);
                    byte* src = ftbmp.buffer + (y * ftbmp.pitch);
                    C.memcpy(dst, src, ftbmp.pitch);
                }

                var span = new ReadOnlySpan<byte>(bitmap, (int)(ftbmp.pitch * ftbmp.rows));

                if (!rect.Rotated)
                {
                    // Sin rotación - copia normal
                    for (int y = 0; y < height; y++)
                    {
                        if (y + yO >= AtlasSize) break;

                        var sourceRow = span.Slice(y * ftbmp.pitch, Math.Min(ftbmp.pitch, width));
                        var targetRow = image.DangerousGetPixelRowMemory(y + yO).Slice(xO, width);

                        for (int x = 0; x < width; x++)
                        {
                            if (x + xO >= AtlasSize) break;
                            targetRow.Span[x] = new L8(sourceRow[x]);
                        }
                    }
                }
                else
                {
                    // CON ROTACIÓN CORREGIDA - 90 grados en sentido horario
                    // Cuando está rotado, el ancho y alto se intercambian
                    for (int srcY = 0; srcY < height; srcY++)
                    {
                        if (srcY >= width) break; // Por seguridad

                        var sourceRow = span.Slice(srcY * ftbmp.pitch, Math.Min(ftbmp.pitch, width));

                        for (int srcX = 0; srcX < width; srcX++)
                        {
                            if (srcX >= height) break; // Por seguridad

                            // Fórmula de rotación 90° sentido horario:
                            // source[srcX, srcY] -> target[srcY, height - 1 - srcX]
                            int targetX = xO + srcY;  // yO se convierte en coordenada X
                            int targetY = yO + (height - 1 - srcX); // xO se convierte en coordenada Y

                            if (targetX < AtlasSize && targetY < AtlasSize)
                            {
                                var targetRow = image.DangerousGetPixelRowMemory(targetY).Slice(targetX, 1);
                                targetRow.Span[0] = new L8(sourceRow[srcX]);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (bitmap != null)
                    NativeMemory.Free(bitmap);
            }

            // Store the glyph information - CORREGIDO para rotación
            _charInfo[c] = new GlyphInfo
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Rotated ? height : width,  // Intercambiar cuando está rotado
                Height = rect.Rotated ? width : height, // Intercambiar cuando está rotado
                Advance = advance,
                BearingX = (glyph.metrics.horiBearingX >> 6),
                BearingY = (glyph.metrics.horiBearingY >> 6),
                U0 = rect.U0,
                V0 = rect.V0,
                U1 = rect.U1,
                V1 = rect.V1
            };
        }

        private readonly int ascent, descent, lineGap;
        private readonly Image<L8> image;
        private readonly Packer packer = new Packer(AtlasSize, AtlasSize, PackAlgorithm.MaxRects);
        public Texture texture
        {
            get
            {
                return _fontTexture;
            }
        }
        public TextureView textureV => _tv;
        private static FT_LibraryRec_* _libraryHandle;
        private GCHandle _memoryHandle;
        private FT_FaceRec_* _faceHandle;
        ~TextRenderer()
        {
            Dispose(false);
        }
        public unsafe TextRenderer(GraphicsDevice gd, Framebuffer targetFB, byte[] data, int fontSize)
        {
            FT_Error err;
            if (_libraryHandle == default)
            {
                FT_LibraryRec_* libraryRef;
                err = FT.FT_Init_FreeType(&libraryRef);

                if (err != FT_Error.FT_Err_Ok)
                    throw new FreeTypeException(err);

                _libraryHandle = libraryRef;
            }
            _memoryHandle = GCHandle.Alloc(data, GCHandleType.Pinned);


            FT_FaceRec_* faceRef;
            err = FT.FT_New_Memory_Face(_libraryHandle, (byte*)_memoryHandle.AddrOfPinnedObject(), (IntPtr)data.Length, IntPtr.Zero, &faceRef);

            if (err != FT_Error.FT_Err_Ok)
                throw new FreeTypeException(err);

            _faceHandle = faceRef;
            err = FT.FT_Set_Pixel_Sizes(_faceHandle, (uint)0, (uint)fontSize);
            if (err != FT_Error.FT_Err_Ok)
                throw new FreeTypeException(err);
            _gd = gd;
            _factory = gd.ResourceFactory;
            _charInfo = new Dictionary<ushort, GlyphInfo>();

            //var fontData = File.ReadAllBytes(fontPath);
            // Some ugly shit going on here
            //font = StbTrueType.CreateFont(fontData, 0);
            image = new Image<L8>(AtlasSize, AtlasSize);
            // I dont fucking know why and i dont wanna know why but this is the best
            // option to avoid scaling issues?
            //scale = StbTrueType.stbtt_ScaleForPixelHeight(font, fontSize);
            int ascent, descent, lineGap;
            // This motherfucker is needed god please kill me already.
            //StbTrueType.stbtt_GetFontVMetrics(font, &ascent, &descent, &lineGap);
            var sizeRec = _faceHandle->size;

            ascent = (int)sizeRec->metrics.ascender >> 6;
            descent = (int)sizeRec->metrics.descender >> 6;
            newLineHeight = (int)sizeRec->metrics.height >> 6;
            this.ascent = ascent;
            this.descent = descent;
            //this.lineGap = lineGap;
            //_baseline = ascent/* * scale*/;
            _fontSize = fontSize;
            //newLineHeight = (ascent - descent + lineGap) * scale;
            // Create the texture from the bitmap
            // The texture is used to draw the glyphs later
            // The texture is created from the bitmap using the ResourceFactory
            // The texture is created with the size of the atlas and the pixel format of R8
            // The texture is created with the usage of Sampled so it can be used in shaders
            // The texture is created with the usage of Texture2D so it can be used in 2D rendering

            // Copy the bitmap data to the pixels span
            // The bitmap data is copied to the pixels span using CopyPixelDataTo
            // The CopyPixelDataTo method copies the pixel data from the bitmap to the span
            // The CopyPixelDataTo method is used to copy the pixel data from the bitmap to the span
            image.DangerousTryGetSinglePixelMemory(out pixels);

            _fontTexture = _factory.CreateTexture(TextureDescription.Texture2D(
                AtlasSize, AtlasSize, 1, 1, PixelFormat.R8_UNorm, TextureUsage.Sampled));

            _gd.UpdateTexture(_fontTexture, pixels.Span, 0, 0, 0, AtlasSize, AtlasSize, 1, 0, 0);
            _tv = _gd.ResourceFactory.CreateTextureView(_fontTexture);
            image.SaveAsPng("font_atlas.png"); // not needed unless debugging
            dirty = false;

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("TexCoords", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2)
            );

            var shaders = _factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(VertexShaderCode), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentShaderCode), "main")
            );

            var rl = _factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Tex", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("TexSampler", ResourceKind.Sampler, ShaderStages.Fragment)
            ));

            _pipeline = _factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                DepthStencilStateDescription.Disabled,
                RasterizerStateDescription.CullNone,
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, shaders),
                rl,
                targetFB.OutputDescription
            ));

            _vertexBuffer = _factory.CreateBuffer(new BufferDescription(
                (uint)(Vertex2D.SizeInBytes * 40), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _resourceSet = _factory.CreateResourceSet(new ResourceSetDescription(
                rl, _tv, gd.LinearSampler
            ));
        }

        private readonly Sampler sampler;
        private readonly List<Vertex2D> v = new();
        private readonly TextureView _tv;

        public void DrawText(CommandList cl, Framebuffer targetFB, string text, Vector2 normalizedPosition, RgbaFloat color, float scale = 1.0f)
        {
            PreloadCharacters(text);
            // Yes this is gross, using a List and then using a fucking Span to update the buffer
            // But this is the best way to do it without using a fixed size array
            // and without using a List<T> and then converting it to an array
            // This is a bit of a hack, but it works and its fast
            // Also, we use a List to avoid resizing the buffer too often
            v.Clear();
            float screenWidth = targetFB.Width;
            float screenHeight = targetFB.Height;

            Vector2 pixelPos = new Vector2(
                normalizedPosition.X * screenWidth,
                normalizedPosition.Y * screenHeight
            );
            float startingX = pixelPos.X;
            pixelPos.Y += ascent * scale;

            foreach (var c in text)
            {
                GlyphInfo info;
                if (!_charInfo.TryGetValue(c, out info))
                {
                    // Yeah this is a hack, but we dont want to crash the app if the character is not found
                    // We just skip it and continue
                    if (c != '\n')
                    {
                        RasterizeGlyph(c, _fontSize);
                        info = _charInfo[c];
                    }
                    else
                    {
                        // If the character is a new line, we need to move the pixel position down
                        // and reset the X position to the starting X position
                        // This is a bit of a hack, but it works and its fast
                        pixelPos.Y += newLineHeight;
                        pixelPos.X = startingX;
                        continue;
                    }
                }
                // This is horrible, but it works
                // and if it works dont touch it
                float width = info.Width * scale;
                float height = info.Height * scale;
                float normWidth = (width / screenWidth);
                float normHeight = (height / screenHeight);
                float bearingX = info.BearingX * scale;
                float bearingY = info.BearingY * scale;
                float x = pixelPos.X + bearingX;
                //float x = pixelPos.X;
                float y = pixelPos.Y - bearingY;
                //float y = pixelPos.Y - bearingY;
                //float y = pixelPos.Y;
                float advance = info.Advance * scale;

                float u0 = info.U0;
                float v0 = info.V0;
                float u1 = info.U1;
                float v1 = info.V1;

                Vector2 posNorm = new Vector2(
                    (x / screenWidth),
                    (y / screenHeight)
                );

                var vertices = new[]
                {
                    new Vertex2D(posNorm, new Vector2(u0, v0)),
                    new Vertex2D(posNorm + new Vector2(normWidth, 0), new Vector2(u1, v0)),
                    new Vertex2D(posNorm + new Vector2(0, normHeight), new Vector2(u0, v1)),
                    new Vertex2D(posNorm + new Vector2(0, normHeight), new Vector2(u0, v1)),
                    new Vertex2D(posNorm + new Vector2(normWidth, 0), new Vector2(u1, v0)),
                    new Vertex2D(posNorm + new Vector2(normWidth, normHeight), new Vector2(u1, v1)),
                };

                v.AddRange(vertices);
                // TODO: Add fucking kerning support
                // because god damn some people have a Mac and want to use it
                // and they want to use a font that has kerning
                // Fucking hell

                pixelPos.X += advance;
            }
            // This is a hack to avoid converting List to Array
            // Step 1: Convert List to Span
            // Step 2: Check if the span length is greater than the buffer size
            // Step 3: If it is, resize the buffer
            // Step 4: Update the buffer with the span
            var span = CollectionsMarshal.AsSpan(v);

            var l = _vertexBuffer.SizeInBytes / Vertex2D.SizeInBytes;
            if (span.Length >= l)
            {
                // Some growth factor to avoid resizing too often
                int size = (int)(span.Length * 1.5);
                // Dispose the old buffer and create a new one with the new size
                // and also update the length
                _vertexBuffer.Dispose();
                _vertexBuffer = _factory.CreateBuffer(new((uint)(Vertex2D.SizeInBytes * v.Capacity),
                    BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                l = _vertexBuffer.SizeInBytes / Vertex2D.SizeInBytes;
            }
            if (dirty)
            {
                image.DangerousTryGetSinglePixelMemory(out pixels);
                _gd.UpdateTexture(_fontTexture, pixels.Span, 0, 0, 0, AtlasSize, AtlasSize, 1, 0, 0);
                image.SaveAsPng("font_atlas.png"); // not needed unless debugging
                dirty = false;
                _gd.WaitForIdle();
            }
            cl.UpdateBuffer(_vertexBuffer, 0, span);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _resourceSet);
            cl.SetVertexBuffer(0, _vertexBuffer);
            cl.Draw((uint)span.Length);
        }
        public void PreloadCharacters(string characters)
        {
            bool a = false;
            foreach (char c in characters)
            {
                if (!_charInfo.ContainsKey(c) && c != '\n' && c != '\r' && c != '\t')
                {
                    RasterizeGlyph(c, _fontSize);
                    a = true;
                }
            }

            // Actualizar textura inmediatamente después de rasterizar
            if (dirty)
            {
                image.DangerousTryGetSinglePixelMemory(out pixels);
                _gd.UpdateTexture(_fontTexture, pixels.Span, 0, 0, 0, AtlasSize, AtlasSize, 1, 0, 0);
                image.SaveAsPng("font_atlas.png"); // not needed unless debugging
                dirty = false;
                _gd.WaitForIdle();
            }
        }
        protected virtual void Dispose(bool disposing)
        {
            if (_faceHandle != default)
            {
                FT.FT_Done_Face(_faceHandle);
                _faceHandle = default;
            }

            if (_memoryHandle.IsAllocated)
                _memoryHandle.Free();
            _fontTexture.Dispose();
            _vertexBuffer.Dispose();
            _pipeline.Dispose();
            _resourceSet.Dispose();
            //font.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        // Fucking C# structs, why do you have to be so ugly?
        // and im finna replace you with a class if you keep being this ugly
        // litterally you keep breaking my References and making them Null,
        // almost lost 1 entire project you fucking piece of shit structs
        [StructLayout(LayoutKind.Sequential)] // Why C#?
        private struct Vertex2D
        {
            public Vector2 Position;
            public Vector2 TexCoords;

            // Dont ask me why i use Unsafe.SizeOf here
            // This is to better than fucking around with math and marshal.sizeof
            // Because god damn it, this is a struct and we want to know its size
            public static int SizeInBytes = Unsafe.SizeOf<Vertex2D>();

            public Vertex2D(Vector2 pos, Vector2 tex)
            {
                Position = pos;
                TexCoords = tex;
            }
        }
        // Dont touch my Spirv shaders, they are perfect
        // They are written in GLSL and then compiled to SPIR-V using Veldrid
        // TODO: Make them more readable and less ugly
        // But for now, they are fine as they are and maybe i will add AA
        private const string VertexShaderCode = @"
            #version 450
            layout(location = 0) in vec2 Position;
            layout(location = 1) in vec2 TexCoords;
            layout(location = 0) out vec2 fsin_texCoords;
            void main() {
                gl_Position = vec4(Position.x * 2.0 - 1.0, 1.0 - Position.y * 2.0, 0.0, 1);
                fsin_texCoords = TexCoords;
            }";

        private const string FragmentShaderCode = @"
            #version 450
            layout(location = 0) in vec2 fsin_texCoords;
            layout(location = 0) out vec4 fsout_color;
            layout(set = 0, binding = 0) uniform texture2D Tex;
            layout(set = 0, binding = 1) uniform sampler TexSampler;

            void main() {
                fsout_color = vec4(1,1,1,texture(sampler2D(Tex, TexSampler), fsin_texCoords).r);
            }"; // WHY FUCKING FWIDTH SMOOTHSTEP IS NOT WORKING? OR FUCKING MAKING MY TEXT
                // LOOK AS A DESCENDANT OF A FUCKING MONKEY OR A ROCK? oh wait i am a descendant of a monkey and of a rock
    }
}