sampler2D Scene : register(s0);
float2 Size : register(c0);
float Strength : register(c1);

float4 PS(float2 uv : TEXCOORD0) : COLOR0
{
    float2 p = (uv - 0.5) * Size;
    float radius = min(17.0, min(Size.x, Size.y) * 0.5);
    float2 q = abs(p) - (Size * 0.5 - radius);
    float2 corner = max(q, 0);
    float d = length(corner) + min(max(q.x, q.y), 0) - radius;
    float2 normal = sign(p) * normalize(corner + step(q.yx, q.xy) * 0.0001);
    float edge = saturate(1 + d / 12);
    float2 sampleAt = saturate(uv - normal * (edge * edge * Strength) / Size);
    float3 color = tex2D(Scene, sampleAt).rgb * 0.70 + float3(0.025, 0.020, 0.016);
    float light = saturate(dot(normal, normalize(float2(-0.55, -0.83))));
    color += exp(-abs(d + 1.5) * 0.95) * (0.04 + 0.22 * light);
    float alpha = saturate(0.5 - d);
    return float4(saturate(color) * alpha, alpha);
}
