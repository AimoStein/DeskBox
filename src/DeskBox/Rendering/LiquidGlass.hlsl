Texture2D Backdrop : register(t0);
Texture2D BlurredBackdrop : register(t1);
SamplerState LinearClamp : register(s0);
cbuffer Glass : register(b0)
{
    float4 Region;      // origin and extent in backdrop UV
    float4 Shape;       // output width, height, corner radius, edge width (pixels)
    float4 Material;    // blur pixels, refraction pixels, tint opacity, dark theme
    float4 Pointer;     // pointer x/y in UV, hover amount, time
    float4 TextureSize;
};

struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
Vertex VS(uint id : SV_VertexID)
{
    Vertex v;
    v.uv = float2((id << 1) & 2, id & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}

float RoundedDistance(float2 p)
{
    float radius = clamp(Shape.z, 0, min(Shape.x, Shape.y) * 0.5);
    float2 q = abs(p) - (Shape.xy * 0.5 - radius);
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}

float4 BlurPS(Vertex input) : SV_TARGET
{
    float2 step = TextureSize.zw * Material.x / TextureSize.xy;
    float4 color = Backdrop.Sample(LinearClamp, input.uv) * 0.227027;
    color += Backdrop.Sample(LinearClamp, input.uv + step * 1.384615) * 0.316216;
    color += Backdrop.Sample(LinearClamp, input.uv - step * 1.384615) * 0.316216;
    color += Backdrop.Sample(LinearClamp, input.uv + step * 3.230769) * 0.070270;
    color += Backdrop.Sample(LinearClamp, input.uv - step * 3.230769) * 0.070270;
    return color;
}

float4 PS(Vertex input) : SV_TARGET
{
    float2 p = (input.uv - 0.5) * Shape.xy;
    float d = RoundedDistance(p);
    float alpha = saturate(0.5 - d);
    if (alpha <= 0) return 0;
    float2 normal = normalize(float2(
        RoundedDistance(p + float2(0.5, 0)) - RoundedDistance(p - float2(0.5, 0)),
        RoundedDistance(p + float2(0, 0.5)) - RoundedDistance(p - float2(0, 0.5))) + 0.00001);
    float edge = saturate(1 + d / max(Shape.w, 1));
    // Curved lens rim, with no distortion in the central reading area.
    float lens = sin(edge * 1.5707963) * edge;
    float2 uv = Region.xy + input.uv * Region.zw;
    uv -= normal * lens * Material.y / TextureSize.xy;
    float3 color = lerp(BlurredBackdrop.Sample(LinearClamp, uv).rgb,
        Backdrop.Sample(LinearClamp, uv).rgb, edge * edge * 0.35);
    if (Material.x < 0.001) color = Backdrop.Sample(LinearClamp, uv).rgb;
    float3 tint = lerp(float3(0.95, 0.97, 1), float3(0.065, 0.075, 0.095), Material.w);
    // Zero concentration leaves wallpaper colors intact; 100% fully applies the theme tint.
    color = lerp(color, tint, saturate(Material.z));
    float light = saturate(dot(normal, normalize(float2(-0.55, -0.83))));
    float rim = exp(-abs(d + 1.25) * 1.3);
    color += rim * (0.1 + 0.36 * light);
    color += pow(edge, 3) * light * 0.07;
    float2 mouseDelta = (input.uv - Pointer.xy) * float2(Shape.x / Shape.y, 1);
    color += exp(-dot(mouseDelta, mouseDelta) * 12) * Pointer.z * 0.045;
    return float4(saturate(color) * alpha, alpha); // premultiplied for WPF
}
