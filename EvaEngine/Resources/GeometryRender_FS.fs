#version 450

layout(location = 0) in flat vec4 inColor;
layout(location = 1) in vec2 inUV;
layout(location = 2) in flat uint inF;
layout(location = 3) in flat uint inI;
layout(set = 0, binding = 0) uniform sampler2D Tex;

layout(location = 0) out vec4 outColor;

void main()
{
    if (inUV == vec2(-11))
    {
        outColor = inColor;
    }
    else
    {
        outColor = texture(Tex, inUV) * inColor;
    }
    
    // Opcional: agregar borde para debugging
    // vec2 coord = abs(inTexCoord - 0.5) * 2.0;
    // if(coord.x > 0.95 || coord.y > 0.95)
    //     outColor = vec4(1.0, 1.0, 1.0, 1.0);
}