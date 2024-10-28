Shader "CameraMetaPassV1/VoxelBufferMeta"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
    }
    SubShader
    {
        Tags
        {
            //"RenderType" = "Opaque"
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
        }

        Cull Off
        //ZTest Always
        //ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vertex_base
            #pragma fragment fragment_base

            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||

            #include "UnityCG.cginc"

            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||

            sampler2D _MainTex;

            float _Cutoff;

            struct meshData
            {
                float4 vertex : POSITION;
                //float3 normal : NORMAL;
                //float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
            };

            struct vertexToFragment
            {
                float4 vertexCameraClipPosition : SV_POSITION;
                float2 uv1 : TEXCOORD0;
            };

            vertexToFragment vertex_base(meshData data)
            {
                vertexToFragment vertex;

                vertex.vertexCameraClipPosition = UnityObjectToClipPos(data.vertex);

                //An attempt at geometry thickening during voxelization.
                //float4 vertexExtrusionValue = mul(unity_ObjectToWorld, float4(_VertexExtrusion, _VertexExtrusion, _VertexExtrusion, 0));
                //vertex.vertexCameraClipPosition = UnityObjectToClipPos(data.vertex + data.normal * length(vertexExtrusionValue));

                //vertex.uv = TRANSFORM_TEX(data.uv0, _MainTex);
                //vertex.uv1 = data.uv1.xy * unity_LightmapST.xy + unity_LightmapST.zw;
                vertex.uv1 = data.uv1.xy;

                return vertex;
            }

            float4 fragment_base(vertexToFragment vertex) : SV_Target
            {
                float4 mainColor = tex2D(_MainTex, vertex.uv1);

                //clip(mainColor.a - _Cutoff);

                return mainColor;
            }
            ENDCG
        }
    }
}
