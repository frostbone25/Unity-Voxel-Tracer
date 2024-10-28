Shader "SceneVoxelizerV4/VoxelBufferMetaNormal"
{
    SubShader
    {
        Cull Off

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

            struct meshData
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv1 : TEXCOORD1;
            };

            struct vertexToFragment
            {
                float4 vertexCameraClipPosition : SV_POSITION;
                float3 normalWorld : TEXCOORD0;
            };

            vertexToFragment vertex_base(meshData data)
            {
                vertexToFragment vertex;

                float4 vertexPosition = float4(1, 1, 1, 1);
                vertexPosition.xy = data.uv1;
                vertexPosition.z = 0;
                //vertexPosition.z = 1.0e-4f;
                //vertexPosition.z = vertex.vertexCameraClipPosition.z > 0 ? 1.0e-4f : 0.0f;
                vertexPosition = mul(UNITY_MATRIX_VP, vertexPosition);

                vertex.vertexCameraClipPosition = vertexPosition;

                vertex.normalWorld = data.normal;

                return vertex;
            }

            float4 fragment_base(vertexToFragment vertex) : SV_Target
            {
                //return float4(vertex.normalWorld.xyz * 0.5 + 0.5, 1);
                return float4(vertex.normalWorld.xyz * 0.5 + 0.5, 1);
            }
            ENDCG
        }
    }
}
