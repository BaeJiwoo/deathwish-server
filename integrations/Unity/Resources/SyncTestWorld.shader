Shader "DeathWish/SyncTestWorld"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Grid ("Grid", Float) = 0
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

            struct Varyings
            {
                float4 position : SV_POSITION;
                float3 worldPosition : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            Varyings vert(appdata_base input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                return output;
            }

            fixed4 frag(Varyings input) : SV_Target
            {
                float2 gridPosition = input.worldPosition.xz;
                float2 distanceToLine = abs(frac(gridPosition + 0.5) - 0.5);
                float2 pixelWidth = max(fwidth(gridPosition), 0.001);
                float2 gridLines = 1.0 - smoothstep(0.0, pixelWidth, distanceToLine);
                float grid = max(gridLines.x, gridLines.y) * (1.0 - saturate(max(pixelWidth.x, pixelWidth.y)));
                float shade = 0.55 + 0.45 * saturate(dot(normalize(input.worldNormal), normalize(float3(0.4, 0.8, -0.5))));
                return fixed4(_Color.rgb * lerp(shade, 1.0 + grid * 0.6, _Grid), _Color.a);
            }
            ENDCG
        }
    }
}
