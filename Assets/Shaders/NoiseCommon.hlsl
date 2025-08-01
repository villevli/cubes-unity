#ifndef _INCLUDE_NOISECOMMON_HLSL_
#define _INCLUDE_NOISECOMMON_HLSL_

// Modulo 289 without a division (only multiplications)
float  mod289(float x)  { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float2 mod289(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }

// Modulo 7 without a division
float3 mod7(float3 x) { return x - floor(x * (1.0 / 7.0)) * 7.0; }
float4 mod7(float4 x) { return x - floor(x * (1.0 / 7.0)) * 7.0; }

// Permutation polynomial: (34x^2 + x) math.mod 289
float  permute(float x)  { return mod289((34.0 * x + 1.0) * x); }
float3 permute(float3 x) { return mod289((34.0 * x + 1.0) * x); }
float4 permute(float4 x) { return mod289((34.0 * x + 1.0) * x); }

float  taylorInvSqrt(float r)  { return 1.79284291400159 - 0.85373472095314 * r; }
float3 taylorInvSqrt(float3 r) { return 1.79284291400159 - 0.85373472095314 * r; }
float4 taylorInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float2 fade(float2 t) { return t*t*t*(t*(t*6.0-15.0)+10.0); }
float3 fade(float3 t) { return t*t*t*(t*(t*6.0-15.0)+10.0); }
float4 fade(float4 t) { return t*t*t*(t*(t*6.0-15.0)+10.0); }

#endif
