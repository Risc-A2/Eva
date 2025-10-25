#version 450

layout(location = 0) in flat vec4 inColor;
layout(location = 1) in flat uint inF;
layout(location = 2) in flat uint inI;
layout(set = 0, binding = 0) uniform sampler2D Tex;

layout(location = 0) out vec4 outColor;

void main()
{
    vec2 UV = vec2(0, 0);
    // Quad sólido con el color especificado
    if (inF == 1)
    {
        //0.8125
        if (inI == 0)
        {
            UV = vec2(0.3125, 0.019048);
        }
        else if (inI == 1)
        {
            UV = vec2(0.8125, 0.019048);
        }
        else if (inI == 2)
        {
            UV = vec2(0.3125, 0.009524);
        }
        else if (inI == 3)
        {
            UV = vec2(0.8125, 0.009524);
        }
        outColor = texture(Tex, UV) * inColor;
    }
    else if (inF == 2)
    {
        //0.8125
        if (inI == 0)
        {
            UV = vec2(0.609375, 0.647619047619);
        }
        else if (inI == 1)
        {
            UV = vec2(0.78125, 0.647619047619);
        }
        else if (inI == 2)
        {
            UV = vec2(0.609375, 0.0380952380953);
        }
        else if (inI == 3)
        {
            UV = vec2(0.78125, 0.0380952380953);
        }
        outColor = texture(Tex, UV);
    }
    else if (inF == 3)
    {
        //0.8125
        if (inI == 0)
        {
            UV = vec2(0.8125, 0.647619047619);
        }
        else if (inI == 1)
        {
            UV = vec2(0.78125, 0.647619047619);
        }
        else if (inI == 2)
        {
            UV = vec2(0.8125, 0.0380952380953);
        }
        else if (inI == 3)
        {
            UV = vec2(0.78125, 0.0380952380953);
        }
        outColor = texture(Tex, UV);
    }
    else if (inF == 4)
    {
        //0.8125
        if (inI == 0)
        {
            UV = vec2(0, 0);
        }
        else if (inI == 1)
        {
            UV = vec2(0.265625, 0);
        }
        else if (inI == 2)
        {
            UV = vec2(0, 0.952380952381);
        }
        else if (inI == 3)
        {
            UV = vec2(0.265625, 0.952380952381);
        }
        outColor = texture(Tex, UV);
    }
    else if (inF == 5)
    {
        //0.8125
        if (inI == 0)
        {
            UV = vec2(20.0 / 64.0, 4.0 / 105.0);
        }
        else if (inI == 1)
        {
            UV = vec2(37.0 / 64.0, 4.0 / 105.0);
        }
        else if (inI == 2)
        {
            UV = vec2(20.0 / 64.0, 101.0 / 105.0);
        }
        else if (inI == 3)
        {
            UV = vec2(37.0 / 64.0, 101.0 / 105.0);
        }
        outColor = texture(Tex, UV);
    }
    else if (inF == 6)
    {
        outColor = inColor;
    }
    
    // Opcional: agregar borde para debugging
    // vec2 coord = abs(inTexCoord - 0.5) * 2.0;
    // if(coord.x > 0.95 || coord.y > 0.95)
    //     outColor = vec4(1.0, 1.0, 1.0, 1.0);
}