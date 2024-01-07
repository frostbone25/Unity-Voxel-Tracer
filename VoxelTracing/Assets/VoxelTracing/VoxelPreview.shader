Shader "VoxelPreview"
{
    Properties
    {
        [HideInInspector] _Seed("_Seed", Float) = 0

        [Header(Volume)]
        _VolumeTexture("Volume Texture", 3D) = "white" {}
        _VolumePos("Volume World Position", Vector) = (0, 0, 0, 0)
        _VolumeSize("Volume World Size", Vector) = (0, 0, 0, 0)
        _MipLevel("Mip Level", Float) = 0

        [Header(Raymarching)]
        _RaymarchStepSize("Raymarch Step Size", Float) = 25
        _RaymarchSteps("Raymarch Steps", Float) = 64
        [Toggle(OPAQUE_RESULT)] _RaymarchOpaque("Raymarch Opaque", Float) = 1
        _RaymarchJitterStrength("Raymarch Jitter Strength", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+2000"
        }

        Cull Off
        ZWrite Off
        ZTest Off
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            //||||||||||||||||||||||||||||| CUSTOM KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| CUSTOM KEYWORDS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| CUSTOM KEYWORDS |||||||||||||||||||||||||||||

            #pragma shader_feature_local OPAQUE_RESULT

            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| UNITY3D INCLUDES |||||||||||||||||||||||||||||

            #include "UnityCG.cginc"

            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| SHADER PARAMETERS |||||||||||||||||||||||||||||

            float _Seed;
            float _MipLevel;
            float _RaymarchStepSize;
            float _RaymarchSteps;
            float _RaymarchJitterStrength;
            float4 _VolumePos;
            float4 _VolumeSize;
            sampler3D _VolumeTexture;

            //||||||||||||||||||||||||||||| RANDOM FUNCTIONS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| RANDOM FUNCTIONS |||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||| RANDOM FUNCTIONS |||||||||||||||||||||||||||||

            // A single iteration of Bob Jenkins' One-At-A-Time hashing algorithm.
            uint JenkinsHash(uint x)
            {
                x += (x << 10u);
                x ^= (x >> 6u);
                x += (x << 3u);
                x ^= (x >> 11u);
                x += (x << 15u);
                return x;
            }

            // Compound versions of the hashing algorithm.
            uint JenkinsHash(uint2 v)
            {
                return JenkinsHash(v.x ^ JenkinsHash(v.y));
            }

            uint JenkinsHash(uint3 v)
            {
                return JenkinsHash(v.x ^ JenkinsHash(v.yz));
            }

            uint JenkinsHash(uint4 v)
            {
                return JenkinsHash(v.x ^ JenkinsHash(v.yzw));
            }

            // Construct a float with half-open range [0, 1) using low 23 bits.
            // All zeros yields 0, all ones yields the next smallest representable value below 1.
            float ConstructFloat(int m) {
                const int ieeeMantissa = 0x007FFFFF; // Binary FP32 mantissa bitmask
                const int ieeeOne = 0x3F800000; // 1.0 in FP32 IEEE

                m &= ieeeMantissa;                   // Keep only mantissa bits (fractional part)
                m |= ieeeOne;                        // Add fractional part to 1.0

                float  f = asfloat(m);               // Range [1, 2)
                return f - 1;                        // Range [0, 1)
            }

            float ConstructFloat(uint m)
            {
                return ConstructFloat(asint(m));
            }

            // Pseudo-random value in half-open range [0, 1). The distribution is reasonably uniform.
            // Ref: https://stackoverflow.com/a/17479300
            float GenerateHashedRandomFloat(uint x)
            {
                return ConstructFloat(JenkinsHash(x));
            }

            float GenerateHashedRandomFloat(uint2 v)
            {
                return ConstructFloat(JenkinsHash(v));
            }

            float GenerateHashedRandomFloat(uint3 v)
            {
                return ConstructFloat(JenkinsHash(v));
            }

            float GenerateHashedRandomFloat(uint4 v)
            {
                return ConstructFloat(JenkinsHash(v));
            }

            float GenerateRandomFloat(float2 screenUV)
            {
                _Seed += 1.0;
                return GenerateHashedRandomFloat(uint3(screenUV * _ScreenParams.xy, _Seed));
            }

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct vertexToFragment
            {
                float4 vertex : SV_POSITION;
                float4 screenPosition : TEXCOORD0;
                float3 camRelativeWorldPos : TEXCOORD1;
            };

            vertexToFragment vert(appdata v)
            {
                vertexToFragment o;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.screenPosition = UnityStereoTransformScreenSpaceTex(ComputeScreenPos(o.vertex));
                o.camRelativeWorldPos = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz - _WorldSpaceCameraPos;

                return o;
            }

            float4 frag(vertexToFragment i) : SV_Target
            {
                float2 vector_screenUV = i.screenPosition.xy / i.screenPosition.w;

                float3 cameraWorldPositionViewPlane = i.camRelativeWorldPos.xyz / dot(i.camRelativeWorldPos.xyz, unity_WorldToCamera._m20_m21_m22);

                float3 worldPos = cameraWorldPositionViewPlane + _WorldSpaceCameraPos;
                float3 localViewDir = normalize(UnityWorldSpaceViewDir(worldPos));

                float3 scaledWorldPos = ((worldPos - _VolumePos) + _VolumeSize * 0.5) / _VolumeSize;
                float3 scaledCameraPos = ((_WorldSpaceCameraPos - _VolumePos) + _VolumeSize * 0.5) / _VolumeSize;

                float3 raymarch_rayIncrement = normalize(i.camRelativeWorldPos.xyz) / _RaymarchSteps;

                float stepLength = length(raymarch_rayIncrement);

                float jitter = 1.0f + GenerateRandomFloat(vector_screenUV + length(localViewDir)) * _RaymarchStepSize * _RaymarchJitterStrength;

                float3 raymarch_currentPos = _WorldSpaceCameraPos + raymarch_rayIncrement * jitter;

                float4 result = float4(0, 0, 0, 0);

                for (int i = 0; i < int(_RaymarchSteps); i++)
                {
                    float3 scaledPos = ((raymarch_currentPos - _VolumePos) + _VolumeSize * 0.5) / _VolumeSize;

                    float3 isInBox = step(float3(0.0, 0.0, 0.0), scaledPos) * step(scaledPos, float3(1.0, 1.0, 1.0));

                    if (all(isInBox))
                    {
                        #if defined (OPAQUE_RESULT)
                            float4 sampledColor = tex3Dlod(_VolumeTexture, float4(scaledPos, _MipLevel));

                            result += sampledColor;

                            if (result.a > 0.0f)
                                break;
                        #else
                            float4 sampledColor = tex3Dlod(_VolumeTexture, float4(scaledPos, _MipLevel));

                            result += sampledColor * stepLength;
                        #endif
                    }

                    raymarch_currentPos += raymarch_rayIncrement * _RaymarchStepSize;
                }

                return float4(result.rgb, 1);
            }
            ENDCG
        }
    }
}