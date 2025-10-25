#version 450

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in vec2 inSize[];
layout(location = 1) in vec4 inColor[];
layout(location = 2) in uint inF[];

layout(location = 0) out vec4 outColor;
layout(location = 1) out uint outF;
layout(location = 2) out uint outI;

void main()
{
    vec2 position = gl_in[0].gl_Position.xy;
    vec2 size = inSize[0] * 2.0;
    
    // Generar los 4 vértices del quad
    vec2[4] positions = vec2[](
        position,                       // Bottom-left
        position + vec2(size.x, 0.0),   // Bottom-right
        position + vec2(0.0, size.y),   // Top-left
        position + size                 // Top-right
    );
    
    outColor = inColor[0];
    outF = inF[0];
    
    // Emitir vértices en orden para triangle_strip
    for(int i = 0; i < 4; i++)
    {
        outI = i;
        gl_Position = vec4(positions[i], 0.0, 1.0);
        EmitVertex();
    }
    
    EndPrimitive();
}