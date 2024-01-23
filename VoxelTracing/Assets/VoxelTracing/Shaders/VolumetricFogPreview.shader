Shader "Hidden/VolumetricFogPreview"
{
    Properties
    {
        [Header(Volume)]
        _VolumeTexture("Volume Texture", 3D) = "white" {}
        _VolumePos("Volume World Position", Vector) = (0, 0, 0, 0)
        _VolumeSize("Volume World Size", Vector) = (0, 0, 0, 0)

        [Header(Raymarching)]
        _RaymarchStepSize("Raymarch Step Size", Float) = 25

        [Header(Rendering)]
        [Toggle(_HALF_RESOLUTION)] _HalfResolution("Half Resolution", Float) = 0
        [Toggle(_ANIMATED_NOISE)] _EnableAnimatedJitter("Animated Noise", Float) = 0
        _JitterTexture("Jitter Texture", 2D) = "white" {}
        _RaymarchJitterStrength("Raymarch Jitter", Float) = 2
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
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_instancing  

            #include "UnityCG.cginc"

            #define RAYMARCH_STEPS 256

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            fixed _RaymarchStepSize;
            fixed _RaymarchJitterStrength;
            fixed4 _VolumePos;
            fixed4 _VolumeSize;
            fixed4 _JitterTexture_TexelSize;
            fixed4 _CameraDepthTexture_TexelSize;
            sampler2D_half _JitterTexture;
            sampler3D_half _VolumeTexture;

            #define RAYMARCH_STEP_SIZE _RaymarchStepSize
            #define RAYMARCH_JITTER_STRENGTH _RaymarchJitterStrength
            #define VOLUME_POS _VolumePos
            #define VOLUME_SIZE _VolumeSize
            #define JITTER_TEXTURE_TEXEL_SIZE _JitterTexture_TexelSize
            #define CAMERA_DEPTH_TEXTURE_TEXEL_SIZE _CameraDepthTexture_TexelSize
            #define JITTER_TEXTURE _JitterTexture
            #define VOLUME_TEXTURE _VolumeTexture

            struct appdata
            {
                fixed4 vertex : POSITION;

                //Single Pass Instanced Support
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct vertexToFragment
            {
                fixed4 vertex : SV_POSITION;
                fixed4 screenPos : TEXCOORD0;
                fixed3 camRelativeWorldPos : TEXCOORD1;

                //Single Pass Instanced Support
                UNITY_VERTEX_OUTPUT_STEREO
            };

            #if defined (_ANIMATED_NOISE)
                //animated noise courtesy of silent
                fixed r2sequence(fixed2 pixel)
                {
                    const fixed a1 = 0.75487766624669276;
                    const fixed a2 = 0.569840290998;

                    return frac(a1 * fixed(pixel.x) + a2 * fixed(pixel.y));
                }

                fixed2 r2_modified(fixed idx, fixed2 seed)
                {
                    return frac(seed + fixed(idx) * fixed2(0.245122333753, 0.430159709002));
                }

                fixed noise(fixed2 uv)
                {
                    uv += r2_modified(_Time.y, uv);
                    //uv += fixed2(_Time.y, _Time.y);
                    uv *= _ScreenParams.xy * JITTER_TEXTURE_TEXEL_SIZE.xy;

                    return tex2Dlod(JITTER_TEXTURE, fixed4(uv, 0, 0));
                }
            #else
                fixed noise(fixed2 uv)
                {
                    #if defined (_HALF_RESOLUTION)
                        return tex2Dlod(JITTER_TEXTURE, fixed4(uv * _ScreenParams.xy * JITTER_TEXTURE_TEXEL_SIZE.xy * 0.5, 0, 0));
                    #else 
                        return tex2Dlod(JITTER_TEXTURE, fixed4(uv * _ScreenParams.xy * JITTER_TEXTURE_TEXEL_SIZE.xy, 0, 0)).r;
                    #endif
                }
            #endif

            vertexToFragment vert(appdata v)
            {
                vertexToFragment o;

                //Single Pass Instanced Support
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(vertexToFragment, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.screenPos = UnityStereoTransformScreenSpaceTex(ComputeScreenPos(o.vertex));
                o.camRelativeWorldPos = mul(unity_ObjectToWorld, fixed4(v.vertex.xyz, 1.0)).xyz - _WorldSpaceCameraPos;

                return o;
            }

            fixed4 frag(vertexToFragment i) : SV_Target
            {
                //Single Pass Instanced Support
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                //our final computed fog color
                fixed4 result = fixed4(0, 0, 0, 0); //rgb = fog color, a = transmittance

                //get our screen uv coords
                fixed2 screenUV = i.screenPos.xy / i.screenPos.w;

                #if UNITY_UV_STARTS_AT_TOP
                    if (CAMERA_DEPTH_TEXTURE_TEXEL_SIZE.y < 0)
                        screenUV.y = 1 - screenUV.y;
                #endif

                #if UNITY_SINGLE_PASS_STEREO
                    // If Single-Pass Stereo mode is active, transform the
                    // coordinates to get the correct output UV for the current eye.
                    fixed4 scaleOffset = unity_StereoScaleOffset[unity_StereoEyeIndex];
                    screenUV = (screenUV - scaleOffset.zw) / scaleOffset.xy;
                #endif

                //draw our scene depth texture and linearize it
                fixed linearDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screenPos)));

                //calculate the world position view plane for the camera
                fixed3 cameraWorldPositionViewPlane = i.camRelativeWorldPos.xyz / dot(i.camRelativeWorldPos.xyz, unity_WorldToCamera._m20_m21_m22);

                //get the world position vector
                fixed3 worldPos = cameraWorldPositionViewPlane * linearDepth + _WorldSpaceCameraPos;

                //scale our vectors to the volume
                fixed3 scaledWorldPos = ((worldPos - VOLUME_POS) + VOLUME_SIZE * 0.5) / VOLUME_SIZE;
                fixed3 scaledCameraPos = ((_WorldSpaceCameraPos - VOLUME_POS) + VOLUME_SIZE * 0.5) / VOLUME_SIZE;

                // UV offset by orientation
                fixed3 localViewDir = normalize(UnityWorldSpaceViewDir(worldPos));

                //compute jitter
                fixed jitter = 1.0f + noise(screenUV + length(localViewDir)) * RAYMARCH_STEP_SIZE * RAYMARCH_JITTER_STRENGTH;

                #if defined (_HALF_RESOLUTION)
                    jitter *= 2.0f;
                #endif

                //get our ray increment vector that we use so we can march into the scene. Jitter it also so we can mitigate banding/stepping artifacts
                fixed3 raymarch_rayIncrement = normalize(i.camRelativeWorldPos.xyz) / RAYMARCH_STEPS;

                //get the length of the step
                fixed stepLength = length(raymarch_rayIncrement);

                //get our starting ray position from the camera
                fixed3 raymarch_currentPos = _WorldSpaceCameraPos + raymarch_rayIncrement * jitter;

                //start marching
                for (int i = 0; i < RAYMARCH_STEPS; i++)
                {
                    //scale the current ray position to be within the volume
                    fixed3 scaledPos = ((raymarch_currentPos - VOLUME_POS) + VOLUME_SIZE * 0.5) / VOLUME_SIZE;

                    //get the squared distances of the ray and the world position
                    fixed distanceRaySq = dot(scaledCameraPos - scaledPos, scaledCameraPos - scaledPos);
                    fixed distanceWorldSq = dot(scaledCameraPos - scaledWorldPos, scaledCameraPos - scaledWorldPos);

                    //make sure we are within our little box
                    fixed3 isInBox = step(fixed3(0.0, 0.0, 0.0), scaledPos) * step(scaledPos, fixed3(1.0, 1.0, 1.0));

                    //IMPORTANT: Check the current position distance of our ray compared to where we started.
                    //If our distance is less than that of the world then that means we aren't intersecting into any objects yet so keep accumulating.
                    if (distanceRaySq < distanceWorldSq && all(isInBox))
                    {
                        //And also keep going if we haven't reached the fullest density just yet.
                        if (result.a < 1.0f)
                        {
                            //sample the fog color (rgb = color, a = density)
                            fixed4 sampledColor = tex3Dlod(VOLUME_TEXTURE, fixed4(scaledPos, 0));

                            //accumulate the samples
                            result += fixed4(sampledColor.rgb, sampledColor.a) * stepLength; //this is slightly cheaper
                        }
                    }
                    else
                        break; //terminate the ray

                    //keep stepping forward into the scene
                    raymarch_currentPos += raymarch_rayIncrement * RAYMARCH_STEP_SIZE;
                }

                //clamp the alpha channel otherwise we get blending issues with bright spots
                result.a = clamp(result.a, 0.0f, 1.0f);

                //return the final fog color
                return result;
            }
            ENDCG
        }
    }
}
