Shader "DeathWish/SyncTestWorld"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Grid ("Grid", Float) = 0
        _Wall ("Wall panels", Float) = 0
        _FloorHalfSize ("Floor half size (m)", Float) = 50
        _Caution ("Caution paint", Float) = 0
        _Solid ("Solid color", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Grid;
            float _Wall;
            float _FloorHalfSize;
            float _Caution;
            float _Solid;

            struct Varyings
            {
                float4 position : SV_POSITION;
                float3 worldPosition : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 localPosition : TEXCOORD2;
            };

            Varyings vert(appdata_base input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.localPosition = input.vertex.xyz;
                return output;
            }

            float stroke(float distanceMeters, float halfWidth)
            {
                float aa = max(fwidth(distanceMeters), 0.001);
                return 1.0 - smoothstep(halfWidth, halfWidth + aa, abs(distanceMeters));
            }

            fixed4 frag(Varyings input) : SV_Target
            {
                float3 blue = float3(0.09, 0.32, 0.72);
                float3 yellow = float3(1.0, 0.77, 0.09);
                float3 n = normalize(input.worldNormal);
                float2 p = input.worldPosition.xz;
                float phase = dot(input.worldPosition, float3(1.4, 1.4, 1.4));
                float wave = -sin(6.2831853 * phase);
                float aa = max(0.001, 6.2831853 * fwidth(phase));
                float stripe = smoothstep(-aa, aa, wave);
                float3 paint = lerp(float3(0.018, 0.024, 0.03), float3(1.0, 0.74, 0.025), stripe);
                if (_Caution > 0.5)
                {
                    return float4(paint, 1);
                }
                if (_Grid > 0.5)
                {
                    float2 seams = abs(frac(p / 2.0 + 0.5) - 0.5) * 2.0;
                    float tileLine = stroke(min(seams.x, seams.y), 0.006);
                    float3 surface = lerp(_Color.rgb, float3(0.64, 0.71, 0.76), tileLine * 0.65);
                    float r = length(p);
                    float ring = stroke(r - 2.6, 0.032);
                    float track = stroke(abs(p.x) - 3.5, 0.035);
                    float tick = stroke(seams.y, 0.025) * step(3.5, abs(p.x)) * step(abs(p.x), 3.73);
                    surface = lerp(surface, blue, max(ring, max(track, tick)));
                    float edge = _FloorHalfSize - max(abs(p.x), abs(p.y));
                    float edgeBand = step(0.06, edge) * step(edge, 0.42);
                    surface = lerp(surface, paint, edgeBand);
                    float originMark = stroke(r - 2.46, 0.06) * step(abs(p.x), 0.46);
                    return float4(lerp(surface, yellow, originMark), 1);
                }
                if (_Wall > 0.5)
                {
                    float u = abs(n.x) > 0.5 ? p.y : p.x;
                    float bay = abs(frac(u / 8.0 + 0.5) - 0.5) * 8.0;
                    float y = input.worldPosition.y;
                    float3 wall = lerp(float3(0.78, 0.84, 0.88), float3(0.96, 0.97, 0.97), saturate(y / 10.0));
                    wall *= 1.0 - 0.18 * stroke(bay, 0.055);
                    float window = step(0.42, bay) * step(5.8, y) * step(y, 8.1);
                    wall = lerp(wall, lerp(float3(0.18, 0.29, 0.40), float3(0.38, 0.55, 0.67), saturate((y - 5.8) / 2.3)), window);
                    wall = lerp(wall, blue, stroke(y - 5.52, 0.09));
                    wall = lerp(wall, yellow, step(bay, 0.22) * step(0.5, y) * step(y, 2.4));
                    return float4(wall, 1);
                }
                float h = input.localPosition.y;
                float shade = 0.64 + 0.36 * saturate(dot(n, normalize(float3(-0.45, 0.8, -0.55))));
                if (_Solid > 0.5)
                {
                    return float4(_Color.rgb * shade, 1);
                }
                float3 shell = lerp(float3(0.92, 0.96, 0.98), _Color.rgb, step(0.12, h));
                shell = lerp(shell, float3(0.10, 0.17, 0.24), step(0.42, h) * step(h, 0.57));
                shell = lerp(shell, _Color.rgb, step(-0.58, h) * step(h, -0.46));
                return float4(shell * shade, 1);
            }
            ENDCG
        }
    }
}
