using System.Text;

using Veldrid;
using Veldrid.SPIRV;

namespace EvaEngine.RenderPipe;

public class DrawFromFBO : IDisposable
{
    readonly DeviceBuffer screenQuadBuffer;
    readonly DeviceBuffer screenQuadIndexBuffer;
    private readonly ResourceLayout layout;
    private readonly ResourceSet _resourceSet;

    private readonly uint FBOShader;
    // Cambiar el array del quad para que las UVs sean correctas
    readonly float[] screenQuadArray = new float[] { 
		// Posiciones X,Y  // UVs
		0, 0,             0, 0,  // inferior izquierda
		0, 1,             0, 1,  // superior izquierda 
		1, 1,             1, 1,  // superior derecha
		1, 0,             1, 0   // inferior derecha
	};
    readonly uint[] screenQuadArrayIndex = new uint[]
    {
        0, 1, 2,  // Primer triángulo
		0, 2, 3   // Segundo triángulo
	};

    private readonly Pipeline pipeline;
    private readonly Sampler sampler;

    public DrawFromFBO(GraphicsDevice GD, Swapchain swapchain, ResourceFactory RF, TextureView tex)
    {
        if (GD.IsUvOriginTopLeft)
        {
            // Flip UV.y
            for (int i = 0; i < screenQuadArray.Length / 4; i++)
            {
                screenQuadArray[i * 4 + 3] = 1f - screenQuadArray[i * 4 + 3]; // UV.y
            }
        }
        screenQuadBuffer =
            RF.CreateBuffer(new BufferDescription((uint)(screenQuadArray.Length * sizeof(float)), BufferUsage.VertexBuffer));
        screenQuadIndexBuffer = RF.CreateBuffer(
            new BufferDescription((uint)(screenQuadArrayIndex.Length * sizeof(uint)), BufferUsage.IndexBuffer));
        GD.UpdateBuffer(screenQuadBuffer, 0, screenQuadArray);
        GD.UpdateBuffer(screenQuadIndexBuffer, 0, screenQuadArrayIndex);
        var vertex = new VertexLayoutDescription([
            new("position", VertexElementFormat.Float2, VertexElementSemantic.Position),
            new("texCoord", VertexElementFormat.Float2, VertexElementSemantic.TextureCoordinate)
        ]);
        var shader = new ShaderSetDescription([vertex], RF.CreateFromSpirv(
            new ShaderDescription(ShaderStages.Vertex, @"#version 450
layout(location = 0) in vec2 position;
layout(location = 1) in vec2 texCoord;
layout(location = 0) out vec2 UV;

void main()
{
    gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
    UV = texCoord;
}
        "u8.ToArray(), "main"), new ShaderDescription(ShaderStages.Fragment, @"#version 450
        layout(location = 0) in vec2 UV;
        layout(location = 0) out vec4 color;
        layout(set = 0, binding = 0) uniform sampler2D TextureSampler;
        
        void main()
        {
            color = texture(TextureSampler, UV);
        }
        "u8.ToArray(), "main")));
        layout = RF.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("TextureSampler", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("TextureSampler", ResourceKind.Sampler, ShaderStages.Fragment)
        ));
        sampler = RF.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Wrap,
                SamplerAddressMode.Wrap,
                SamplerAddressMode.Wrap,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null,
                1, // nivel de anisotropía, 16 es lo máximo en muchas GPUs
                0,
                tex.MipLevels,
                0,
                0) with
        {
            ComparisonKind = null
        });
        _resourceSet = RF.CreateResourceSet(new ResourceSetDescription(
            layout,
            tex,
            sampler
        ));


        var graphicsPipelineDescription = new GraphicsPipelineDescription(BlendStateDescription.SingleOverrideBlend,
            DepthStencilStateDescription.DepthOnlyLessEqual, RasterizerStateDescription.CullNone, PrimitiveTopology.TriangleList, shader,
            [layout], swapchain.Framebuffer.OutputDescription);
        pipeline = RF.CreateGraphicsPipeline(graphicsPipelineDescription);
    }

    public unsafe void Render(CommandList CL)
    {
        CL.SetPipeline(pipeline);
        CL.SetGraphicsResourceSet(0, _resourceSet);
        CL.SetVertexBuffer(0, screenQuadBuffer);
        CL.SetIndexBuffer(screenQuadIndexBuffer, IndexFormat.UInt32);
        CL.DrawIndexed(6);
    }

    public void Dispose()
    {
        screenQuadBuffer.Dispose();
        sampler.Dispose();
        screenQuadIndexBuffer.Dispose();
        layout.Dispose();
        _resourceSet.Dispose();
        pipeline.Dispose();
    }
}