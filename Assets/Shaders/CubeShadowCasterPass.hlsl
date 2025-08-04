#ifndef UNIVERSAL_CUBE_SHADOW_CASTER_PASS_INCLUDED
#define UNIVERSAL_CUBE_SHADOW_CASTER_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// Shadow Casting Light geometric parameters. These variables are used when applying the shadow Normal Bias and are set by UnityEngine.Rendering.Universal.ShadowUtils.SetupShadowCasterConstantBuffer in com.unity.render-pipelines.universal/Runtime/ShadowUtils.cs
// For Directional lights, _LightDirection is used when applying shadow Normal Bias.
// For Spot lights and Point lights, _LightPosition is used to compute the actual light direction because it is different at each shadow caster geometry vertex.
float3 _LightDirection;
float3 _LightPosition;

struct Attributes
{
    float4 positionOS   : POSITION;
    float4 texRect      : TEXCOORD0;
    float2 sides         : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VertexInput
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float2 texcoord     : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    #if defined(_ALPHATEST_ON)
        float2 uv       : TEXCOORD0;
    #endif
    float4 positionCS   : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float4 GetShadowPositionHClip(VertexInput input)
{
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    positionCS = ApplyShadowClamping(positionCS);
    return positionCS;
}

Attributes ShadowPassVertex(Attributes input)
{
    return input;
}

Varyings CalculateVertex(VertexInput input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    #if defined(_ALPHATEST_ON)
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    #endif

    output.positionCS = GetShadowPositionHClip(input);
    return output;
}

Varyings CreateVertex(float x, float y, float z, float3 normal, float4 texRect, int corner)
{
    VertexInput vi = (VertexInput)0;
    vi.positionOS = float4(x, y, z, 0);
    vi.normalOS = normal;

    #if defined(_ALPHATEST_ON)
        // xy = min, zw = max
        if (corner == 0)
            vi.texcoord = texRect.xy;
        else if (corner == 1)
            vi.texcoord = texRect.xw;
        else if (corner == 2)
            vi.texcoord = texRect.zy;
        else if (corner == 3)
            vi.texcoord = texRect.zw;
    #endif

    return CalculateVertex(vi);
}

// take point as input and output up to 6 quads to form a cube
[maxvertexcount(24)]
void ShadowPassGeom(point Attributes IN[1], inout TriangleStream<Varyings> triStream)
{
    float3 down = float3(0,-1,0);
    float3 up = float3(0,1,0);
    float3 south = float3(0,0,-1);
    float3 north = float3(0,0,1);
    float3 west = float3(-1,0,0);
    float3 east = float3(1,0,0);

    float x = IN[0].positionOS.x;
    float y = IN[0].positionOS.y;
    float z = IN[0].positionOS.z;

    int sides = asint(IN[0].sides.x);
    // TODO: Get texcoords from a structured buffer indexed by a block type in the point vertex?
    float4 texRect = IN[0].texRect;

    // down y-
    if (sides & 1)
    {
        triStream.Append(CreateVertex(x + 0, y + 0, z + 0, down, texRect, 0));
        triStream.Append(CreateVertex(x + 1, y + 0, z + 0, down, texRect, 1));
        triStream.Append(CreateVertex(x + 0, y + 0, z + 1, down, texRect, 2));
        triStream.Append(CreateVertex(x + 1, y + 0, z + 1, down, texRect, 3));
        triStream.RestartStrip();
    }
    // up y+
    if (sides & 2)
    {
        triStream.Append(CreateVertex(x + 0, y + 1, z + 0, up, texRect, 0));
        triStream.Append(CreateVertex(x + 0, y + 1, z + 1, up, texRect, 1));
        triStream.Append(CreateVertex(x + 1, y + 1, z + 0, up, texRect, 2));
        triStream.Append(CreateVertex(x + 1, y + 1, z + 1, up, texRect, 3));
        triStream.RestartStrip();
    }
    // south z-
    if (sides & 4)
    {
        triStream.Append(CreateVertex(x + 0, y + 0, z + 0, south, texRect, 0));
        triStream.Append(CreateVertex(x + 0, y + 1, z + 0, south, texRect, 1));
        triStream.Append(CreateVertex(x + 1, y + 0, z + 0, south, texRect, 2));
        triStream.Append(CreateVertex(x + 1, y + 1, z + 0, south, texRect, 3));
        triStream.RestartStrip();
    }
    // north z+
    if (sides & 8)
    {
        triStream.Append(CreateVertex(x + 1, y + 0, z + 1, north, texRect, 0));
        triStream.Append(CreateVertex(x + 1, y + 1, z + 1, north, texRect, 1));
        triStream.Append(CreateVertex(x + 0, y + 0, z + 1, north, texRect, 2));
        triStream.Append(CreateVertex(x + 0, y + 1, z + 1, north, texRect, 3));
        triStream.RestartStrip();
    }
    // west x-
    if (sides & 16)
    {
        triStream.Append(CreateVertex(x + 0, y + 0, z + 1, west, texRect, 0));
        triStream.Append(CreateVertex(x + 0, y + 1, z + 1, west, texRect, 1));
        triStream.Append(CreateVertex(x + 0, y + 0, z + 0, west, texRect, 2));
        triStream.Append(CreateVertex(x + 0, y + 1, z + 0, west, texRect, 3));
        triStream.RestartStrip();
    }
    // east x+
    if (sides & 32)
    {
        triStream.Append(CreateVertex(x + 1, y + 0, z + 0, east, texRect, 0));
        triStream.Append(CreateVertex(x + 1, y + 1, z + 0, east, texRect, 1));
        triStream.Append(CreateVertex(x + 1, y + 0, z + 1, east, texRect, 2));
        triStream.Append(CreateVertex(x + 1, y + 1, z + 1, east, texRect, 3));
        triStream.RestartStrip();
    }
}

half4 ShadowPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return 0;
}

#endif
