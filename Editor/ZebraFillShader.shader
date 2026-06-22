Shader "Hidden/BuildTools/ZebraFill"
{
    Properties
    {
        _Color1 ("Color 1", Color) = (1, 1, 0, 0.3)
        _Color2 ("Color 2", Color) = (1, 1, 0, 0.05)
        _StripeWidth ("Stripe Width", Float) = 3.0
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color1;
            float4 _Color2;
            float _StripeWidth;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // 手动计算世界坐标（因为 GL 绘制的顶点已在世界空间）
                o.worldPos = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 斑马纹：沿 XZ 平面对角线方向产生条纹
                float stripe = frac((i.worldPos.x + i.worldPos.z) / _StripeWidth);
                return stripe < 0.5 ? _Color1 : _Color2;
            }
            ENDCG
        }
    }
}
