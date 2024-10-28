Shader "Unlit/ObjectShader"
{
    Properties
    {
        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode("Cull Mode", Int) = 2

        //https://docs.unity3d.com/Manual/SL-ZWrite.html
        //Sets whether the depth buffer contents are updated during rendering.
        //Normally, ZWrite is enabled for opaque objects and disabled for semi - transparent ones.
        [ToggleUI] _ZWrite("ZWrite", Float) = 1

        //https://docs.unity3d.com/Manual/SL-ZTest.html
        // 0 - Disabled:
        // 1 - Never:
        // 2 - Less: Draw geometry that is in front of existing geometry.Do not draw geometry that is at the same distance as or behind existing geometry.
        // 3 - LEqual: Draw geometry that is in front of or at the same distance as existing geometry.Do not draw geometry that is behind existing geometry. (This is the default value)
        // 4 - Equal: Draw geometry that is at the same distance as existing geometry.Do not draw geometry that is in front of or behind existing geometry.
        // 5 - GEqual: Draw geometry that is behind or at the same distance as existing geometry.Do not draw geometry that is in front of existing geometry.
        // 6 - Greater: Draw geometry that is behind existing geometry.Do not draw geometry that is at the same distance as or in front of existing geometry.
        // 7 - NotEqual: Draw geometry that is not at the same distance as existing geometry.Do not draw geometry that is at the same distance as existing geometry.
        // 8 - Always: No depth testing occurs. Draw all geometry, regardless of distance.
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 4

        [Header(Color)]
        [MainColor] _Color("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}

        [Header(Voxel Lightmap)]
        _VolumeTexture("Volume Texture", 3D) = "white" {}
        _VolumePos("Volume World Position", Vector) = (0, 0, 0, 0)
        _VolumeSize("Volume World Size", Vector) = (0, 0, 0, 0)
        _MipLevel("Mip Level", Float) = 0
        _NormalOffset("Normal Offset", Float) = 0
        _Exposure("Exposure", Float) = 1

        [Header(Comparison)]
        [Toggle(COMPARE_TO_LIGHTMAP)] _CompareToLightmap("Compare To Lightmap", Float) = 1
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
        }

        Cull[_CullMode]
        ZWrite[_ZWrite]
        ZTest[_ZTest]

        Pass
        {
            Name "ObjectShaderTemplate_ForwardBase"

            Tags
            {
                "LightMode" = "ForwardBase"
            }

            CGPROGRAM
            #pragma vertex vertex_forward_base
            #pragma fragment fragment_forward_base

            //||||||||||||||||||||||||||||| UNITY3D KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D KEYWORDS |||||||||||||||||||||||||||||

            #pragma multi_compile_fwdbase
            #pragma multi_compile_instancing

            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ UNITY_LIGHTMAP_FULL_HDR

            //||||||||||||||||||||||||||||| CUSTOM KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| CUSTOM KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| CUSTOM KEYWORDS |||||||||||||||||||||||||||||

            #pragma shader_feature_local COMPARE_TO_LIGHTMAP

            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||

            //BUILT IN RENDER PIPELINE
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            #include "UnityShadowLibrary.cginc"
            #include "UnityLightingCommon.cginc"
            #include "UnityStandardBRDF.cginc"

            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||

            float4 _Color;
            sampler2D _MainTex;
            float4 _MainTex_ST;         //(X = Tiling X | Y = Tiling Y | Z = Offset X | W = Offset Y)
            float4 _MainTex_TexelSize;  //(X = 1 / Width | Y = 1 / Height | Z = Width | W = Height)

            float _Exposure;
            float _NormalOffset;
            float _MipLevel;
            float4 _VolumePos;
            float4 _VolumeSize;
            sampler3D _VolumeTexture;

            struct meshData
            {
                float4 vertex : POSITION;   //Vertex Position (X = Position X | Y = Position Y | Z = Position Z | W = 1)
                float3 normal : NORMAL;     //Normal Direction [-1..1] (X = Direction X | Y = Direction Y | Z = Direction)
                float2 uv0 : TEXCOORD0;     //Mesh UVs [0..1] (X = U | Y = V)
                float2 uv1 : TEXCOORD1;     //Lightmap UVs [0..1] (X = U | Y = V)

                UNITY_VERTEX_INPUT_INSTANCE_ID //Instancing
            };

            struct vertexToFragment
            {
                float4 vertexCameraClipPosition : SV_POSITION;  //Vertex Position In Camera Clip Space
                float2 uv0 : TEXCOORD0;                         //UV0 Texture Coordinates
                float2 uvStaticLightmap : TEXCOORD1;            //(XY = Static Lightmap UVs)
                float4 vertexWorldPosition : TEXCOORD2;         //Vertex World Space Position 
                float3 vertexWorldNormal : TEXCOORD3;

                UNITY_VERTEX_OUTPUT_STEREO //Instancing
            };

            vertexToFragment vertex_forward_base(meshData data)
            {
                vertexToFragment vertex;

                //Instancing
                UNITY_SETUP_INSTANCE_ID(data);
                UNITY_INITIALIZE_OUTPUT(vertexToFragment, vertex);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(vertex);

                //transforms a point from object space to the camera's clip space
                vertex.vertexCameraClipPosition = UnityObjectToClipPos(data.vertex);

                //[TEXCOORD ASSIGNMENT 1]
                //This is the simplest way of getting texture coordinates.
                //This can be useful if you have multiple tiled textures in a shader, but don't want to create a large amount of texcoords to store all of them.
                //You can instead transform the texture coordinates in the fragment shader for each of those textures when sampling them.
                vertex.uv0 = data.uv0;

                //[TEXCOORD ASSIGNMENT 2]
                //This is a common way of getting texture coordinates, and transforming them with tiling/offsets from _MainTex.
                //Technically this is more efficent than the first because these are only computed per vertex and for one texture.
                //But it can become limiting if you have multiple textures and each of them have their own tiling/offsets
                //o.uv0 = TRANSFORM_TEX(v.uv0, _MainTex);

                //define our world position vector
                vertex.vertexWorldPosition = mul(unity_ObjectToWorld, data.vertex);

                //get regular static lightmap texcoord ONLY if lightmaps are in use
                #if defined(LIGHTMAP_ON)
                    vertex.uvStaticLightmap.xy = data.uv1.xy * unity_LightmapST.xy + unity_LightmapST.zw;
                #endif

                //the normal of the mesh
                vertex.vertexWorldNormal = UnityObjectToWorldNormal(normalize(data.normal));

                return vertex;
            }

            float4 fragment_forward_base(vertexToFragment vertex) : SV_Target
            {
                //||||||||||||||||||||||||||||||| VECTORS |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| VECTORS |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| VECTORS |||||||||||||||||||||||||||||||
                //main shader vectors used for textures or lighting calculations.

                float2 vector_uv = vertex.uv0; //uvs for sampling regular textures (uv0)
                float2 vector_lightmapUVs = vertex.uvStaticLightmap.xy; //uvs for baked lightmaps (uv1)
                float3 vector_worldPosition = vertex.vertexWorldPosition.xyz; //world position vector
                float3 vector_viewPosition = _WorldSpaceCameraPos.xyz - vector_worldPosition; //camera world position
                float3 vector_viewDirection = normalize(vector_viewPosition); //camera world position direction
                float3 vector_normalDirection = vertex.vertexWorldNormal;

                //||||||||||||||||||||||||||||||| FINAL COLOR |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| FINAL COLOR |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| FINAL COLOR |||||||||||||||||||||||||||||||

                float4 finalColor = float4(0, 0, 0, 1);

                //||||||||||||||||||||||||||||||| BAKED LIGHTING |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| BAKED LIGHTING |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| BAKED LIGHTING |||||||||||||||||||||||||||||||
                //support for regular baked unity lightmaps.

                #if defined(LIGHTMAP_ON)
                    float4 indirectLightmap = UNITY_SAMPLE_TEX2D(unity_Lightmap, vector_lightmapUVs.xy);

                    indirectLightmap.rgb = DecodeLightmap(indirectLightmap);

                    #if defined (COMPARE_TO_LIGHTMAP)
                        finalColor += indirectLightmap;
                    #endif
                #endif

                #if !defined(COMPARE_TO_LIGHTMAP)
                    float3 normalizedVolumeCoordinates = (((vector_worldPosition + (vector_normalDirection *_NormalOffset)) + (_VolumeSize / 2.0f)) - _VolumePos) / _VolumeSize;
                    float4 volumetricLightmap = tex3Dlod(_VolumeTexture, float4(normalizedVolumeCoordinates.xyz, _MipLevel));

                    finalColor.rgb += volumetricLightmap.rgb * _Exposure;
                #endif

                //||||||||||||||||||||||||||||||| ALBEDO |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| ALBEDO |||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||| ALBEDO |||||||||||||||||||||||||||||||

                //transform the UVs so that we can tile/offset the main texture.
                //we could also do this in the vertex shader before hand (which is slightly more efficent)
                vector_uv = vector_uv * _MainTex_ST.xy + _MainTex_ST.zw;

                // sample the texture
                float4 textureColor = tex2D(_MainTex, vector_uv) * _Color;

                finalColor *= textureColor;

                return finalColor;
            }
            ENDCG
        }

        Pass
        {
            Name "ObjectShaderTemplate_ShadowCaster"

            Tags 
            { 
                "LightMode" = "ShadowCaster" 
            }

            CGPROGRAM

            #pragma vertex vertex_shadow_cast
            #pragma fragment fragment_shadow_caster
            #pragma target 3.0

            //||||||||||||||||||||||||||||| UNITY3D KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D KEYWORDS |||||||||||||||||||||||||||||

            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing

            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||

            //BUILT IN RENDER PIPELINE
            #include "UnityCG.cginc"

            struct meshData
            {
                float4 vertex : POSITION;   //Vertex Position (X = Position X | Y = Position Y | Z = Position Z | W = 1)
                float3 normal : NORMAL;     //Normal Direction [-1..1] (X = Direction X | Y = Direction Y | Z = Direction)

                UNITY_VERTEX_INPUT_INSTANCE_ID //Instancing
            };

            struct vertexToFragment
            {
                V2F_SHADOW_CASTER;

                UNITY_VERTEX_OUTPUT_STEREO //Instancing
            };

            vertexToFragment vertex_shadow_cast(meshData v)
            {
                vertexToFragment vertex;

                //Instancing
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(vertexToFragment, vertex);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(vertex);

                TRANSFER_SHADOW_CASTER_NORMALOFFSET(vertex)

                return vertex;
            }

            float4 fragment_shadow_caster(vertexToFragment vertex) : SV_Target
            {
                SHADOW_CASTER_FRAGMENT(vertex)
            }

            ENDCG
        }
    }
}