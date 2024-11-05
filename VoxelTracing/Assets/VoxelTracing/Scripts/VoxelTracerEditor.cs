using System.Collections;
using System.Collections.Generic;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityVoxelTracer
{
    [CustomEditor(typeof(VoxelTracer))]
    public class VoxelTracerEditor : Editor
    {
        //|||||||||||||||||||||||||||||||||||||||||| VoxelTracer VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| VoxelTracer VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| VoxelTracer VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        //Scene Voxelization
        private static string tooltip_voxelName = 
        "This is the name of the volume used by the voxel tracer. " +
        "Textures saved to the data folder will be prefixed with this name.";
        SerializedProperty voxelName;

        private static string tooltip_voxelSize =
        "This defines the size of the volume for the voxel tracer in the scene. " +
        "The bigger the volume, the more data required for storage/memory, and more computation time needed for generating textures.";
        SerializedProperty voxelSize;

        private static string tooltip_voxelDensitySize =
        "This controls the resolution of the voxels used in the voxel tracer. Default value is 1. \n" +
        "\n[SMALLER VALUES]: Better voxel resolution/accuracy | Longer baking times | More storage/memory required. \n" +
        "\n[LARGER VALUES]: Lower voxel resolution/accuracy | Faster baking times | Less storage/memory required.";
        SerializedProperty voxelDensitySize;

        //Scene Voxelization - Meta Pass Properties
        private static string tooltip_texelDensityPerUnit =
        "This controls how many \"pixels\" per scene unit an object will have to represent it's materials. " +
        "This is for \"meta\" textures representing the albedo and emissive buffers of an object. Default value is 2. \n" +
        "\n[SMALLER VALUES]: Less pixels allocated | Worse quality/accuracy | Less memory usage (smaller meta textures for objects) \n" +
        "\n[LARGER VALUES]: More pixels allocated | Better quality/accuracy | More memory usage (bigger meta textures for objects)";
        SerializedProperty texelDensityPerUnit;

        private static string tooltip_minimumBufferResolution =
        "Minimum resolution for \"meta\" textures captured from objects in the scene (so objects too small will be capped to this value resolution wise) Default value is 16. \n" +
        "\n[SMALLER VALUES]: Less pixels allocated at minimum for object meta textures | Worse quality/accuracy | Less memory usage (smaller meta textures for objects) \n" +
        "\n[LARGER VALUES]: More pixels allocated at minimum for object meta textures | Better quality/accuracy | More memory usage (bigger meta textures for objects)";
        SerializedProperty minimumBufferResolution;

        private static string tooltip_performDilation =
        "This controls whether or not pixel dilation will be performed for each meta texture buffer. " +
        "This is done for meta tetures representing the objects albedo and emissive buffers. " +
        "Highly recomended because meta textures will be low resolution inherently, and without it the textures won't fit perfectly into the UV space due to pixlation. " +
        "As a result you will get black outlines on the borders of the UV atlases which will pollute the results of each buffer. Default value is true. \n" +
        "\n[ENABLED]: This will perform dilation on meta textures | Slightly slower voxelization \n" +
        "\n[DISABLED]: This will NOT do dilation on meta textures | Slightly faster voxelization";
        SerializedProperty performDilation;

        private static string tooltip_dilationPixelSize =
        "Max dilation size for the dilation radius, the higher it is the broader the dilation filter will cover, therefore reducing black outlines. Default value is 128. \n" +
        "\n[SMALLER VALUES]: Smaller dilation radius | Worse dilation quality/accuracy \n" +
        "\n[LARGER VALUES]: Larger dilation radius | Better dilation quality/accuracy";
        SerializedProperty dilationPixelSize;

        //Scene Voxelization - Voxel Rendering
        private static string tooltip_blendAlbedoVoxelSlices =
        "This will perform blending with multiple captured voxel slices of the scene albedo buffer. " +
        "The scene is captured in multiple slices in 6 different axis's, \"overdraw\" happens for alot of pixels. " +
        "So during voxelization if a pixel already has data written, we write again but blend with the original result. " +
        "In theory this should lead to better accuracy of the buffer because we retain data captured from every voxel slice, which are not the exact same on each different axis. Default value is true. \n" +
        "\n[ENABLED]: Averages multiple slices if there is overdraw of pixels, potentially better accuracy. \n" +
        "\n[DISABLED]: On each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.";
        SerializedProperty blendAlbedoVoxelSlices;

        private static string tooltip_blendEmissiveVoxelSlices =
        "This will perform blending with multiple captured voxel slices of the scene emissive buffer. " +
        "The scene is captured in multiple slices in 6 different axis's, \"overdraw\" happens for alot of pixels. " +
        "So during voxelization if a pixel already has data written, we write again but blend with the original result. " +
        "In theory this should lead to better accuracy of the buffer because we retain data captured from every voxel slice, which are not the exact same on each different axis. Default value is true. \n" +
        "\n[ENABLED]: Averages multiple slices if there is overdraw of pixels, potentially better accuracy. \n" +
        "\n[DISABLED]: On each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.";
        SerializedProperty blendEmissiveVoxelSlices;

        private static string tooltip_blendNormalVoxelSlices =
        "This will perform blending with multiple captured voxel slices of the scene emissive buffer. " +
        "The scene is captured in multiple slices in 6 different axis's, \"overdraw\" happens for alot of pixels. " +
        "So during voxelization if a pixel already has data written, we write again but blend with the original result. " +
        "In theory this should lead to better accuracy of the buffer because we retain data captured from every voxel slice, which are not the exact same on each different axis. Default value is false. \n" +
        "\nNOTE: Depending on the scene in some cases this can lead to inaccuracy on some surfaces, creating skewed shading results since some surfaces depending on how they are captured, will have their vectors averaged with others. \n" +
        "\n[ENABLED]: Averages multiple slices if there is overdraw of pixels, potentially better accuracy. \n" +
        "\n[DISABLED]: On each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.";
        SerializedProperty blendNormalVoxelSlices;

        private static string tooltip_doubleSidedGeometry =
        "This determines whether or not geometry in the scene can be seen from both sides. " +
        "This is on by default because it helps at thickening geometry in the scene and reducing holes/cracks. \n" +
        "\n[ENABLED]: Scene is voxelized with geometry visible on all sides with no culling. \n" +
        "\n[DISABLED]: Scene is voxelized with geometry visible only on the front face, back faces are culled and invisible.";
        SerializedProperty doubleSidedGeometry;

        //Scene Voxelization - Optimizations
        private static string tooltip_onlyUseGIContributors =
        "This will only use mesh renderers that are marked \"Contribute Global Illumination\". Default value is true. \n" +
        "\n[ENABLED]: This will only use meshes in the scene marked for GI | Faster voxelization | Less memory usage (less objects needing meta textures) \n" +
        "\n[DISABLED]: Every mesh renderer in the scene will be used | Slower voxelization | More memory usage (more objects needing meta textures)";
        SerializedProperty onlyUseGIContributors;

        private static string tooltip_onlyUseShadowCasters =
        "This will only use mesh renderers that have shadow casting enabled (On/TwoSided). Default value is true. \n" +
        "\n[ENABLED]: This will only use meshes with shadowcasting enabled. | Faster voxelization | Less memory usage (less objects needing meta textures) \n" +
        "\n[DISABLED]: Shadowcasting and non-shadowcasting mesh renderers in the scene will be used | Slower voxelization | More memory usage (more objects needing meta textures)";
        SerializedProperty onlyUseShadowCasters;

        private static string tooltip_onlyUseMeshesWithinBounds =
        "Only use meshes that are within voxelization bounds. Default value is true. \n" +
        "\n[ENABLED]: Only objects within voxelization bounds will be used | Faster voxelization | Less memory usage (less objects needing meta textures) \n" +
        "\n[DISABLED]: All objects in the scene will be used for voxelization | Slower voxelization | More memory usage (more objects needing meta textures)";
        SerializedProperty onlyUseMeshesWithinBounds;

        private static string tooltip_useBoundingBoxCullingForRendering =
        "Use the bounding boxes on meshes during \"voxelization\" to render only what is visible. Default value is true. \n" +
        "\n[ENABLED]: Renders objects only visible in each voxel slice | Much faster voxelization \n" +
        "\n[DISABLED]: Renders all objects | Much slower voxelization";
        SerializedProperty useBoundingBoxCullingForRendering;

        private static string tooltip_objectLayerMask = "Only use objects that match the layer mask requirements. ";
        SerializedProperty objectLayerMask;

        //Environment Options
        SerializedProperty enableEnvironmentLighting;
        SerializedProperty environmentResolution;
        SerializedProperty customEnvironmentMap;

        //Bake Options
        SerializedProperty lightLayerMask;
        SerializedProperty enableVolumetricTracing;
        SerializedProperty directSurfaceSamples;
        SerializedProperty directVolumetricSamples;
        SerializedProperty environmentSurfaceSamples;
        SerializedProperty environmentVolumetricSamples;
        SerializedProperty bounceSurfaceSamples;
        SerializedProperty bounceVolumetricSamples;
        SerializedProperty bounces;
        SerializedProperty normalOrientedHemisphereSampling;

        //Artistic Controls
        SerializedProperty albedoBoost;
        SerializedProperty indirectIntensity;
        SerializedProperty environmentIntensity;
        SerializedProperty emissiveIntensity;

        //Misc
        SerializedProperty halfPrecisionLighting;
        SerializedProperty enableGPU_Readback_Limit;
        SerializedProperty GPU_Readback_Limit;

        //Post Bake Options
        SerializedProperty volumetricDirectGaussianSamples;
        SerializedProperty volumetricBounceGaussianSamples;
        SerializedProperty volumetricEnvironmentGaussianSamples;

        //Gizmos
        SerializedProperty previewBounds;

        //|||||||||||||||||||||||||||||||||||||||||| EDITOR VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| EDITOR VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| EDITOR VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        private static int guiSpace = 10;

        private GUIStyle errorStyle;
        private GUIStyle bgLightGrey;

        private bool useCustomEnvironmentMap = false;
        private bool uncappedEditorValues = false;

        void OnEnable()
        {
            //Scene Voxelization
            voxelName = serializedObject.FindProperty("voxelName");
            voxelSize = serializedObject.FindProperty("voxelSize");
            voxelDensitySize = serializedObject.FindProperty("voxelDensitySize");

            //Scene Voxelization - Meta Pass Properties
            texelDensityPerUnit = serializedObject.FindProperty("texelDensityPerUnit");
            minimumBufferResolution = serializedObject.FindProperty("minimumBufferResolution");
            performDilation = serializedObject.FindProperty("performDilation");
            dilationPixelSize = serializedObject.FindProperty("dilationPixelSize");

            //Scene Voxelization - Voxel Rendering
            blendAlbedoVoxelSlices = serializedObject.FindProperty("blendAlbedoVoxelSlices");
            blendEmissiveVoxelSlices = serializedObject.FindProperty("blendEmissiveVoxelSlices");
            blendNormalVoxelSlices = serializedObject.FindProperty("blendNormalVoxelSlices");
            doubleSidedGeometry = serializedObject.FindProperty("doubleSidedGeometry");

            //Scene Voxelization - Optimizations
            onlyUseGIContributors = serializedObject.FindProperty("onlyUseGIContributors");
            onlyUseShadowCasters = serializedObject.FindProperty("onlyUseShadowCasters");
            onlyUseMeshesWithinBounds = serializedObject.FindProperty("onlyUseMeshesWithinBounds");
            useBoundingBoxCullingForRendering = serializedObject.FindProperty("useBoundingBoxCullingForRendering");
            objectLayerMask = serializedObject.FindProperty("objectLayerMask");

            //Environment Options
            enableEnvironmentLighting = serializedObject.FindProperty("enableEnvironmentLighting");
            environmentResolution = serializedObject.FindProperty("environmentResolution");
            customEnvironmentMap = serializedObject.FindProperty("customEnvironmentMap");

            //Bake Options
            lightLayerMask = serializedObject.FindProperty("lightLayerMask");
            enableVolumetricTracing = serializedObject.FindProperty("enableVolumetricTracing");
            directSurfaceSamples = serializedObject.FindProperty("directSurfaceSamples");
            directVolumetricSamples = serializedObject.FindProperty("directVolumetricSamples");
            environmentSurfaceSamples = serializedObject.FindProperty("environmentSurfaceSamples");
            environmentVolumetricSamples = serializedObject.FindProperty("environmentVolumetricSamples");
            bounceSurfaceSamples = serializedObject.FindProperty("bounceSurfaceSamples");
            bounceVolumetricSamples = serializedObject.FindProperty("bounceVolumetricSamples");
            bounces = serializedObject.FindProperty("bounces");
            normalOrientedHemisphereSampling = serializedObject.FindProperty("normalOrientedHemisphereSampling");

            //Artistic Controls
            albedoBoost = serializedObject.FindProperty("albedoBoost");
            indirectIntensity = serializedObject.FindProperty("indirectIntensity");
            environmentIntensity = serializedObject.FindProperty("environmentIntensity");
            emissiveIntensity = serializedObject.FindProperty("emissiveIntensity");

            //Misc
            halfPrecisionLighting = serializedObject.FindProperty("halfPrecisionLighting");
            enableGPU_Readback_Limit = serializedObject.FindProperty("enableGPU_Readback_Limit");
            GPU_Readback_Limit = serializedObject.FindProperty("GPU_Readback_Limit");

            //Post Bake Options
            volumetricDirectGaussianSamples = serializedObject.FindProperty("volumetricDirectGaussianSamples");
            volumetricBounceGaussianSamples = serializedObject.FindProperty("volumetricBounceGaussianSamples");
            volumetricEnvironmentGaussianSamples = serializedObject.FindProperty("volumetricEnvironmentGaussianSamples");

            //Gizmos
            previewBounds = serializedObject.FindProperty("previewBounds");
        }

        private void PropertyField(SerializedProperty serializedProperty, string tooltip = "")
        {
            if(string.IsNullOrEmpty(tooltip))
                EditorGUILayout.PropertyField(serializedProperty);
            else
                EditorGUILayout.PropertyField(serializedProperty, new GUIContent(serializedProperty.displayName, tooltip));
        }

        private void IntProperty(SerializedProperty serializedProperty, string label, int minimumValue, string tooltip = "")
        {
            if (uncappedEditorValues)
            {
                serializedProperty.intValue = EditorGUILayout.IntField(label, serializedProperty.intValue);
                serializedProperty.intValue = Mathf.Max(minimumValue, serializedProperty.intValue); //clamp to never reach below the defined minimumValue
            }
            else
                PropertyField(serializedProperty, tooltip);
        }

        private void FloatProperty(SerializedProperty serializedProperty, string label, float minimumValue, string tooltip = "")
        {
            if (uncappedEditorValues)
            {
                serializedProperty.floatValue = EditorGUILayout.FloatField(label, serializedProperty.floatValue);
                serializedProperty.floatValue = Mathf.Max(minimumValue, serializedProperty.floatValue); //clamp to never reach below the defined minimumValue
            }
            else
                PropertyField(serializedProperty, tooltip);
        }

        public override void OnInspectorGUI()
        {
            if (bgLightGrey == null)
            {
                bgLightGrey = new GUIStyle(EditorStyles.label);
                bgLightGrey.normal.background = Texture2D.linearGrayTexture;
            }

            if (errorStyle == null)
            {
                errorStyle = new GUIStyle(EditorStyles.label);
                errorStyle.normal.textColor = Color.red;
            }

            VoxelTracer scriptObject = serializedObject.targetObject as VoxelTracer;

            serializedObject.Update();

            //|||||||||||||||||||||||||||||||||||||||||| SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||||||

            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Scene Voxelization", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            EditorGUILayout.LabelField("Voxel Main", EditorStyles.boldLabel);
            PropertyField(voxelName, tooltip_voxelName);
            PropertyField(voxelSize, tooltip_voxelSize);
            voxelSize.vector3Value = Vector3.Max(Vector3.one * voxelDensitySize.floatValue, voxelSize.vector3Value);
            PropertyField(voxelDensitySize, tooltip_voxelDensitySize);
            voxelDensitySize.floatValue = Mathf.Max(0.01f, voxelDensitySize.floatValue);
            EditorGUILayout.LabelField(string.Format("RESOLUTION: {0} x {1} x {2} ({3} Voxels)", scriptObject.voxelResolution.x, scriptObject.voxelResolution.y, scriptObject.voxelResolution.z, scriptObject.voxelResolution.x * scriptObject.voxelResolution.y * scriptObject.voxelResolution.z), EditorStyles.helpBox);
            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Voxel Meta Pass Properties", EditorStyles.boldLabel);
            PropertyField(texelDensityPerUnit, tooltip_texelDensityPerUnit);
            texelDensityPerUnit.floatValue = Mathf.Max(0.01f, texelDensityPerUnit.floatValue);
            PropertyField(minimumBufferResolution, tooltip_minimumBufferResolution);
            minimumBufferResolution.intValue = Mathf.Max(4, minimumBufferResolution.intValue);
            PropertyField(performDilation, tooltip_performDilation);

            if(performDilation.boolValue)
                PropertyField(dilationPixelSize, tooltip_dilationPixelSize);

            dilationPixelSize.intValue = Mathf.Max(1, dilationPixelSize.intValue);

            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Voxel Rendering", EditorStyles.boldLabel);
            PropertyField(blendAlbedoVoxelSlices, tooltip_blendAlbedoVoxelSlices);
            PropertyField(blendEmissiveVoxelSlices, tooltip_blendEmissiveVoxelSlices);
            PropertyField(blendNormalVoxelSlices, tooltip_blendNormalVoxelSlices);
            PropertyField(doubleSidedGeometry, tooltip_doubleSidedGeometry);
            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Voxel Optimizations", EditorStyles.boldLabel);
            PropertyField(onlyUseGIContributors, tooltip_onlyUseGIContributors);
            PropertyField(onlyUseShadowCasters, tooltip_onlyUseShadowCasters);
            PropertyField(onlyUseMeshesWithinBounds, tooltip_onlyUseMeshesWithinBounds);
            PropertyField(useBoundingBoxCullingForRendering, tooltip_useBoundingBoxCullingForRendering);
            PropertyField(objectLayerMask, tooltip_objectLayerMask);
            EditorGUILayout.Space(guiSpace);

            //|||||||||||||||||||||||||||||||||||||||||| VOXEL TRACING OPTIONS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| VOXEL TRACING OPTIONS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| VOXEL TRACING OPTIONS ||||||||||||||||||||||||||||||||||||||||||

            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Voxel Tracing Options", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            EditorGUILayout.PropertyField(normalOrientedHemisphereSampling);
            EditorGUILayout.PropertyField(enableVolumetricTracing);
            EditorGUILayout.PropertyField(enableEnvironmentLighting);
            EditorGUILayout.PropertyField(lightLayerMask);
            EditorGUILayout.Space(guiSpace);

            //|||||||||||||||||||||||||||||||||||||||||| DIRECT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| DIRECT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| DIRECT LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Direct Lighting", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            FloatProperty(albedoBoost, "Albedo Boost", 0.0f);

            IntProperty(directSurfaceSamples, "Direct Surface Samples", 1);

            if(enableVolumetricTracing.boolValue)
                IntProperty(directVolumetricSamples, "Direct Volumetric Samples", 1);

            EditorGUILayout.Space(guiSpace);

            //|||||||||||||||||||||||||||||||||||||||||| EMISSIVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| EMISSIVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| EMISSIVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Emissive Lighting", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            FloatProperty(emissiveIntensity, "Emissive Intensity", 0.0f);

            EditorGUILayout.Space(guiSpace);

            //|||||||||||||||||||||||||||||||||||||||||| ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            if (enableEnvironmentLighting.boolValue)
            {
                GUILayout.BeginVertical(bgLightGrey);
                EditorGUILayout.LabelField("Environment Lighting", EditorStyles.whiteLargeLabel);
                GUILayout.EndVertical();

                useCustomEnvironmentMap = EditorGUILayout.Toggle("Use Custom Environment Map", useCustomEnvironmentMap);

                if (!useCustomEnvironmentMap)
                    IntProperty(environmentResolution, "Environment Resolution", 32);

                if(useCustomEnvironmentMap)
                    EditorGUILayout.PropertyField(customEnvironmentMap);

                FloatProperty(environmentIntensity, "Environment Intensity", 0.0f);

                IntProperty(environmentSurfaceSamples, "Environment Surface Samples", 1);

                if (enableVolumetricTracing.boolValue)
                    IntProperty(environmentVolumetricSamples, "Environment Volumetric Samples", 1);

                EditorGUILayout.Space(guiSpace);
            }

            //|||||||||||||||||||||||||||||||||||||||||| BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Bounce Lighting", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            FloatProperty(indirectIntensity, "Indirect Intensity", 0.0f);

            IntProperty(bounceSurfaceSamples, "Bounce Surface Samples", 1);

            if (enableVolumetricTracing.boolValue)
                IntProperty(bounceVolumetricSamples, "Bounce Volumetric Samples", 1);

            IntProperty(bounces, "Bounces", 1);

            EditorGUILayout.Space(guiSpace);

            //|||||||||||||||||||||||||||||||||||||||||| POST VOLUMETRIC BAKE OPTIONS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST VOLUMETRIC BAKE OPTIONS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST VOLUMETRIC BAKE OPTIONS ||||||||||||||||||||||||||||||||||||||||||

            if (enableVolumetricTracing.boolValue)
            {
                GUILayout.BeginVertical(bgLightGrey);
                EditorGUILayout.LabelField("Post Volumetric Bake Options", EditorStyles.whiteLargeLabel);
                GUILayout.EndVertical();

                IntProperty(volumetricDirectGaussianSamples, "Volumetric Direct Gaussian Samples", 0);
                IntProperty(volumetricBounceGaussianSamples, "Volumetric Bounce Gaussian Samples", 0);

                if(enableEnvironmentLighting.boolValue)
                    IntProperty(volumetricEnvironmentGaussianSamples, "Volumetric Environment Gaussian Samples", 0);

                EditorGUILayout.Space(guiSpace);
            }

            //|||||||||||||||||||||||||||||||||||||||||| MISC ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| MISC ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| MISC ||||||||||||||||||||||||||||||||||||||||||

            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Miscellaneous", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            EditorGUILayout.PropertyField(halfPrecisionLighting);

            uncappedEditorValues = EditorGUILayout.Toggle("Uncap Editor Values", uncappedEditorValues);
            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("This will intentionally stall the CPU to wait until the GPU is ready after X amount of compute shader dispatches. It prevents the GPU from being overburdened and potentially crashing with the amount of work.", EditorStyles.helpBox);

            EditorGUILayout.PropertyField(enableGPU_Readback_Limit);

            if (enableGPU_Readback_Limit.boolValue)
            {
                EditorGUILayout.PropertyField(GPU_Readback_Limit);
            }

            EditorGUILayout.Space(guiSpace);

            //|||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||

            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Gizmos", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            EditorGUILayout.PropertyField(previewBounds);
            EditorGUILayout.Space(guiSpace);

            //|||||||||||||||||||||||||||||||||||||||||| FUNCTIONS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| FUNCTIONS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| FUNCTIONS ||||||||||||||||||||||||||||||||||||||||||

            GUILayout.BeginVertical(bgLightGrey);
            EditorGUILayout.LabelField("Functions", EditorStyles.whiteLargeLabel);
            GUILayout.EndVertical();

            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);

            if (GUILayout.Button("Generate Scene Buffers"))
                scriptObject.GenerateAlbedoEmissiveNormalBuffers();

            if(enableEnvironmentLighting.boolValue)
            {
                if (GUILayout.Button("Capture Environment Map"))
                    scriptObject.CaptureEnvironment();
            }    

            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Direct Lighting", EditorStyles.boldLabel);

            if (GUILayout.Button("Trace Direct Surface Lighting"))
                scriptObject.TraceDirectSurfaceLighting();

            if (enableVolumetricTracing.boolValue)
            {
                if (GUILayout.Button("Trace Direct Volumetric Lighting"))
                    scriptObject.TraceDirectVolumeLighting();
            }

            if (enableEnvironmentLighting.boolValue)
            {
                EditorGUILayout.Space(guiSpace);

                EditorGUILayout.LabelField("Environment Direct Lighting", EditorStyles.boldLabel);

                if (GUILayout.Button("Trace Environment Surface Lighting"))
                    scriptObject.TraceEnvironmentSurfaceLighting();

                if (enableVolumetricTracing.boolValue)
                {
                    if (GUILayout.Button("Trace Environment Volumetric Lighting"))
                        scriptObject.TraceEnvironmentVolumeLighting();
                }
            }

            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Final Direct Lighting", EditorStyles.boldLabel);

            if (GUILayout.Button("Combine Direct Light Terms")) 
                scriptObject.CombineDirectSurfaceLightingTerms();

            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Bounce Lighting", EditorStyles.boldLabel);

            if (GUILayout.Button("Trace Bounce Surface Lighting"))
                scriptObject.TraceBounceSurfaceLighting();

            if (enableVolumetricTracing.boolValue)
            {
                if (GUILayout.Button("Trace Bounce Volumetric Lighting"))
                    scriptObject.TraceBounceVolumeLighting();
            }

            EditorGUILayout.Space(guiSpace);

            EditorGUILayout.LabelField("Final", EditorStyles.boldLabel);

            if (GUILayout.Button("Combine Surface Direct and Bounce Light"))
                scriptObject.CombineSurfaceLighting();

            if (enableVolumetricTracing.boolValue)
            {
                if (GUILayout.Button("Combine Volumetric Direct and Bounce Light"))
                    scriptObject.CombineVolumeLighting();
            }

            //|||||||||||||||||||||||||||||||||||||||||| DEBUG ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| DEBUG ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| DEBUG ||||||||||||||||||||||||||||||||||||||||||

            EditorGUILayout.LabelField("DEBUG", EditorStyles.whiteLargeLabel);

            if (GUILayout.Button("Create Voxel Preview"))
                scriptObject.CreateVoxelPreview();

            serializedObject.ApplyModifiedProperties();
        }
    }
}