sampler2D Scene : register(s0);
float3 Red : register(c0);
float3 Green : register(c1);
float3 Blue : register(c2);

float4 PS(float2 uv : TEXCOORD0) : COLOR0
{
    float4 pixel = tex2D(Scene, uv);
    return float4(saturate(float3(dot(pixel.rgb, Red), dot(pixel.rgb, Green), dot(pixel.rgb, Blue))), pixel.a);
}
