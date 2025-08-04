#ifndef UNIVERSAL_CUBE_SIMPLE_LIT_PASS_INCLUDED
#define UNIVERSAL_CUBE_SIMPLE_LIT_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct Attributes
{
    float4 positionOS    : POSITION;
    float4 texRect       : TEXCOORD0;
    float2 sides         : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VertexInput
{
    float4 positionOS    : POSITION;
    float3 normalOS      : NORMAL;
    float4 tangentOS     : TANGENT;
    float2 texcoord      : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv                       : TEXCOORD0;

    float3 positionWS                  : TEXCOORD1;    // xyz: posWS

    #ifdef _NORMALMAP
        half4 normalWS                 : TEXCOORD2;    // xyz: normal, w: viewDir.x
        half4 tangentWS                : TEXCOORD3;    // xyz: tangent, w: viewDir.y
        half4 bitangentWS              : TEXCOORD4;    // xyz: bitangent, w: viewDir.z
    #else
        half3  normalWS                : TEXCOORD2;
    #endif

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
        half4 fogFactorAndVertexLight  : TEXCOORD5; // x: fogFactor, yzw: vertex light
    #else
        half  fogFactor                 : TEXCOORD5;
    #endif

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        float4 shadowCoord             : TEXCOORD6;
    #endif

    half3 vertexSH                      : TEXCOORD7;

    float4 positionCS                  : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(Varyings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

    inputData.positionWS = input.positionWS;
#if defined(DEBUG_DISPLAY)
    inputData.positionCS = input.positionCS;
#endif

    #ifdef _NORMALMAP
        half3 viewDirWS = half3(input.normalWS.w, input.tangentWS.w, input.bitangentWS.w);
        inputData.tangentToWorld = half3x3(input.tangentWS.xyz, input.bitangentWS.xyz, input.normalWS.xyz);
        inputData.normalWS = TransformTangentToWorld(normalTS, inputData.tangentToWorld);
    #else
        half3 viewDirWS = GetWorldSpaceNormalizeViewDir(inputData.positionWS);
        inputData.normalWS = input.normalWS;
    #endif

    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
    viewDirWS = SafeNormalize(viewDirWS);

    inputData.viewDirectionWS = viewDirWS;

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        inputData.shadowCoord = input.shadowCoord;
    #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
        inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
    #else
        inputData.shadowCoord = float4(0, 0, 0, 0);
    #endif

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
        inputData.fogCoord = InitializeInputDataFog(float4(inputData.positionWS, 1.0), input.fogFactorAndVertexLight.x);
        inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
    #else
        inputData.fogCoord = InitializeInputDataFog(float4(inputData.positionWS, 1.0), input.fogFactor);
        inputData.vertexLighting = half3(0, 0, 0);
    #endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

    #if defined(DEBUG_DISPLAY)
    inputData.vertexSH = input.vertexSH;
    #endif
}

void InitializeBakedGIData(Varyings input, inout InputData inputData)
{
    inputData.bakedGI = SampleSHPixel(input.vertexSH, inputData.normalWS);
}

Varyings CalculateVertex(VertexInput input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

#if defined(_FOG_FRAGMENT)
        half fogFactor = 0;
#else
        half fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
#endif

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionWS.xyz = vertexInput.positionWS;
    output.positionCS = vertexInput.positionCS;

#ifdef _NORMALMAP
    half3 viewDirWS = GetWorldSpaceViewDir(vertexInput.positionWS);
    output.normalWS = half4(normalInput.normalWS, viewDirWS.x);
    output.tangentWS = half4(normalInput.tangentWS, viewDirWS.y);
    output.bitangentWS = half4(normalInput.bitangentWS, viewDirWS.z);
#else
    output.normalWS = NormalizeNormalPerVertex(normalInput.normalWS);
#endif

    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
        half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
        output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
    #else
        output.fogFactor = fogFactor;
    #endif

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        output.shadowCoord = GetShadowCoord(vertexInput);
    #endif

    return output;
}

///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

Attributes LitPassVertexSimple(Attributes input)
{
    // What's normally in the vertex shader is now in the geometry shader (LitPassGeomSimple)
    return input;
}

Varyings CreateVertex(float x, float y, float z, float3 normal, float4 texRect, int corner)
{
    VertexInput vi = (VertexInput)0;
    vi.positionOS = float4(x, y, z, 0);
    vi.normalOS = normal;
    // TODO: Set tangentOS. I guess only needed when using normal maps?

    // xy = min, zw = max
    if (corner == 0)
        vi.texcoord = texRect.xy;
    else if (corner == 1)
        vi.texcoord = texRect.xw;
    else if (corner == 2)
        vi.texcoord = texRect.zy;
    else if (corner == 3)
        vi.texcoord = texRect.zw;

    return CalculateVertex(vi);
}

// take point as input and output up to 6 quads to form a cube
[maxvertexcount(24)]
void LitPassGeomSimple(point Attributes IN[1], inout TriangleStream<Varyings> triStream)
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

void LitPassFragmentSimple(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out float4 outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    SurfaceData surfaceData;
    InitializeSimpleLitSurfaceData(input.uv, surfaceData);

    // TODO: Bake occlusion into the vertices and pass it here
    //       Could pass e.g. in input.vertexSH.w
    // surfaceData.occlusion = 

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    InitializeBakedGIData(input, inputData);

    half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));

    outColor = color;

#ifdef _WRITE_RENDERING_LAYERS
    uint renderingLayers = GetMeshRenderingLayer();
    outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
#endif
}

#endif
