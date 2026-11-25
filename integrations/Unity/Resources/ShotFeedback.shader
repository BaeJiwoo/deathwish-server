Shader "DeathWish/ShotFeedback"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Vertex { float4 position : POSITION; fixed4 color : COLOR; };
            struct Varyings { float4 position : SV_POSITION; fixed4 color : COLOR; };
            Varyings vert(Vertex v)
            {
                Varyings o;
                o.position = UnityObjectToClipPos(v.position);
                o.color = v.color;
                return o;
            }
            fixed4 frag(Varyings i) : SV_Target { return i.color; }
            ENDCG
        }
    }
}
