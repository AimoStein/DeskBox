sampler2D Scene : register(s0);
float2 Size : register(c0);
float Strength : register(c1);
float3 EdgeA : register(c2);
float3 EdgeB : register(c3);
float3 Hub : register(c4);

float4 PS(float2 uv : TEXCOORD0) : COLOR0
{
    float2 pixel = uv * Size;
    float2 radial = pixel - Hub.xy;
    float distance = length(radial);
    float2 direction = radial / max(distance, 0.001);
    float a = dot(float3(pixel, 1), EdgeA);
    float b = dot(float3(pixel, 1), EdgeB);
    float inner = distance - Hub.z;
    float2 normal = a < b ? EdgeA.xy : EdgeB.xy;
    normal = inner < min(a, b) ? direction : normal;
    float depth = min(min(a, b), inner);
    float bevel = saturate(1 - depth / 14);
    float light = saturate(dot(normal, float2(0.55, 0.83)));
    float2 sampleAt = saturate(uv + normal * bevel * bevel * Strength / Size);
    float3 color = tex2D(Scene, sampleAt).rgb * 0.68 + float3(0.032, 0.025, 0.018);
    color += bevel * bevel * (0.045 + 0.17 * light);
    return float4(saturate(color), 1);
}
