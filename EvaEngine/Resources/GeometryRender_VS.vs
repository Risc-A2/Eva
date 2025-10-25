#version 450

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inSize;
layout(location = 2) in vec4 inColor;
layout(location = 3) in uint flags;

layout(location = 0) out vec2 outSize;
layout(location = 1) out flat vec4 outColor;
layout(location = 2) out flat uint outF;

void main()
{
    vec2 ndcPosition = inPosition * 2.0 - 1.0;
    gl_Position = vec4(ndcPosition, 0.0, 1.0);
    outSize = inSize;
    outColor = inColor;
    outF = flags;
}