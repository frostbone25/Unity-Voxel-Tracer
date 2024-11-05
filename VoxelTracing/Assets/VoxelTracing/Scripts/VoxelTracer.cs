//using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Collections;
using RenderTextureConverting;

/*
 * NOTE 1: The Anti-Aliasing when used for the generation of the voxels does help maintain geometry at low resolutions, however there is a quirk because it also samples the background color.
 * This was noticable when rendering position/normals voxel buffers where when on some geo looked darker than usual because the BG color was black.
 * Not to sure how to solve this and just might deal with it?
 * 
 * NOTE 2: For best volumetric bounce light results, set bounce light samples for the "surface" tracing very high so volumetric bounce results appear cleaner.
 * 
 * NOTE 3: Surface Bounce Samples, and Volumetric Bounce samples are seperated for good reason.
 * Doing any kind of shading or sampling in the volumetric tracing functions are WAY more heavy than just doing it on surfaces.
 * 
 * NOTE 4: Theoretically, as an optimization for bounced volumetric lighting, we actually don't need to do any additional bounces for it.
 * What matters is that the surface lighting has the bounces it needs, all we do is just throw up the samples in the air and average them.
 * No need to do bounces for the volumetric lighting itself because that will VERY QUICKLY get intensive.
 * 
 * NOTE 5: Might be worth investing time into writing a voxel normal estimator, and a dynamically changing sample type... I'll explain
 * 
 * While generating a voxel buffer of scene normals do work, and is rather trivial there are issues with it.
 * When they are used to orient hemispheres for importance sampling, if a voxel normal is facing the wrong direction, the hemisphere will be oriented incorrectly.
 * As a result sometimes objects will appear to be just purely black or incorrect.
 * So in that case it might be better just to estimate them with the surface albedo to help alleviate this and better align hemispheres with voxels.
 * 
 * In addition to that, sometimes geometry can be only one voxel thin.
 * In that case hemisphere sampling doesn't work, and we should be switching to full sphere sampling so we can get everything around correctly.
*/

namespace UnityVoxelTracer
{
    public class VoxelTracer : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        //[Header("Scene Voxelization")]
        public string voxelName = "Voxel"; //Name of the asset
        public Vector3 voxelSize = new Vector3(10.0f, 10.0f, 10.0f); //Size of the volume
        public float voxelDensitySize = 1.0f; //Size of each voxel (Smaller = More Voxels, Larger = Less Voxels)

        //[Header("Meta Pass Properties")]
        //this controls how many "pixels" per unit an object will have.
        //this is for "meta" textures representing the different buffers of an object (albedo, normal, emissive)
        //LARGER VALUES: more pixels allocated | better quality/accuracy | more memory usage (bigger meta textures for objects)
        //SMALLER VALUES: less pixels allocated | worse quality/accuracy | less memory usage (smaller meta textures for objects)
        public float texelDensityPerUnit = 2;

        //minimum resolution for meta textures captured from objects in the scene (so objects too small will be capped to this value resolution wise)
        //LARGER VALUES: more pixels allocated at minimum for object meta textures | better quality/accuracy | more memory usage (bigger meta textures for objects)
        //SMALLER VALUES: less pixels allocated at minimum for object meta textures | worse quality/accuracy | less memory usage (smaller meta textures for objects)
        public int minimumBufferResolution = 16;

        //this controls whether or not pixel dilation will be performed for each meta texture buffer.
        //this is done for "meta" tetures representing diferent buffers of an object (albedo, normal, emissive)
        //this is highly recomended because meta textures will be low resolution inherently, and without it the textures won't fit perfectly into the UV space due to pixlation.
        //as a result you will get black outlines on the borders of the UV atlases which will pollute the results of each buffer
        //ENABLED: this will perform dilation on meta textures | slightly slower voxelization
        //DISABLED: this will NOT do dilation on meta textures | slightly faster voxelization
        public bool performDilation = true;

        //max dilation size for the dilation radius, the higher it is the broader the dilation filter will cover.
        //LARGER VALUES: larger dilation radius | better dilation quality/accuracy
        //SMALLER VALUES: smaller dilation radius | worse dilation quality/accuracy
        public int dilationPixelSize = 128;

        //[Header("Rendering")]
        //this will perform blending with multiple captured voxel slices of the scene albedo buffer
        //the scene is captured in multiple slices in 6 different axis's, "overdraw" happens for alot of pixels.
        //so during voxelization if a pixel already has data written, we write again but blend with the original result, in theory this should lead to better accuracy of the buffer because each slice depending on the axis is not the exact same every time.
        //ENABLED: averages multiple slices if there is overdraw of pixels, potentially better accuracy.
        //DISABLED: on each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.
        public bool blendAlbedoVoxelSlices = true;

        //this will perform blending with multiple captured voxel slices of the scene emissive buffer
        //the scene is captured in multiple slices in 6 different axis's, "overdraw" happens for alot of pixels.
        //so during voxelization if a pixel already has data written, we write again but blend with the original result, in theory this should lead to better accuracy of the buffer because each slice depending on the axis is not the exact same every time.
        //ENABLED: averages multiple slices if there is overdraw of pixels, potentially better accuracy.
        //DISABLED: on each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.
        //NOTE: This could lead to inaccuracy on some surfaces and could create skewed results since some surfaces depending on how they are captured, will have their vectors altered.
        public bool blendEmissiveVoxelSlices = true;

        //this will perform blending with multiple captured voxel slices of the scene albedo buffer
        //the scene is captured in multiple slices in 6 different axis's, "overdraw" happens for alot of pixels.
        //so during voxelization if a pixel already has data written, we write again but blend with the original result, in theory this should lead to better accuracy of the buffer because each slice depending on the axis is not the exact same every time.
        //ENABLED: averages multiple slices if there is overdraw of pixels, potentially better accuracy.
        //DISABLED: on each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.
        public bool blendNormalVoxelSlices = false;

        //this determines whether or not geometry in the scene can be seen from both sides.
        //this is on by default because its good at thickening geometry in the scene and reducing holes/cracks.
        //ENABLED: scene is voxelized with geometry visible on all sides with no culling.
        //DISABLED: scene is voxelized with geometry visible only on the front face, back faces are culled and invisible.
        public bool doubleSidedGeometry = true;

        //[Header("Optimizations")]
        //this will only use mesh renderers that are marked "Contribute Global Illumination".
        //ENABLED: this will only use meshes in the scene marked for GI | faster voxelization | less memory usage (less objects needing meta textures)
        //DISABLED: every mesh renderer in the scene will be used | slower voxelization | more memory usage (more objects needing meta textures)
        public bool onlyUseGIContributors = true;

        //this will only use mesh renderers that have shadow casting enabled.
        //ENABLED: this will only use meshes in the scene marked for GI | faster voxelization | less memory usage (less objects needing meta textures)
        //DISABLED: every mesh renderer in the scene will be used | slower voxelization | more memory usage (more objects needing meta textures)
        public bool onlyUseShadowCasters = true;

        //only use meshes that are within voxelization bounds
        //ENABLED: only objects within voxelization bounds will be used | faster voxelization | less memory usage (less objects needing meta textures)
        //DISABLED: all objects in the scene will be used for voxelization | slower voxelization | more memory usage (more objects needing meta textures)
        public bool onlyUseMeshesWithinBounds = true;

        //use the bounding boxes on meshes during "voxelization" to render only what is visible
        //ENABLED: renders objects only visible in each voxel slice | much faster voxelization
        //DISABLED: renders all objects | much slower voxelization |
        public bool useBoundingBoxCullingForRendering = true;

        //only use objects that match the layer mask requirements
        public LayerMask objectLayerMask = 1;











        //[Header("Environment Options")]
        //Should we calculate environment lighting?
        public bool enableEnvironmentLighting = true;

        //Resolution of the captured environment cubemap.
        [Range(32, 4096)] public int environmentResolution = 128;

        //Custom environment cubemap if the user wants to input their own.
        public Cubemap customEnvironmentMap;

        //[Header("Bake Options")]

        public LayerMask lightLayerMask = 1;

        public bool enableVolumetricTracing = true;

        [Range(1, 8192)] public int directSurfaceSamples = 128;
        [Range(1, 8192)] public int directVolumetricSamples = 128;
        [Range(1, 8192)] public int environmentSurfaceSamples = 64;
        [Range(1, 8192)] public int environmentVolumetricSamples = 64;
        [Range(1, 8192)] public int bounceSurfaceSamples = 128;
        [Range(1, 8192)] public int bounceVolumetricSamples = 64;

        //Amount of surface shading bounces to do.
        [Range(1, 8)] public int bounces = 4;

        //Improve surface shading quality by using a cosine hemisphere oriented with the surface normal.
        //Results in better ray allocation at lower sample counts (though at the moment there are issues with scene normals)
        public bool normalOrientedHemisphereSampling = true;

        //[Header("Artistic Controls")]

        [Range(1, 10)] public float albedoBoost = 1; //1 is default, physically accurate.
        [Range(0, 5)] public float indirectIntensity = 1; //1 is default, physically accurate.
        [Range(0, 8)] public float environmentIntensity = 1; //1 is default, physically accurate.
        [Range(0, 8)] public float emissiveIntensity = 1; //1 is default, physically accurate.

        //[Header("Misc")]

        public bool halfPrecisionLighting = false;

        //Enables an intentional CPU staller.
        //This is a bit of a hack, but a necessary one that will intentionally stall the CPU after X amount of compute shader dispatches.
        //The compute shaders we use can get rather expensive, and issuing too many expensive workloads to the GPU can cause TDR/Driver timeouts and crash the editor.
        //To get around it, we deliberaly stall the CPU by issuing a GPU Readback call to get data back from the GPU.
        //If we get data back from the GPU that means the GPU is ready for more work and it completed whatever prior task it had.
        public bool enableGPU_Readback_Limit = true;

        //(If enabled) This adjusts the limit to how many times we do a GPU readback to stall the CPU after X amount of samples.
        [Range(1, 32)] public int GPU_Readback_Limit = 4;

        //[Header("Post Bake Options")]
        //Applies a 3D gaussian blur to volumetric light terms to smooth results out.
        //High samples though means that leaks can occur as this is not voxel/geometry aware.

        [Range(0, 64)] public int volumetricDirectGaussianSamples = 0;
        [Range(0, 64)] public int volumetricBounceGaussianSamples = 0;
        [Range(0, 64)] public int volumetricEnvironmentGaussianSamples = 0;

        //[Header("Gizmos")]
        public bool previewBounds = true;

        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        private uint THREAD_GROUP_SIZE_X = 0;
        private uint THREAD_GROUP_SIZE_Y = 0;
        private uint THREAD_GROUP_SIZE_Z = 0;

        public Vector3Int voxelResolution => new Vector3Int((int)(voxelSize.x / voxelDensitySize), (int)(voxelSize.y / voxelDensitySize), (int)(voxelSize.z / voxelDensitySize));
        private Bounds voxelBounds => new Bounds(transform.position, voxelSize);

        private static string localAssetFolder =                            "Assets/VoxelTracing";
        private static string localAssetDataFolder =                        "Assets/VoxelTracing/Data";
        private static string localAssetShadersFolder =                     "Assets/VoxelTracing/Shaders";
        private static string voxelDirectSurfaceLightAssetPath =            "Assets/VoxelTracing/Shaders/VoxelDirectSurfaceLight.compute";
        private static string voxelDirectVolumetricLightAssetPath =         "Assets/VoxelTracing/Shaders/VoxelDirectVolumetricLight.compute";
        private static string voxelBounceSurfaceLightAssetPath =            "Assets/VoxelTracing/Shaders/VoxelBounceSurfaceLight.compute";
        private static string voxelBounceVolumetricLightBufferAssetPath =   "Assets/VoxelTracing/Shaders/VoxelBounceVolumetricLight.compute";
        private static string voxelEnvironmentSurfaceLightAssetPath =       "Assets/VoxelTracing/Shaders/VoxelEnvironmentSurfaceLight.compute";
        private static string voxelEnvironmentVolumetricLightAssetPath =    "Assets/VoxelTracing/Shaders/VoxelEnvironmentVolumetricLight.compute";
        private static string combineBuffersAssetPath =                     "Assets/VoxelTracing/Shaders/CombineBuffers.compute";
        private static string gaussianBlurAssetPath =                       "Assets/VoxelTracing/Shaders/GaussianBlur3D.compute";
        private static string voxelizeSceneAssetPath =                      "Assets/VoxelTracing/Shaders/VoxelizeScene.compute";
        private static string dilateAssetPath =                             "Assets/VoxelTracing/Shaders/Dilation.compute";

        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();
        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;

        private Texture3D voxelAlbedoBuffer;
        private Texture3D voxelNormalBuffer;
        private Texture3D voxelEmissiveBuffer;
        private Texture3D voxelDirectLightSurfaceBuffer;
        private Texture3D voxelDirectLightSurfaceAlbedoBuffer;
        private Texture3D voxelDirectLightVolumeBuffer;
        private Texture3D voxelEnvironmentLightSurfaceBuffer;
        private Texture3D voxelEnvironmentLightSurfaceAlbedoBuffer;
        private Texture3D voxelEnvironmentLightVolumeBuffer;
        private Texture3D voxelCombinedDirectLightSurfaceBuffer;
        private Texture3D voxelCombinedDirectLightSurfaceAlbedoBuffer;
        private Texture3D voxelCombinedSurfaceAndVolumetricBuffer;
        private Texture3D voxelBounceLightSurfaceBuffer;
        private Texture3D voxelBounceLightSurfaceAlbedoBuffer;
        private Texture3D voxelBounceLightVolumeBuffer;

        private Cubemap environmentMap;

        private string voxelAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_albedo.asset", voxelName);
        private string voxelNormalBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_normal.asset", voxelName);
        private string voxelEmissiveBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_emissive.asset", voxelName);

        private string voxelDirectLightSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_directSurface.asset", voxelName);
        private string voxelDirectLightSurfaceAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_directSurfaceAlbedo.asset", voxelName);
        private string voxelDirectLightVolumeBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_directVolumetric.asset", voxelName);

        private string voxelEnvironmentLightSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_environmentSurface.asset", voxelName);
        private string voxelEnvironmentLightSurfaceAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_environmentSurfaceAlbedo.asset", voxelName);
        private string voxelEnvironmentLightVolumetricBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_environmentVolumetric.asset", voxelName);

        private string voxelCombinedDirectLightSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedDirectLightSurface.asset", voxelName);
        private string voxelCombinedDirectLightSurfaceAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedDirectLightSurfaceAlbedo.asset", voxelName);

        private string voxelBounceLightSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_bounceSurface.asset", voxelName);
        private string voxelBounceLightSurfaceAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_bounceSurfaceAlbedo.asset", voxelName);
        private string voxelBounceLightVolumeBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_bounceVolumetric.asset", voxelName);

        private string voxelCombinedSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedSurface.asset", voxelName);
        private string voxelCombinedSurfaceAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedSurfaceAlbedo.asset", voxelName);
        private string voxelCombinedVolumetricBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedVolumetric.asset", voxelName);
        private string voxelCombinedSurfaceAndVolumetricBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedSurfaceVolumetric.asset", voxelName);

        private string environmentMapAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_environment.exr", voxelName);
        private string voxelPreviewAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_voxelPreview.mat", voxelName);
        private string volumetricFogPreviewAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_fogPreview.mat", voxelName);

        private GameObject voxelCameraGameObject;
        private Camera voxelCamera;

        private ComputeShader voxelDirectSurfaceLight;
        private ComputeShader voxelDirectVolumetricLight;
        private ComputeShader voxelBounceSurfaceLight;
        private ComputeShader voxelBounceVolumetricLight;
        private ComputeShader voxelEnvironmentSurfaceLight;
        private ComputeShader voxelEnvironmentVolumetricLight;
        private ComputeShader combineBuffers;
        private ComputeShader gaussianBlur;
        private ComputeShader voxelizeScene;
        private ComputeShader dilate;

        private Shader voxelPreviewShader => Shader.Find("Hidden/VoxelPreview");
        private Shader volumetricFogPreviewShader => Shader.Find("Hidden/VolumetricFogPreview");

        private ComputeBuffer directionalLightsBuffer = null;
        private ComputeBuffer pointLightsBuffer = null;
        private ComputeBuffer spotLightsBuffer = null;
        private ComputeBuffer areaLightsBuffer = null;

        //private static RenderTextureFormat metaPackedFormat = RenderTextureFormat.ARGB64;
        private static GraphicsFormat metaPackedFormat = GraphicsFormat.R16G16B16A16_UNorm;

        private static RenderTextureFormat unpackedAlbedoBufferFormat = RenderTextureFormat.ARGB32; //NOTE: ARGB1555 is unsupported for random writes
        private static RenderTextureFormat unpackedEmissiveBufferFormat = RenderTextureFormat.ARGBHalf;
        private static RenderTextureFormat unpackedNormalBufferFormat = RenderTextureFormat.ARGB32; //NOTE: ARGB1555 is unsupported for random writes

        private TextureFormat textureFormat => halfPrecisionLighting ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;
        private RenderTextureFormat renderTextureFormat => halfPrecisionLighting ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat;

        private RenderTextureConverterV2 renderTextureConverter => new RenderTextureConverterV2();

        private MetaPassRenderingV2.MetaPassRenderingV2 metaPassRenderer;

        /// <summary>
        /// Load in necessary resources for the voxel tracer.
        /// </summary>
        private bool GetResources()
        {
            voxelDirectSurfaceLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelDirectSurfaceLightAssetPath);
            voxelDirectVolumetricLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelDirectVolumetricLightAssetPath);
            voxelBounceSurfaceLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelBounceSurfaceLightAssetPath);
            voxelBounceVolumetricLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelBounceVolumetricLightBufferAssetPath);
            voxelEnvironmentSurfaceLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelEnvironmentSurfaceLightAssetPath);
            voxelEnvironmentVolumetricLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelEnvironmentVolumetricLightAssetPath);
            gaussianBlur = AssetDatabase.LoadAssetAtPath<ComputeShader>(gaussianBlurAssetPath);
            combineBuffers = AssetDatabase.LoadAssetAtPath<ComputeShader>(combineBuffersAssetPath);
            voxelizeScene = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelizeSceneAssetPath);
            //dilate = AssetDatabase.LoadAssetAtPath<ComputeShader>(dilateAssetPath);

            if (voxelDirectSurfaceLight == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelDirectSurfaceLightAssetPath));
                return false;
            }
            else if(voxelDirectVolumetricLight == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelDirectVolumetricLightAssetPath));
                return false;
            }
            else if(voxelBounceSurfaceLight == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelBounceSurfaceLightAssetPath));
                return false;
            }
            else if(voxelBounceVolumetricLight == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelBounceVolumetricLightBufferAssetPath));
                return false;
            }
            else if(voxelEnvironmentSurfaceLight == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelEnvironmentSurfaceLightAssetPath));
                return false;
            }
            else if(voxelEnvironmentVolumetricLight == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelEnvironmentVolumetricLightAssetPath));
                return false;
            }
            else if(gaussianBlur == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", gaussianBlurAssetPath));
                return false;
            }
            else if(combineBuffers == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", combineBuffersAssetPath));
                return false;
            }
            else if(voxelizeScene == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelizeSceneAssetPath));
                return false;
            }
            //else if (dilate == null)
            //{
                //Debug.LogError(string.Format("{0} does not exist!", dilateAssetPath));
                //return false;
            //}

            return true;
        }

        /// <summary>
        /// Loads in all of the generated textures from the voxel tracer.
        /// <para>If some don't exist, they are just simply null.</para>
        /// </summary>
        public void GetGeneratedContent()
        {
            voxelAlbedoBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelAlbedoBufferAssetPath);
            voxelNormalBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelNormalBufferAssetPath);
            voxelEmissiveBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelEmissiveBufferAssetPath);

            voxelDirectLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelDirectLightSurfaceBufferAssetPath);
            voxelDirectLightSurfaceAlbedoBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelDirectLightSurfaceAlbedoBufferAssetPath);
            voxelDirectLightVolumeBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelDirectLightVolumeBufferAssetPath);

            if (enableEnvironmentLighting)
            {
                voxelEnvironmentLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelEnvironmentLightSurfaceBufferAssetPath);
                voxelEnvironmentLightSurfaceAlbedoBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelEnvironmentLightSurfaceAlbedoBufferAssetPath);
                voxelEnvironmentLightVolumeBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelEnvironmentLightVolumetricBufferAssetPath);
            }

            voxelCombinedDirectLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelCombinedDirectLightSurfaceBufferAssetPath);
            voxelCombinedDirectLightSurfaceAlbedoBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelCombinedDirectLightSurfaceAlbedoBufferAssetPath);
            voxelCombinedSurfaceAndVolumetricBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelCombinedSurfaceAndVolumetricBufferAssetPath);

            voxelBounceLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelBounceLightSurfaceBufferAssetPath);
            voxelBounceLightSurfaceAlbedoBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelBounceLightSurfaceAlbedoBufferAssetPath);
            voxelBounceLightVolumeBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelBounceLightVolumeBufferAssetPath);
        }

        /// <summary>
        /// Sets up the local asset directory to store the generated files from the tracer.
        /// </summary>
        public void SetupAssetFolders()
        {
            //check if there is a data folder, if not then create one
            if (AssetDatabase.IsValidFolder(localAssetDataFolder) == false)
                AssetDatabase.CreateFolder(localAssetFolder, "Data");

            //check if the scene is a valid one before setting up a local "scene" folder in our local asset directory.
            if (activeScene.IsValid() == false || string.IsNullOrEmpty(activeScene.path))
            {
                string message = "Scene is not valid! Be sure to save the scene before you setup volumetrics for it!";
                EditorUtility.DisplayDialog("Error", message, "OK");
                Debug.LogError(message);
                return;
            }

            //check if there is a folder sharing the scene name, if there isn't then create one
            if (AssetDatabase.IsValidFolder(localAssetSceneDataFolder) == false)
                AssetDatabase.CreateFolder(localAssetDataFolder, activeScene.name);
        }

        /// <summary>
        /// Saves a given Texture3D asset to the local voxel asset directory under the current scene name.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="tex3D"></param>
        public void SaveVolumeTexture(string fileName, Texture3D tex3D)
        {
            string volumeAssetPath = localAssetSceneDataFolder + "/" + fileName + ".asset";

            AssetDatabase.CreateAsset(tex3D, volumeAssetPath);
        }

        /// <summary>
        /// Gets all Unity Lights in the scene, and builds compute buffers of them.
        /// <para>This is used only when doing Direct Light tracing.</para>
        /// </summary>
        public void BuildLightComputeBuffers()
        {
            //|||||||||||||||||||||||||||||||||||||||||| GET SCENE LIGHTS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| GET SCENE LIGHTS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| GET SCENE LIGHTS ||||||||||||||||||||||||||||||||||||||||||

            List<VoxelLightDirectional> voxelLightDirectionals = new List<VoxelLightDirectional>();
            List<VoxelLightPoint> voxelLightPoints = new List<VoxelLightPoint>();
            List<VoxelLightSpot> voxelLightSpots = new List<VoxelLightSpot>();
            List<VoxelLightArea> voxelLightAreas = new List<VoxelLightArea>();

            foreach (Light sceneLight in FindObjectsOfType<Light>())
            {
                if (lightLayerMask == (lightLayerMask | (1 << sceneLight.gameObject.layer)) == false)
                    continue;

                if (sceneLight.type == LightType.Directional)
                {
                    VoxelLightDirectional voxelLightDirectional = new VoxelLightDirectional(sceneLight);
                    voxelLightDirectionals.Add(voxelLightDirectional);
                }
                else if (sceneLight.type == LightType.Point)
                {
                    VoxelLightPoint voxelLightPoint = new VoxelLightPoint(sceneLight);
                    voxelLightPoints.Add(voxelLightPoint);
                }
                else if (sceneLight.type == LightType.Spot)
                {
                    VoxelLightSpot voxelLightSpot = new VoxelLightSpot(sceneLight);
                    voxelLightSpots.Add(voxelLightSpot);
                }
                else if (sceneLight.type == LightType.Area)
                {
                    VoxelLightArea voxelLightArea = new VoxelLightArea(sceneLight);
                    voxelLightAreas.Add(voxelLightArea);
                }
            }

            //|||||||||||||||||||||||||||||||||||||||||| CLEAR LIGHT BUFFERS (If Used) ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| CLEAR LIGHT BUFFERS (If Used) ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| CLEAR LIGHT BUFFERS (If Used) ||||||||||||||||||||||||||||||||||||||||||
            //just making sure that these are absolutely cleared and cleaned up.

            if (directionalLightsBuffer != null)
            {
                directionalLightsBuffer.Release();
                directionalLightsBuffer.Dispose();
            }

            if (pointLightsBuffer != null)
            {
                pointLightsBuffer.Release();
                pointLightsBuffer.Dispose();
            }

            if (spotLightsBuffer != null)
            {
                spotLightsBuffer.Release();
                spotLightsBuffer.Dispose();
            }

            if (areaLightsBuffer != null)
            {
                areaLightsBuffer.Release();
                areaLightsBuffer.Dispose();
            }

            directionalLightsBuffer = null;
            pointLightsBuffer = null;
            spotLightsBuffer = null;
            areaLightsBuffer = null;

            //|||||||||||||||||||||||||||||||||||||||||| BUILD SCENE LIGHT BUFFERS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| BUILD SCENE LIGHT BUFFERS ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| BUILD SCENE LIGHT BUFFERS ||||||||||||||||||||||||||||||||||||||||||

            //build directional light buffer
            if (voxelLightDirectionals.Count > 0)
            {
                directionalLightsBuffer = new ComputeBuffer(voxelLightDirectionals.Count, VoxelLightDirectional.GetByteSize());
                directionalLightsBuffer.SetData(voxelLightDirectionals.ToArray());
            }

            //build point light buffer
            if (voxelLightPoints.Count > 0)
            {
                pointLightsBuffer = new ComputeBuffer(voxelLightPoints.Count, VoxelLightPoint.GetByteSize());
                pointLightsBuffer.SetData(voxelLightPoints.ToArray());
            }

            //build spot light buffer
            if (voxelLightSpots.Count > 0)
            {
                spotLightsBuffer = new ComputeBuffer(voxelLightSpots.Count, VoxelLightSpot.GetByteSize());
                spotLightsBuffer.SetData(voxelLightSpots.ToArray());
            }

            //build area light buffer
            if (voxelLightAreas.Count > 0)
            {
                areaLightsBuffer = new ComputeBuffer(voxelLightAreas.Count, VoxelLightArea.GetByteSize());
                areaLightsBuffer.SetData(voxelLightAreas.ToArray());
            }

            Debug.Log(string.Format("[Directional: {0}] [Spot: {1}] [Point: {2}] [Area: {3}]", voxelLightDirectionals.Count, voxelLightSpots.Count, voxelLightPoints.Count, voxelLightAreas.Count));
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 0: SETUP VOXEL CAPTURE ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 0: SETUP VOXEL CAPTURE ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 0: SETUP VOXEL CAPTURE ||||||||||||||||||||||||||||||||||||||||||
        //This is where we construct our voxel capture rig that will be used when we voxelize the scene in step 1.

        /// <summary>
        /// Creates a GameObject with a Camera for Voxel Capture.
        /// </summary>
        public void CreateVoxelCamera()
        {
            if (voxelCameraGameObject == null)
                voxelCameraGameObject = new GameObject("VoxelizeSceneCamera");

            if (voxelCamera == null)
                voxelCamera = voxelCameraGameObject.AddComponent<Camera>();

            voxelCamera.enabled = false;
            voxelCamera.forceIntoRenderTexture = true;
            voxelCamera.useOcclusionCulling = false;
            voxelCamera.allowMSAA = false;
            voxelCamera.orthographic = true;
            voxelCamera.nearClipPlane = 0.0f;
            voxelCamera.farClipPlane = voxelDensitySize;
            voxelCamera.clearFlags = CameraClearFlags.Color;
            voxelCamera.backgroundColor = new Color(0, 0, 0, 0);
            voxelCamera.depthTextureMode = DepthTextureMode.None;
            voxelCamera.renderingPath = RenderingPath.Forward;
        }

        /// <summary>
        /// Destroys the Voxel Camera.
        /// </summary>
        public void CleanupVoxelCamera()
        {
            if (voxelCameraGameObject != null)
                DestroyImmediate(voxelCameraGameObject);

            if (voxelCamera != null)
                DestroyImmediate(voxelCamera);

            voxelCameraGameObject = null;
            voxelCamera = null;
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 1: CAPTURE ALBEDO/EMISSIVE VOXEL BUFFERS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 1: CAPTURE ALBEDO/EMISSIVE VOXEL BUFFERS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 1: CAPTURE ALBEDO/EMISSIVE VOXEL BUFFERS ||||||||||||||||||||||||||||||||||||||||||
        //This is where we voxelize the current scene within the volume bounds.
        // - [Albedo Buffer]
        // This is used for the main scene color (RGB), but it is also used importantly for occlusion checking (A) when tracing.
        //
        // - [Emissive Buffer]
        // This is used to capture any emissive materials in the scene.
        // This is added in the direct light pass, and it's actual emission of light is calculated in the bounce lighting phase.
        //
        // - [Normal Buffer]
        // This is used only when 'normalOrientedHemisphereSampling' is enabled to orient cosine hemispheres when calculating bounce surface lighting.

        public void GenerateAlbedoEmissiveNormalBuffers()
        {
            metaPassRenderer = new MetaPassRenderingV2.MetaPassRenderingV2();
            metaPassRenderer.dilationPixelSize = dilationPixelSize;
            metaPassRenderer.minimumBufferResolution = minimumBufferResolution;
            metaPassRenderer.objectLayerMask = objectLayerMask;
            metaPassRenderer.onlyUseGIContributors = onlyUseGIContributors;
            metaPassRenderer.onlyUseMeshesWithinBounds = onlyUseMeshesWithinBounds;
            metaPassRenderer.onlyUseShadowCasters = onlyUseShadowCasters;
            metaPassRenderer.performDilation = performDilation;
            metaPassRenderer.texelDensityPerUnit = texelDensityPerUnit;
            metaPassRenderer.useBoundingBoxCullingForRendering = useBoundingBoxCullingForRendering;
            metaPassRenderer.sceneObjectsBounds = voxelBounds;
            metaPassRenderer.doubleSidedGeometry = doubleSidedGeometry;

            bool getResourcesResult = GetResources();

            if (getResourcesResult == false)
                return; //if resource gathering functions returned false, that means something failed so don't continue

            UpdateProgressBar("Preparing to generate albedo/normal/emissive...", 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.
            CreateVoxelCamera(); //Create our voxel camera rig

            int renderTextureDepthBits = 32; //bits for the render texture used by the voxel camera (technically 16 does just fine?)

            UpdateProgressBar("Building object meta buffers...", 0.5f);

            List<MetaPassRenderingV2.ObjectMetaData> objectMetaData = metaPassRenderer.ExtractSceneObjectMetaBuffers();

            //|||||||||||||||||||||||||||||||||||||| PREPARE SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| PREPARE SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| PREPARE SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||

            UpdateProgressBar("Rendering scene...", 0.5f);

            //compute per voxel position offset values.
            float xOffset = voxelSize.x / voxelResolution.x;
            float yOffset = voxelSize.y / voxelResolution.y;
            float zOffset = voxelSize.z / voxelResolution.z;

            //pre-fetch our voxelize kernel function in the compute shader.
            int ComputeShader_VoxelizeScene_X_POS = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene_X_POS");
            int ComputeShader_VoxelizeScene_X_NEG = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene_X_NEG");
            int ComputeShader_VoxelizeScene_Y_POS = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene_Y_POS");
            int ComputeShader_VoxelizeScene_Y_NEG = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene_Y_NEG");
            int ComputeShader_VoxelizeScene_Z_POS = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene_Z_POS");
            int ComputeShader_VoxelizeScene_Z_NEG = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene_Z_NEG");

            //make sure the voxelize shader knows our voxel resolution beforehand.
            voxelizeScene.SetVector(ShaderIDs.VolumeResolution, new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            //create our 3D render texture, which will be accumulating 2D slices of the scene captured at various axis.
            RenderTexture sceneAlbedo = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, unpackedAlbedoBufferFormat);
            sceneAlbedo.dimension = TextureDimension.Tex3D;
            sceneAlbedo.filterMode = FilterMode.Point;
            sceneAlbedo.wrapMode = TextureWrapMode.Clamp;
            sceneAlbedo.volumeDepth = voxelResolution.z;
            sceneAlbedo.enableRandomWrite = true;
            sceneAlbedo.Create();

            RenderTexture sceneEmissive = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, unpackedEmissiveBufferFormat);
            sceneEmissive.dimension = TextureDimension.Tex3D;
            sceneEmissive.filterMode = FilterMode.Point;
            sceneEmissive.wrapMode = TextureWrapMode.Clamp;
            sceneEmissive.volumeDepth = voxelResolution.z;
            sceneEmissive.enableRandomWrite = true;
            sceneEmissive.Create();

            RenderTexture sceneNormal = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, unpackedNormalBufferFormat);
            sceneNormal.dimension = TextureDimension.Tex3D;
            sceneNormal.filterMode = FilterMode.Point;
            sceneNormal.wrapMode = TextureWrapMode.Clamp;
            sceneNormal.volumeDepth = voxelResolution.z;
            sceneNormal.enableRandomWrite = true;
            sceneNormal.Create();

            float timeBeforeRendering = Time.realtimeSinceStartup;

            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the X axis.

            using (CommandBuffer sceneAlbedoCommandBuffer = new CommandBuffer())
            {
                //create a 2D render texture based off our voxel resolution to capture the scene in the X axis.
                RenderTexture voxelPackedCameraSlice = new RenderTexture(voxelResolution.z, voxelResolution.y, renderTextureDepthBits, metaPackedFormat);
                voxelPackedCameraSlice.filterMode = FilterMode.Point;
                voxelPackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                voxelPackedCameraSlice.enableRandomWrite = true;
                voxelCamera.targetTexture = voxelPackedCameraSlice;
                voxelCamera.orthographicSize = voxelSize.y * 0.5f;

                RenderTexture albedoUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                albedoUnpackedCameraSlice.filterMode = FilterMode.Point;
                albedoUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                albedoUnpackedCameraSlice.enableRandomWrite = true;
                albedoUnpackedCameraSlice.Create();

                RenderTexture emissiveUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                emissiveUnpackedCameraSlice.filterMode = FilterMode.Point;
                emissiveUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                emissiveUnpackedCameraSlice.enableRandomWrite = true;
                emissiveUnpackedCameraSlice.Create();

                RenderTexture normalUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                normalUnpackedCameraSlice.filterMode = FilterMode.Point;
                normalUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                normalUnpackedCameraSlice.enableRandomWrite = true;
                normalUnpackedCameraSlice.Create();

                //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //orient the voxel camera to face the positive X axis.
                voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 90.0f, 0);

                for (int i = 0; i < voxelResolution.x; i++)
                {
                    //step through the scene on the X axis
                    voxelCameraGameObject.transform.position = transform.position - new Vector3(voxelSize.x / 2.0f, 0, 0) + new Vector3(xOffset * i, 0, 0);
                    metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelPackedCameraSlice);
                    metaPassRenderer.UnpackSceneRender(voxelPackedCameraSlice, albedoUnpackedCameraSlice, emissiveUnpackedCameraSlice, normalUnpackedCameraSlice);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);

                    //albedo
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendAlbedoVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.CameraVoxelRender, albedoUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.Write, sceneAlbedo);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //emissive
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendEmissiveVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.CameraVoxelRender, emissiveUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.Write, sceneEmissive);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //normal
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendNormalVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.CameraVoxelRender, normalUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.Write, sceneNormal);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);
                }

                //||||||||||||||||||||||||||||||||| X NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| X NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| X NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //orient the voxel camera to face the negative X axis.
                voxelCameraGameObject.transform.eulerAngles = new Vector3(0, -90.0f, 0);

                for (int i = 0; i < voxelResolution.x; i++)
                {
                    //step through the scene on the X axis
                    voxelCameraGameObject.transform.position = transform.position + new Vector3(voxelSize.x / 2.0f, 0, 0) - new Vector3(xOffset * i, 0, 0);
                    metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelPackedCameraSlice);
                    metaPassRenderer.UnpackSceneRender(voxelPackedCameraSlice, albedoUnpackedCameraSlice, emissiveUnpackedCameraSlice, normalUnpackedCameraSlice);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);

                    //albedo
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendAlbedoVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.CameraVoxelRender, albedoUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.Write, sceneAlbedo);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //emissive
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendEmissiveVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.CameraVoxelRender, emissiveUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.Write, sceneEmissive);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //normal
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendNormalVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.CameraVoxelRender, normalUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.Write, sceneNormal);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);
                }

                //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
                voxelPackedCameraSlice.Release();
                albedoUnpackedCameraSlice.Release();
                emissiveUnpackedCameraSlice.Release();
                normalUnpackedCameraSlice.Release();

                //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                //captures the scene on the Y axis.

                //create a 2D render texture based off our voxel resolution to capture the scene in the Y axis.
                voxelPackedCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.z, renderTextureDepthBits, metaPackedFormat);
                voxelPackedCameraSlice.filterMode = FilterMode.Point;
                voxelPackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                voxelPackedCameraSlice.enableRandomWrite = true;
                voxelCamera.targetTexture = voxelPackedCameraSlice;
                voxelCamera.orthographicSize = voxelSize.z * 0.5f;

                albedoUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                albedoUnpackedCameraSlice.filterMode = FilterMode.Point;
                albedoUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                albedoUnpackedCameraSlice.enableRandomWrite = true;
                albedoUnpackedCameraSlice.Create();

                emissiveUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                emissiveUnpackedCameraSlice.filterMode = FilterMode.Point;
                emissiveUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                emissiveUnpackedCameraSlice.enableRandomWrite = true;
                emissiveUnpackedCameraSlice.Create();

                normalUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                normalUnpackedCameraSlice.filterMode = FilterMode.Point;
                normalUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                normalUnpackedCameraSlice.enableRandomWrite = true;
                normalUnpackedCameraSlice.Create();

                //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //orient the voxel camera to face the positive Y axis.
                voxelCameraGameObject.transform.eulerAngles = new Vector3(-90.0f, 0, 0);

                for (int i = 0; i < voxelResolution.y; i++)
                {
                    //step through the scene on the Y axis
                    voxelCameraGameObject.transform.position = transform.position - new Vector3(0, voxelSize.y / 2.0f, 0) + new Vector3(0, yOffset * i, 0);
                    metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelPackedCameraSlice);
                    metaPassRenderer.UnpackSceneRender(voxelPackedCameraSlice, albedoUnpackedCameraSlice, emissiveUnpackedCameraSlice, normalUnpackedCameraSlice);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);

                    //albedo
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendAlbedoVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.CameraVoxelRender, albedoUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.Write, sceneAlbedo);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //emissive
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendEmissiveVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.CameraVoxelRender, emissiveUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.Write, sceneEmissive);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //normal
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendNormalVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.CameraVoxelRender, normalUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.Write, sceneNormal);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);
                }

                //||||||||||||||||||||||||||||||||| Y NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //orient the voxel camera to face the negative Y axis.
                voxelCameraGameObject.transform.eulerAngles = new Vector3(90.0f, 0, 0);

                for (int i = 0; i < voxelResolution.y; i++)
                {
                    //step through the scene on the Y axis
                    voxelCameraGameObject.transform.position = transform.position + new Vector3(0, voxelSize.y / 2.0f, 0) - new Vector3(0, yOffset * i, 0);
                    metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelPackedCameraSlice);
                    metaPassRenderer.UnpackSceneRender(voxelPackedCameraSlice, albedoUnpackedCameraSlice, emissiveUnpackedCameraSlice, normalUnpackedCameraSlice);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);

                    //albedo
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendAlbedoVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.CameraVoxelRender, albedoUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.Write, sceneAlbedo);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //emissive
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendEmissiveVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.CameraVoxelRender, emissiveUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.Write, sceneEmissive);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //normal
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendNormalVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.CameraVoxelRender, normalUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.Write, sceneNormal);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);
                }

                //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
                voxelPackedCameraSlice.Release();
                albedoUnpackedCameraSlice.Release();
                emissiveUnpackedCameraSlice.Release();
                normalUnpackedCameraSlice.Release();

                //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
                //captures the scene on the Z axis.

                //create a 2D render texture based off our voxel resolution to capture the scene in the Z axis.
                voxelPackedCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.y, renderTextureDepthBits, metaPackedFormat);
                voxelPackedCameraSlice.filterMode = FilterMode.Point;
                voxelPackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                voxelPackedCameraSlice.enableRandomWrite = true;
                voxelCamera.targetTexture = voxelPackedCameraSlice;
                voxelCamera.orthographicSize = voxelSize.y * 0.5f;

                albedoUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                albedoUnpackedCameraSlice.filterMode = FilterMode.Point;
                albedoUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                albedoUnpackedCameraSlice.enableRandomWrite = true;
                albedoUnpackedCameraSlice.Create();

                emissiveUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                emissiveUnpackedCameraSlice.filterMode = FilterMode.Point;
                emissiveUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                emissiveUnpackedCameraSlice.enableRandomWrite = true;
                emissiveUnpackedCameraSlice.Create();

                normalUnpackedCameraSlice = new RenderTexture(voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, renderTextureDepthBits, metaPackedFormat);
                normalUnpackedCameraSlice.filterMode = FilterMode.Point;
                normalUnpackedCameraSlice.wrapMode = TextureWrapMode.Clamp;
                normalUnpackedCameraSlice.enableRandomWrite = true;
                normalUnpackedCameraSlice.Create();

                //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
                //orient the voxel camera to face the positive Z axis.
                voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 0, 0);

                for (int i = 0; i < voxelResolution.z; i++)
                {
                    //step through the scene on the Z axis
                    voxelCameraGameObject.transform.position = transform.position - new Vector3(0, 0, voxelSize.z / 2.0f) + new Vector3(0, 0, zOffset * i);
                    metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelPackedCameraSlice);
                    metaPassRenderer.UnpackSceneRender(voxelPackedCameraSlice, albedoUnpackedCameraSlice, emissiveUnpackedCameraSlice, normalUnpackedCameraSlice);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);

                    //albedo
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendAlbedoVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.CameraVoxelRender, albedoUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.Write, sceneAlbedo);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //emissive
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendEmissiveVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.CameraVoxelRender, emissiveUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.Write, sceneEmissive);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //normal
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendNormalVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.CameraVoxelRender, normalUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.Write, sceneNormal);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_POS, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);
                }

                //||||||||||||||||||||||||||||||||| Z NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Z NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Z NEGATIVE AXIS |||||||||||||||||||||||||||||||||
                //orient the voxel camera to face the negative Z axis.
                voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 180.0f, 0);

                for (int i = 0; i < voxelResolution.z; i++)
                {
                    //step through the scene on the Z axis
                    voxelCameraGameObject.transform.position = transform.position + new Vector3(0, 0, voxelSize.z / 2.0f) - new Vector3(0, 0, zOffset * i);
                    metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelPackedCameraSlice);
                    metaPassRenderer.UnpackSceneRender(voxelPackedCameraSlice, albedoUnpackedCameraSlice, emissiveUnpackedCameraSlice, normalUnpackedCameraSlice);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);

                    //albedo
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendAlbedoVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.CameraVoxelRender, albedoUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.Write, sceneAlbedo);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //emissive
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendEmissiveVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.CameraVoxelRender, emissiveUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.Write, sceneEmissive);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);

                    //normal
                    SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendNormalVoxelSlices);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.CameraVoxelRender, normalUnpackedCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.Write, sceneNormal);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_NEG, voxelPackedCameraSlice.width, voxelPackedCameraSlice.height, 1);
                }

                //release the render texture slice, because we are done with it...
                voxelPackedCameraSlice.Release();
                albedoUnpackedCameraSlice.Release();
                emissiveUnpackedCameraSlice.Release();
                normalUnpackedCameraSlice.Release();

                Debug.Log(string.Format("Rendering took {0} seconds.", Time.realtimeSinceStartup - timeBeforeRendering));
            }

            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //final step, save our accumulated 3D texture to the disk.

            UpdateProgressBar("Saving Volume...", 0.5f);

            float timeBeforeVolumeSaving = Time.realtimeSinceStartup;

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(sceneAlbedo, voxelAlbedoBufferAssetPath, true);
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(sceneEmissive, voxelEmissiveBufferAssetPath, false);
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(sceneNormal, voxelNormalBufferAssetPath, false);

            Debug.Log(string.Format("Volume Saving took {0} seconds.", Time.realtimeSinceStartup - timeBeforeVolumeSaving));

            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||

            //get rid of this junk, don't need it no more.
            CleanupVoxelCamera();

            metaPassRenderer.CleanUpSceneObjectMetaBuffers(objectMetaData);

            Debug.Log(string.Format("Total Function Time: {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));

            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: CAPTURE ENVIRONMENT ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: CAPTURE ENVIRONMENT ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: CAPTURE ENVIRONMENT ||||||||||||||||||||||||||||||||||||||||||

        public void CaptureEnvironment()
        {
            if (enableEnvironmentLighting == false)
                return;

            if (customEnvironmentMap != null)
                return;

            GameObject environmentCameraGameObject = new GameObject("EnvironmentProbe");
            ReflectionProbe environmentProbe = environmentCameraGameObject.AddComponent<ReflectionProbe>();

            environmentProbe.cullingMask = 0;
            environmentProbe.hdr = true;
            environmentProbe.resolution = environmentResolution;

            //use the lightmapping API to our advantage to simplify things.
            Lightmapping.BakeReflectionProbe(environmentProbe, environmentMapAssetPath);

            environmentMap = AssetDatabase.LoadAssetAtPath<Cubemap>(environmentMapAssetPath);

            DestroyImmediate(environmentProbe);
            DestroyImmediate(environmentCameraGameObject);
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform direct SURFACE lighting on the voxelized scene.
        //
        //This is the among the lightest compute shader functions we have...

        public void TraceDirectSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Preparing direct surface lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            BuildLightComputeBuffers(); //Get all unity scene lights ready to use in the compute shader.

            //consruct our render texture that we will write into
            RenderTexture directSurfaceTrace = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            directSurfaceTrace.dimension = TextureDimension.Tex3D;
            directSurfaceTrace.wrapMode = TextureWrapMode.Clamp;
            directSurfaceTrace.filterMode = FilterMode.Point;
            directSurfaceTrace.volumeDepth = voxelResolution.z;
            directSurfaceTrace.enableRandomWrite = true;
            directSurfaceTrace.Create();

            //fetch our main direct surface light function kernel in the compute shader
            int ComputeShader_TraceSurfaceDirectLight = voxelDirectSurfaceLight.FindKernel("ComputeShader_TraceSurfaceDirectLight");
            voxelDirectSurfaceLight.GetKernelThreadGroupSizes(ComputeShader_TraceSurfaceDirectLight, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //make sure the compute shader knows the following parameters.
            voxelDirectSurfaceLight.SetInt("VolumeMipCount", voxelAlbedoBuffer.mipmapCount);
            voxelDirectSurfaceLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelDirectSurfaceLight.SetVector("VolumePosition", transform.position);
            voxelDirectSurfaceLight.SetVector("VolumeSize", voxelSize);
            voxelDirectSurfaceLight.SetFloat("AlbedoBoost", albedoBoost);
            voxelDirectSurfaceLight.SetInt("MaxDirectSamples", directSurfaceSamples);

            //make sure the compute shader knows what sets of lights we have.
            SetComputeKeyword(voxelDirectSurfaceLight, "DIRECTIONAL_LIGHTS", directionalLightsBuffer != null);
            SetComputeKeyword(voxelDirectSurfaceLight, "POINT_LIGHTS", pointLightsBuffer != null);
            SetComputeKeyword(voxelDirectSurfaceLight, "SPOT_LIGHTS", spotLightsBuffer != null);
            SetComputeKeyword(voxelDirectSurfaceLight, "AREA_LIGHTS", areaLightsBuffer != null);

            //build a small dummy compute buffer, so that we can use GetData later to perform a CPU stall.
            ComputeBuffer dummyComputeBuffer = new ComputeBuffer(1, 4);
            dummyComputeBuffer.SetData(new int[1]);

            for (int i = 0; i < directSurfaceSamples; i++)
            {
                //randomize the seed for noise sampling (THIS IS IMPORTANT)
                voxelDirectSurfaceLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                //feed the compute shader the constructed compute buffers of the unity lights we gathered if they exist.
                if (directionalLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "DirectionalLights", directionalLightsBuffer);
                if (pointLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "PointLights", pointLightsBuffer);
                if (spotLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "SpotLights", spotLightsBuffer);
                if (areaLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "AreaLights", areaLightsBuffer);

                //feed our compute shader the appropriate buffers so we can use them.
                voxelDirectSurfaceLight.SetTexture(ComputeShader_TraceSurfaceDirectLight, "SceneAlbedo", voxelAlbedoBuffer); //most important one, contains scene color and "occlusion".
                voxelDirectSurfaceLight.SetTexture(ComputeShader_TraceSurfaceDirectLight, "SceneNormal", voxelNormalBuffer); //this actually isn't needed and used at the moment.

                voxelDirectSurfaceLight.SetTexture(ComputeShader_TraceSurfaceDirectLight, "Write", directSurfaceTrace);

                //send a tiny dummy compute buffer so we can do a CPU stall later if enabled.
                voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "DummyComputeBuffer", dummyComputeBuffer);

                //let the GPU compute direct surface lighting, and hope it can manage it :D
                voxelDirectSurfaceLight.Dispatch(ComputeShader_TraceSurfaceDirectLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
                //voxelDirectSurfaceLight.Dispatch(ComputeShader_TraceSurfaceDirectLight, THREAD_GROUP_SIZE_X, THREAD_GROUP_SIZE_Y, THREAD_GROUP_SIZE_Z);
                //voxelDirectSurfaceLight.Dispatch(ComputeShader_TraceSurfaceDirectLight, voxelResolution.x, voxelResolution.y, voxelResolution.z);

                //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                if (i % GPU_Readback_Limit == 0 && enableGPU_Readback_Limit)
                {
                    int[] dummyData = new int[1];
                    dummyComputeBuffer.GetData(dummyData);
                }

                UpdateProgressBar(string.Format("Tracing Direct Surface Light... [SAMPLES: {0} / {1}]", i + 1, directSurfaceSamples), 0.5f);
            }

            //|||||||||||||||||||||||||||||||||||||||||| SAVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| SAVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| SAVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(directSurfaceTrace, voxelDirectLightSurfaceBufferAssetPath);

            //|||||||||||||||||||||||||||||||||||||||||| ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ALBEDO ||||||||||||||||||||||||||||||||||||||||||

            //consruct our render texture that we will write into
            RenderTexture directSurfaceAlbedoTrace = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            directSurfaceAlbedoTrace.dimension = TextureDimension.Tex3D;
            directSurfaceAlbedoTrace.wrapMode = TextureWrapMode.Clamp;
            directSurfaceAlbedoTrace.filterMode = FilterMode.Point;
            directSurfaceAlbedoTrace.volumeDepth = voxelResolution.z;
            directSurfaceAlbedoTrace.enableRandomWrite = true;
            directSurfaceAlbedoTrace.Create();

            GetGeneratedContent();

            int ComputeShader_CombineAlbedoWithLighting = combineBuffers.FindKernel("ComputeShader_CombineAlbedoWithLighting");
            combineBuffers.SetFloat("AlbedoBoost", albedoBoost);
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferA", voxelAlbedoBuffer); //albedo
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferB", voxelDirectLightSurfaceBuffer); //lighting
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "Write", directSurfaceAlbedoTrace);
            combineBuffers.Dispatch(ComputeShader_CombineAlbedoWithLighting, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(directSurfaceAlbedoTrace, voxelDirectLightSurfaceAlbedoBufferAssetPath);

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //after surviving the onslaught of computations, we will save our results to the disk.

            //we are done with the compute buffers of the unity lights, so clean them up.
            if (directionalLightsBuffer != null) directionalLightsBuffer.Release();
            if (pointLightsBuffer != null) pointLightsBuffer.Release();
            if (spotLightsBuffer != null) spotLightsBuffer.Release();
            if (areaLightsBuffer != null) areaLightsBuffer.Release();

            Debug.Log(string.Format("'TraceDirectSurfaceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 4: TRACE DIRECT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 4: TRACE DIRECT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 4: TRACE DIRECT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform direct VOLUMETRIC lighting on the voxelized scene.
        //
        //This is definetly slightly more expensive than the surface tracing version.
        //It can definetly get intensive with a large amount of lights, or with a dense enough voxel resolution.
        //This should be optimized later with random importance sampling of lights just like the one before.
        //
        //But... for the time being compared to the bounce functions later this is relatively light and doesn't cause GPU driver timeouts. 

        public void TraceDirectVolumeLighting()
        {
            UpdateProgressBar(string.Format("Tracing Direct Volume Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.
            BuildLightComputeBuffers(); //Get all unity scene lights ready to use in the compute shader.

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.wrapMode = TextureWrapMode.Clamp;
            volumeWrite.filterMode = FilterMode.Point;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //fetch our main direct volumetric light function kernel in the compute shader
            int ComputeShader_TraceVolumeDirectLight = voxelDirectVolumetricLight.FindKernel("ComputeShader_TraceVolumeDirectLight");
            voxelDirectVolumetricLight.GetKernelThreadGroupSizes(ComputeShader_TraceVolumeDirectLight, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //make sure the compute shader knows the following parameters.
            voxelDirectVolumetricLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelDirectVolumetricLight.SetVector("VolumePosition", transform.position);
            voxelDirectVolumetricLight.SetVector("VolumeSize", voxelSize);
            voxelDirectVolumetricLight.SetFloat("AlbedoBoost", albedoBoost);
            voxelDirectVolumetricLight.SetInt("MaxDirectSamples", directVolumetricSamples);

            //make sure the compute shader knows what sets of lights we have.
            SetComputeKeyword(voxelDirectVolumetricLight, "DIRECTIONAL_LIGHTS", directionalLightsBuffer != null);
            SetComputeKeyword(voxelDirectVolumetricLight, "POINT_LIGHTS", pointLightsBuffer != null);
            SetComputeKeyword(voxelDirectVolumetricLight, "SPOT_LIGHTS", spotLightsBuffer != null);
            SetComputeKeyword(voxelDirectVolumetricLight, "AREA_LIGHTS", areaLightsBuffer != null);

            //build a small dummy compute buffer, so that we can use GetData later to perform a CPU stall.
            ComputeBuffer dummyComputeBuffer = new ComputeBuffer(1, 4);
            dummyComputeBuffer.SetData(new int[1]);

            for (int i = 0; i < directVolumetricSamples; i++)
            {
                //randomize the seed for noise sampling (THIS IS IMPORTANT)
                voxelDirectVolumetricLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                //feed the compute shader the constructed compute buffers of the unity lights we gathered if they exist.
                if (directionalLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "DirectionalLights", directionalLightsBuffer);
                if (pointLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "PointLights", pointLightsBuffer);
                if (spotLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "SpotLights", spotLightsBuffer);
                if (areaLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "AreaLights", areaLightsBuffer);

                //feed our compute shader the appropriate buffers so we can use them.
                voxelDirectVolumetricLight.SetTexture(ComputeShader_TraceVolumeDirectLight, "SceneAlbedo", voxelAlbedoBuffer); //most important one, contains scene color and "occlusion".
                voxelDirectVolumetricLight.SetTexture(ComputeShader_TraceVolumeDirectLight, "Write", volumeWrite);

                //send a tiny dummy compute buffer so we can do a CPU stall later if enabled.
                voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "DummyComputeBuffer", dummyComputeBuffer);

                //let the GPU compute direct surface lighting, and hope it can manage it :D
                voxelDirectVolumetricLight.Dispatch(ComputeShader_TraceVolumeDirectLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                if (i % GPU_Readback_Limit == 0 && enableGPU_Readback_Limit)
                {
                    int[] dummyData = new int[1];
                    dummyComputeBuffer.GetData(dummyData);
                }

                UpdateProgressBar(string.Format("Tracing Direct Volumetric Light... [SAMPLES: {0} / {1}]", i + 1, directVolumetricSamples), 0.5f);
            }

            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //In an attempt to squeeze more out of less...
            //We will now perform a 3D gaussian blur to smooth out the results from the direct volumetric light.
            //(IF ITS ENABLED)

            if (volumetricDirectGaussianSamples > 0)
            {
                //fetch our main gaussian blur function kernel in the compute shader
                int ComputeShader_GaussianBlur = gaussianBlur.FindKernel("ComputeShader_GaussianBlur");
                gaussianBlur.GetKernelThreadGroupSizes(ComputeShader_GaussianBlur, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

                //convert the raw volumetric bounce light render texture into a texture3D so that it can be read.
                Texture3D tempRawVolumetricBounceLight = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //make sure the compute shader knows the following parameters.
                gaussianBlur.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
                gaussianBlur.SetInt("BlurSamples", volumetricDirectGaussianSamples);

                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(1, 0, 0, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempRawVolumetricBounceLight);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //get the result from the x pass and convert it into a texture3D so that it can be read again.
                Texture3D tempBlurX = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 1, 0, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempBlurX);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //get the result from the y pass and convert it into a texture3D so that it can be read one more time.
                Texture3D tempBlurY = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 0, 1, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempBlurY);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //after surviving the onslaught of computations, we will save our results to the disk.

            UpdateProgressBar(string.Format("Saving to disk..."), 0.5f);

            //save it!
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeWrite, voxelDirectLightVolumeBufferAssetPath);

            //we are done with the compute buffers of the unity lights, so clean them up.
            if (directionalLightsBuffer != null) directionalLightsBuffer.Release();
            if (pointLightsBuffer != null) pointLightsBuffer.Release();
            if (spotLightsBuffer != null) spotLightsBuffer.Release();
            if (areaLightsBuffer != null) areaLightsBuffer.Release();

            Debug.Log(string.Format("'TraceDirectVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE ENVIRONMENT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE ENVIRONMENT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE ENVIRONMENT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform environment SURFACE lighting on the voxelized scene.

        public void TraceEnvironmentSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Preparing environment surface lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //fetch our main bounce surface light function kernel in the compute shader
            int ComputeShader_TraceSurfaceEnvironmentLight = voxelEnvironmentSurfaceLight.FindKernel("ComputeShader_TraceSurfaceEnvironmentLight");
            voxelEnvironmentSurfaceLight.GetKernelThreadGroupSizes(ComputeShader_TraceSurfaceEnvironmentLight, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //make sure the compute shader knows the following parameters.
            voxelEnvironmentSurfaceLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelEnvironmentSurfaceLight.SetVector("VolumePosition", transform.position);
            voxelEnvironmentSurfaceLight.SetVector("VolumeSize", voxelSize);
            voxelEnvironmentSurfaceLight.SetInt("MaxEnvironmentSamples", environmentSurfaceSamples);
            voxelEnvironmentSurfaceLight.SetFloat("EnvironmentIntensity", environmentIntensity);
            voxelEnvironmentSurfaceLight.SetFloat("AlbedoBoost", albedoBoost);

            //if enabled, use a normal oriented cosine hemisphere for better ray allocation/quality (though it has some issues/quirks)
            SetComputeKeyword(voxelEnvironmentSurfaceLight, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.wrapMode = TextureWrapMode.Clamp;
            volumeWrite.filterMode = FilterMode.Point;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //build a small dummy compute buffer, so that we can use GetData later to perform a CPU stall.
            ComputeBuffer dummyComputeBuffer = new ComputeBuffer(1, 4);
            dummyComputeBuffer.SetData(new int[1]);

            for (int i = 0; i < environmentSurfaceSamples; i++)
            {
                //randomize the seed for noise sampling (THIS IS IMPORTANT)
                voxelEnvironmentSurfaceLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                //feed our compute shader the appropriate buffers so we can use them.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "SceneAlbedo", voxelAlbedoBuffer); //important, used for "occlusion" checking.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "SceneNormal", voxelNormalBuffer); //important, used to help orient hemispheres when enabled.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "EnvironmentMap", environmentMap); //important, the main color that we will be bouncing around.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "Write", volumeWrite);

                //send a tiny dummy compute buffer so we can do a CPU stall later if enabled.
                voxelEnvironmentSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceEnvironmentLight, "DummyComputeBuffer", dummyComputeBuffer);

                //let the GPU compute bounced surface lighting, and hope it can manage it :(
                voxelEnvironmentSurfaceLight.Dispatch(ComputeShader_TraceSurfaceEnvironmentLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                if (i % GPU_Readback_Limit == 0 && enableGPU_Readback_Limit)
                {
                    int[] dummyData = new int[1];
                    dummyComputeBuffer.GetData(dummyData);
                }

                UpdateProgressBar(string.Format("Environment Surface Light... [SAMPLES: {0} / {1}]", i + 1, environmentSurfaceSamples), 0.5f);
            }

            //|||||||||||||||||||||||||||||||||||||||||| SAVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| SAVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| SAVE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeWrite, voxelEnvironmentLightSurfaceBufferAssetPath);

            //|||||||||||||||||||||||||||||||||||||||||| ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ALBEDO ||||||||||||||||||||||||||||||||||||||||||

            //consruct our render texture that we will write into
            RenderTexture environmentSurfaceAlbedoTrace = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            environmentSurfaceAlbedoTrace.dimension = TextureDimension.Tex3D;
            environmentSurfaceAlbedoTrace.wrapMode = TextureWrapMode.Clamp;
            environmentSurfaceAlbedoTrace.filterMode = FilterMode.Point;
            environmentSurfaceAlbedoTrace.volumeDepth = voxelResolution.z;
            environmentSurfaceAlbedoTrace.enableRandomWrite = true;
            environmentSurfaceAlbedoTrace.Create();

            GetGeneratedContent();

            int ComputeShader_CombineAlbedoWithLighting = combineBuffers.FindKernel("ComputeShader_CombineAlbedoWithLighting");
            combineBuffers.SetFloat("AlbedoBoost", albedoBoost);
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferA", voxelAlbedoBuffer); //albedo
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferB", voxelEnvironmentLightSurfaceBuffer); //lighting
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "Write", environmentSurfaceAlbedoTrace);
            combineBuffers.Dispatch(ComputeShader_CombineAlbedoWithLighting, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(environmentSurfaceAlbedoTrace, voxelEnvironmentLightSurfaceAlbedoBufferAssetPath);

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //if we have survived the onslaught of computations... FANTASTIC! lets save our results to the disk before we lose it.

            //we are done with this, so clean up.
            volumeWrite.DiscardContents(true, true);
            volumeWrite.Release();

            Debug.Log(string.Format("'TraceEnvironmentSurfaceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: TRACE ENVIRONMENT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: TRACE ENVIRONMENT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: TRACE ENVIRONMENT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform environment VOLUMETRIC lighting on the voxelized scene.
        //
        //This is definetly slightly more expensive than the surface tracing version.
        //It can definetly get intensive with a large amount of lights, or with a dense enough voxel resolution.
        //This should be optimized later with random importance sampling of lights just like the one before.
        //
        //But... for the time being compared to the bounce functions later this is relatively light and doesn't cause GPU driver timeouts. 

        public void TraceEnvironmentVolumeLighting()
        {
            UpdateProgressBar(string.Format("Tracing Environment Volume Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.
            BuildLightComputeBuffers(); //Get all unity scene lights ready to use in the compute shader.

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.wrapMode = TextureWrapMode.Clamp;
            volumeWrite.filterMode = FilterMode.Point;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //fetch our main direct volumetric light function kernel in the compute shader
            int ComputeShader_TraceVolumetricEnvironmentLight = voxelEnvironmentVolumetricLight.FindKernel("ComputeShader_TraceVolumetricEnvironmentLight");
            voxelEnvironmentVolumetricLight.GetKernelThreadGroupSizes(ComputeShader_TraceVolumetricEnvironmentLight, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //make sure the compute shader knows the following parameters.
            voxelEnvironmentVolumetricLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelEnvironmentVolumetricLight.SetVector("VolumePosition", transform.position);
            voxelEnvironmentVolumetricLight.SetVector("VolumeSize", voxelSize);
            voxelEnvironmentVolumetricLight.SetFloat("EnvironmentIntensity", environmentIntensity);
            voxelEnvironmentVolumetricLight.SetInt("MaxEnvironmentSamples", environmentVolumetricSamples);

            //build a small dummy compute buffer, so that we can use GetData later to perform a CPU stall.
            ComputeBuffer dummyComputeBuffer = new ComputeBuffer(1, 4);
            dummyComputeBuffer.SetData(new int[1]);

            for (int i = 0; i < environmentVolumetricSamples; i++)
            {
                //randomize the seed for noise sampling (THIS IS IMPORTANT)
                voxelEnvironmentVolumetricLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                //feed our compute shader the appropriate buffers so we can use them.
                voxelEnvironmentVolumetricLight.SetTexture(ComputeShader_TraceVolumetricEnvironmentLight, "SceneAlbedo", voxelAlbedoBuffer); //most important one, contains scene color and "occlusion".
                voxelEnvironmentVolumetricLight.SetTexture(ComputeShader_TraceVolumetricEnvironmentLight, "EnvironmentMap", environmentMap); //important, the main color that we will be bouncing around.
                voxelEnvironmentVolumetricLight.SetTexture(ComputeShader_TraceVolumetricEnvironmentLight, "Write", volumeWrite);

                //send a tiny dummy compute buffer so we can do a CPU stall later if enabled.
                voxelEnvironmentVolumetricLight.SetBuffer(ComputeShader_TraceVolumetricEnvironmentLight, "DummyComputeBuffer", dummyComputeBuffer);

                //let the GPU compute direct surface lighting, and hope it can manage it :D
                voxelEnvironmentVolumetricLight.Dispatch(ComputeShader_TraceVolumetricEnvironmentLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                if (i % GPU_Readback_Limit == 0 && enableGPU_Readback_Limit)
                {
                    int[] dummyData = new int[1];
                    dummyComputeBuffer.GetData(dummyData);
                }

                UpdateProgressBar(string.Format("Tracing Environment Volumetric Light... [SAMPLES: {0} / {1}]", i + 1, environmentVolumetricSamples), 0.5f);
            }

            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //In an attempt to squeeze more out of less...
            //We will now perform a 3D gaussian blur to smooth out the results from the direct volumetric light.
            //(IF ITS ENABLED)

            if (volumetricEnvironmentGaussianSamples > 0)
            {
                //fetch our main gaussian blur function kernel in the compute shader
                int ComputeShader_GaussianBlur = gaussianBlur.FindKernel("ComputeShader_GaussianBlur");
                gaussianBlur.GetKernelThreadGroupSizes(ComputeShader_GaussianBlur, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

                //convert the raw volumetric bounce light render texture into a texture3D so that it can be read.
                Texture3D tempRawVolumetricBounceLight = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //make sure the compute shader knows the following parameters.
                gaussianBlur.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
                gaussianBlur.SetInt("BlurSamples", volumetricEnvironmentGaussianSamples);

                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(1, 0, 0, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempRawVolumetricBounceLight);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //get the result from the x pass and convert it into a texture3D so that it can be read again.
                Texture3D tempBlurX = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 1, 0, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempBlurX);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //get the result from the y pass and convert it into a texture3D so that it can be read one more time.
                Texture3D tempBlurY = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 0, 1, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempBlurY);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //after surviving the onslaught of computations, we will save our results to the disk.

            UpdateProgressBar(string.Format("Saving to disk..."), 0.5f);

            //save it!
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeWrite, voxelEnvironmentLightVolumetricBufferAssetPath);

            Debug.Log(string.Format("'TraceEnvironmentVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE SURFACE DIRECT LIGHTING TERMS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE SURFACE DIRECT LIGHTING TERMS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE SURFACE DIRECT LIGHTING TERMS ||||||||||||||||||||||||||||||||||||||||||
        //This is a light operation, so no worries here.

        public void CombineDirectSurfaceLightingTerms()
        {
            UpdateProgressBar(string.Format("Combining Direct Surface Lighting Terms..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //fetch our function kernel in the compute shader
            int ComputeShader_AddBuffers = combineBuffers.FindKernel("ComputeShader_AddBuffers");
            combineBuffers.GetKernelThreadGroupSizes(ComputeShader_AddBuffers, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE ENVIRONMENT LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE ENVIRONMENT LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE ENVIRONMENT LIGHT ||||||||||||||||||||||||||||||||||||||||||

            Texture3D volumeLighting1 = voxelDirectLightSurfaceBuffer;

            if (enableEnvironmentLighting)
            {
                //consruct our render texture that we will write into
                RenderTexture volumeDirectAndEnvironmentLighting = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
                volumeDirectAndEnvironmentLighting.dimension = TextureDimension.Tex3D;
                volumeDirectAndEnvironmentLighting.wrapMode = TextureWrapMode.Clamp;
                volumeDirectAndEnvironmentLighting.filterMode = FilterMode.Point;
                volumeDirectAndEnvironmentLighting.volumeDepth = voxelResolution.z;
                volumeDirectAndEnvironmentLighting.enableRandomWrite = true;
                volumeDirectAndEnvironmentLighting.Create();

                //feed the compute shader the textures that will be added together
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", voxelDirectLightSurfaceBuffer);
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelEnvironmentLightSurfaceBuffer);
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeDirectAndEnvironmentLighting);

                //let the GPU add the textures together.
                combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                volumeLighting1 = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeDirectAndEnvironmentLighting);
            }

            //|||||||||||||||||||||||||||||||||||||||||| COMBINE LIGHT WITH ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMBINE LIGHT WITH ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMBINE LIGHT WITH ALBEDO ||||||||||||||||||||||||||||||||||||||||||

            RenderTexture volumeLightWithAlbedo = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeLightWithAlbedo.dimension = TextureDimension.Tex3D;
            volumeLightWithAlbedo.wrapMode = TextureWrapMode.Clamp;
            volumeLightWithAlbedo.filterMode = FilterMode.Point;
            volumeLightWithAlbedo.volumeDepth = voxelResolution.z;
            volumeLightWithAlbedo.enableRandomWrite = true;
            volumeLightWithAlbedo.Create();

            int ComputeShader_CombineAlbedoWithLighting = combineBuffers.FindKernel("ComputeShader_CombineAlbedoWithLighting");
            combineBuffers.GetKernelThreadGroupSizes(ComputeShader_CombineAlbedoWithLighting, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            combineBuffers.SetFloat("AlbedoBoost", albedoBoost);
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferA", voxelAlbedoBuffer); //albedo
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferB", volumeLighting1); //lighting
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "Write", volumeLightWithAlbedo);
            combineBuffers.Dispatch(ComputeShader_CombineAlbedoWithLighting, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            Texture3D volumeCombinedLightWithAlbedo = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeLightWithAlbedo);

            //|||||||||||||||||||||||||||||||||||||||||| ADD EMISSIVE LIGHT TO COMBINED LIGHT/ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD EMISSIVE LIGHT TO COMBINED LIGHT/ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD EMISSIVE LIGHT TO COMBINED LIGHT/ALBEDO ||||||||||||||||||||||||||||||||||||||||||

            RenderTexture volumeLightWithAlbedoAndEmissive = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeLightWithAlbedoAndEmissive.dimension = TextureDimension.Tex3D;
            volumeLightWithAlbedoAndEmissive.wrapMode = TextureWrapMode.Clamp;
            volumeLightWithAlbedoAndEmissive.filterMode = FilterMode.Point;
            volumeLightWithAlbedoAndEmissive.volumeDepth = voxelResolution.z;
            volumeLightWithAlbedoAndEmissive.enableRandomWrite = true;
            volumeLightWithAlbedoAndEmissive.Create();

            combineBuffers.GetKernelThreadGroupSizes(ComputeShader_AddBuffers, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //feed the compute shader the textures that will be added together
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", volumeCombinedLightWithAlbedo);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelEmissiveBuffer);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeLightWithAlbedoAndEmissive);

            //let the GPU add the textures together.
            combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //save results to the disk
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeLightWithAlbedoAndEmissive, voxelCombinedDirectLightSurfaceAlbedoBufferAssetPath);

            //|||||||||||||||||||||||||||||||||||||||||| ADD EMISSIVE LIGHT TO COMBINED LIGHT ONLY ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD EMISSIVE LIGHT TO COMBINED LIGHT ONLY ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD EMISSIVE LIGHT TO COMBINED LIGHT ONLY ||||||||||||||||||||||||||||||||||||||||||

            RenderTexture volumeLightWithEmissive = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeLightWithEmissive.dimension = TextureDimension.Tex3D;
            volumeLightWithEmissive.wrapMode = TextureWrapMode.Clamp;
            volumeLightWithEmissive.filterMode = FilterMode.Point;
            volumeLightWithEmissive.volumeDepth = voxelResolution.z;
            volumeLightWithEmissive.enableRandomWrite = true;
            volumeLightWithEmissive.Create();

            combineBuffers.GetKernelThreadGroupSizes(ComputeShader_AddBuffers, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //feed the compute shader the textures that will be added together
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", volumeLighting1);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelEmissiveBuffer);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeLightWithEmissive);

            //let the GPU add the textures together.
            combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //save results to the disk
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeLightWithEmissive, voxelCombinedDirectLightSurfaceBufferAssetPath);

            Debug.Log(string.Format("'CombineDirectSurfaceLightingTerms' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 8: TRACE BOUNCE SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 8: TRACE BOUNCE SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 8: TRACE BOUNCE SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform bounced SURFACE lighting on the voxelized scene.
        //
        //This is the second most intensive operation we do.
        //Luckily it doesn't scale with the amount of lights we have, but it does obviously scale with voxel resolution and the amount of samples we do.

        public void TraceBounceSurfaceLighting()
        {
            UpdateProgressBar("Preparing to bounce surface light...", 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.wrapMode = TextureWrapMode.Clamp;
            volumeWrite.filterMode = FilterMode.Point;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //the current bounce that we are on, we will start off with bouncing from the direct surface lighting.
            Texture3D bounceTemp = voxelCombinedDirectLightSurfaceAlbedoBuffer;

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            //fetch our main bounce surface light function kernel in the compute shader
            int ComputeShader_TraceSurfaceBounceLight = voxelBounceSurfaceLight.FindKernel("ComputeShader_TraceSurfaceBounceLight");
            voxelBounceSurfaceLight.GetKernelThreadGroupSizes(ComputeShader_TraceSurfaceBounceLight, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //make sure the compute shader knows the following parameters.
            voxelBounceSurfaceLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelBounceSurfaceLight.SetVector("VolumePosition", transform.position);
            voxelBounceSurfaceLight.SetVector("VolumeSize", voxelSize);
            voxelBounceSurfaceLight.SetInt("MaxBounceSamples", bounceSurfaceSamples);
            voxelBounceSurfaceLight.SetFloat("IndirectIntensity", indirectIntensity);
            voxelBounceSurfaceLight.SetFloat("AlbedoBoost", albedoBoost);
            voxelBounceSurfaceLight.SetFloat("EnvironmentIntensity", environmentIntensity);

            //if enabled, use a normal oriented cosine hemisphere for better ray allocation/quality (though it has some issues/quirks)
            SetComputeKeyword(voxelBounceSurfaceLight, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

            //build a small dummy compute buffer, so that we can use GetData later to perform a CPU stall.
            ComputeBuffer dummyComputeBuffer = new ComputeBuffer(1, 4);
            dummyComputeBuffer.SetData(new int[1]);

            for (int i = 0; i < bounces; i++)
            {
                for (int j = 0; j < bounceSurfaceSamples; j++)
                {
                    //randomize the seed for noise sampling (THIS IS IMPORTANT)
                    voxelBounceSurfaceLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                    //feed our compute shader the appropriate buffers so we can use them.
                    voxelBounceSurfaceLight.SetTexture(ComputeShader_TraceSurfaceBounceLight, "SceneAlbedo", voxelAlbedoBuffer); //important, used for "occlusion" checking.
                    voxelBounceSurfaceLight.SetTexture(ComputeShader_TraceSurfaceBounceLight, "SceneNormal", voxelNormalBuffer); //important, used to help orient hemispheres when enabled.
                    voxelBounceSurfaceLight.SetTexture(ComputeShader_TraceSurfaceBounceLight, "DirectLightSurface", bounceTemp); //important, the main color that we will be bouncing around.
                    voxelBounceSurfaceLight.SetTexture(ComputeShader_TraceSurfaceBounceLight, "Write", volumeWrite);

                    //send a tiny dummy compute buffer so we can do a CPU stall later if enabled.
                    voxelBounceSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceBounceLight, "DummyComputeBuffer", dummyComputeBuffer);

                    //let the GPU compute bounced surface lighting, and hope it can manage it :(
                    voxelBounceSurfaceLight.Dispatch(ComputeShader_TraceSurfaceBounceLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                    //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                    if (i % GPU_Readback_Limit == 0 && enableGPU_Readback_Limit)
                    {
                        int[] dummyData = new int[1];
                        dummyComputeBuffer.GetData(dummyData);
                    }

                    UpdateProgressBar(string.Format("Bouncing Surface Light... [BOUNCES: {0} / {1}] [SAMPLES: {2} / {3}]", i + 1, bounces, j + 1, bounceSurfaceSamples), 0.5f);
                }

                //if we are doing more than 1 bounce
                if (i > 0)
                {
                    //convert our finished bounced lighting into a Texture3D so we can reuse it again for the next bounce
                    bounceTemp = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite, false, false, false);
                }
            }

            UpdateProgressBar(string.Format("Saving to disk..."), 0.5f);

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeWrite, voxelBounceLightSurfaceBufferAssetPath);

            GetGeneratedContent();

            //|||||||||||||||||||||||||||||||||||||||||| COMBINE BOUNCE LIGHT WITH ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMBINE BOUNCE LIGHT WITH ALBEDO ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMBINE BOUNCE LIGHT WITH ALBEDO ||||||||||||||||||||||||||||||||||||||||||

            RenderTexture volumeBounceLightWithAlbedo = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeBounceLightWithAlbedo.dimension = TextureDimension.Tex3D;
            volumeBounceLightWithAlbedo.wrapMode = TextureWrapMode.Clamp;
            volumeBounceLightWithAlbedo.filterMode = FilterMode.Point;
            volumeBounceLightWithAlbedo.volumeDepth = voxelResolution.z;
            volumeBounceLightWithAlbedo.enableRandomWrite = true;
            volumeBounceLightWithAlbedo.Create();

            int ComputeShader_CombineAlbedoWithLighting = combineBuffers.FindKernel("ComputeShader_CombineAlbedoWithLighting");
            combineBuffers.GetKernelThreadGroupSizes(ComputeShader_CombineAlbedoWithLighting, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            combineBuffers.SetFloat("AlbedoBoost", albedoBoost);
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferA", voxelAlbedoBuffer); //albedo
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "BufferB", voxelBounceLightSurfaceBuffer); //lighting
            combineBuffers.SetTexture(ComputeShader_CombineAlbedoWithLighting, "Write", volumeBounceLightWithAlbedo);
            combineBuffers.Dispatch(ComputeShader_CombineAlbedoWithLighting, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeBounceLightWithAlbedo, voxelBounceLightSurfaceAlbedoBufferAssetPath);

            Debug.Log(string.Format("'TraceBounceSurfaceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 9: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 9: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 9: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform bounced VOLUMETRIC lighting on the voxelized scene.
        //
        //This is by far the most intensive operation we do.
        //Luckily it doesn't scale with the amount of lights we have, but it does obviously scale with voxel resolution and the amount of samples we do.

        public void TraceBounceVolumeLighting()
        {
            UpdateProgressBar(string.Format("Preparing to bounce volumetric light..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.wrapMode = TextureWrapMode.Clamp;
            volumeWrite.filterMode = FilterMode.Point;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //the current bounce that we are on, we will start off with bouncing from the direct surface lighting.
            Texture3D bounceTemp = voxelCombinedDirectLightSurfaceAlbedoBuffer;

            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            if (enableEnvironmentLighting)
            {
                //fetch our function kernel in the compute shader
                int ComputeShader_AddBuffers = combineBuffers.FindKernel("ComputeShader_AddBuffers");
                combineBuffers.GetKernelThreadGroupSizes(ComputeShader_AddBuffers, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

                //feed the compute shader the textures that will be added together
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", bounceTemp);
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelEnvironmentLightSurfaceBuffer);
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeWrite);

                //let the GPU add the textures together.
                combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                bounceTemp = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);
            }

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            //fetch our main bounce volumetric light function kernel in the compute shader
            int ComputeShader_TraceVolumeBounceLight = voxelBounceVolumetricLight.FindKernel("ComputeShader_TraceVolumeBounceLight");
            voxelBounceVolumetricLight.GetKernelThreadGroupSizes(ComputeShader_TraceVolumeBounceLight, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //make sure the compute shader knows the following parameters.
            voxelBounceVolumetricLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelBounceVolumetricLight.SetVector("VolumePosition", transform.position);
            voxelBounceVolumetricLight.SetVector("VolumeSize", voxelSize);
            voxelBounceVolumetricLight.SetInt("MaxBounceSamples", bounceVolumetricSamples);
            voxelBounceVolumetricLight.SetFloat("IndirectIntensity", indirectIntensity);
            voxelBounceVolumetricLight.SetFloat("AlbedoBoost", albedoBoost);

            //build a small dummy compute buffer, so that we can use GetData later to perform a CPU stall.
            ComputeBuffer dummyComputeBuffer = new ComputeBuffer(1, 4);
            dummyComputeBuffer.SetData(new int[1]);

            for (int i = 0; i < bounceVolumetricSamples; i++)
            {
                //randomize the seed for noise sampling (THIS IS IMPORTANT)
                voxelBounceVolumetricLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                //feed our compute shader the appropriate buffers so we can use them.
                voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "SceneAlbedo", voxelAlbedoBuffer); //important, used for "occlusion" checking.
                //voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "SceneNormal", voxelNormalBuffer); //this isn't used at all.
                voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "DirectLightSurface", bounceTemp); //important, the main color that we will be bouncing around.
                voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "Write", volumeWrite);

                //send a tiny dummy compute buffer so we can do a CPU stall later if enabled.
                voxelBounceVolumetricLight.SetBuffer(ComputeShader_TraceVolumeBounceLight, "DummyComputeBuffer", dummyComputeBuffer);

                voxelBounceVolumetricLight.Dispatch(ComputeShader_TraceVolumeBounceLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                if (i % GPU_Readback_Limit == 0 && enableGPU_Readback_Limit)
                {
                    int[] dummyData = new int[1];
                    dummyComputeBuffer.GetData(dummyData);
                }

                UpdateProgressBar(string.Format("Bouncing Volumetric Light... [SAMPLES: {0} / {1}]", i + 1, bounceVolumetricSamples), 0.5f);
            }

            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //In an attempt to squeeze more out of less...
            //We will now perform a 3D gaussian blur to smooth out the results from the bounced volumetric light.
            //(IF ITS ENABLED)

            if (volumetricBounceGaussianSamples > 0)
            {
                UpdateProgressBar(string.Format("Performing Gaussian Blur..."), 0.5f);

                //fetch our main gaussian blur function kernel in the compute shader
                int ComputeShader_GaussianBlur = gaussianBlur.FindKernel("ComputeShader_GaussianBlur");
                gaussianBlur.GetKernelThreadGroupSizes(ComputeShader_GaussianBlur, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

                //convert the raw volumetric bounce light render texture into a texture3D so that it can be read.
                Texture3D tempRawVolumetricBounceLight = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //make sure the compute shader knows the following parameters.
                gaussianBlur.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
                gaussianBlur.SetInt("BlurSamples", volumetricBounceGaussianSamples);

                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR X PASS ||||||||||||||||||||||||||||||||||||||||||
                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(1, 0, 0, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempRawVolumetricBounceLight);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Y PASS ||||||||||||||||||||||||||||||||||||||||||
                //get the result from the x pass and convert it into a texture3D so that it can be read again.
                Texture3D tempBlurX = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 1, 0, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempBlurX);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //|||||||||||||||||||||||||||||||||||||||||| BLUR Z PASS ||||||||||||||||||||||||||||||||||||||||||
                //get the result from the y pass and convert it into a texture3D so that it can be read one more time.
                Texture3D tempBlurY = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);

                //set the gaussian blur direction for this pass.
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 0, 1, 0));

                //feed our compute shader the appropriate textures.
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Read", tempBlurY);
                gaussianBlur.SetTexture(ComputeShader_GaussianBlur, "Write", volumeWrite);

                //let the GPU perform a gaussian blur along the given axis.
                gaussianBlur.Dispatch(ComputeShader_GaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //if we have SOMEHOW survived the onslaught of computations... FANTASTIC! lets save our results to the disk before we lose it.

            UpdateProgressBar(string.Format("Saving to disk..."), 0.5f);

            //SAVE IT!
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeWrite, voxelBounceLightVolumeBufferAssetPath);

            Debug.Log(string.Format("'TraceBounceVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 10: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 10: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 10: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we simply combine the generated surface light buffers into one single texture.
        //This is a light operation, so no worries here.

        public void CombineSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Combining Surface Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT (NO ALBEDO) ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT (NO ALBEDO) ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT (NO ALBEDO) ||||||||||||||||||||||||||||||||||||||||||

            //consruct our render texture that we will write into
            RenderTexture volumeFinalLighting = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeFinalLighting.dimension = TextureDimension.Tex3D;
            volumeFinalLighting.wrapMode = TextureWrapMode.Clamp;
            volumeFinalLighting.filterMode = FilterMode.Point;
            volumeFinalLighting.volumeDepth = voxelResolution.z;
            volumeFinalLighting.enableRandomWrite = true;
            volumeFinalLighting.Create();

            //fetch our function kernel in the compute shader
            int ComputeShader_AddBuffers = combineBuffers.FindKernel("ComputeShader_AddBuffers");
            combineBuffers.GetKernelThreadGroupSizes(ComputeShader_AddBuffers, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //feed the compute shader the textures that will be added together
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", voxelCombinedDirectLightSurfaceBuffer);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelBounceLightSurfaceBuffer);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeFinalLighting);

            //let the GPU add the textures together.
            combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeFinalLighting, voxelCombinedSurfaceBufferAssetPath);

            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT (WITH ALBEDO) ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT (WITH ALBEDO) ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT (WITH ALBEDO) ||||||||||||||||||||||||||||||||||||||||||

            //consruct our render texture that we will write into
            RenderTexture volumeFinalLightingWithAlbedo = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeFinalLightingWithAlbedo.dimension = TextureDimension.Tex3D;
            volumeFinalLightingWithAlbedo.wrapMode = TextureWrapMode.Clamp;
            volumeFinalLightingWithAlbedo.filterMode = FilterMode.Point;
            volumeFinalLightingWithAlbedo.volumeDepth = voxelResolution.z;
            volumeFinalLightingWithAlbedo.enableRandomWrite = true;
            volumeFinalLightingWithAlbedo.Create();

            //feed the compute shader the textures that will be added together
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", voxelCombinedDirectLightSurfaceAlbedoBuffer);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelBounceLightSurfaceAlbedoBuffer);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeFinalLightingWithAlbedo);

            //let the GPU add the textures together.
            combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeFinalLightingWithAlbedo, voxelCombinedSurfaceAlbedoBufferAssetPath);

            Debug.Log(string.Format("'CombineSurfaceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 11: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 11: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 11: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we simply combine the generated volumetric light buffers into one single texture.
        //This is a light operation, so no worries here.

        public void CombineVolumeLighting()
        {
            UpdateProgressBar(string.Format("Combining Volume Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.wrapMode = TextureWrapMode.Clamp;
            volumeWrite.filterMode = FilterMode.Point;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //fetch our function kernel in the compute shader
            int ComputeShader_AddBuffers = combineBuffers.FindKernel("ComputeShader_AddBuffers");
            combineBuffers.GetKernelThreadGroupSizes(ComputeShader_AddBuffers, out THREAD_GROUP_SIZE_X, out THREAD_GROUP_SIZE_Y, out THREAD_GROUP_SIZE_Z);

            //get the result from the y pass and convert it into a texture3D so that it can be read one more time.
            Texture3D addedColorsTemp = voxelDirectLightVolumeBuffer;

            //|||||||||||||||||||||||||||||||||||||||||| ADD VOLUMETRIC ENVIRONMENT LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD VOLUMETRIC ENVIRONMENT LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD VOLUMETRIC ENVIRONMENT LIGHT ||||||||||||||||||||||||||||||||||||||||||

            if (enableEnvironmentLighting)
            {
                //feed the compute shader the textures that will be added together
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", addedColorsTemp);
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelEnvironmentLightVolumeBuffer);
                combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeWrite);

                //let the GPU add the textures together.
                combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //get the result from the x pass and convert it into a texture3D so that it can be read again.
                addedColorsTemp = renderTextureConverter.ConvertRenderTexture3DToTexture3D(volumeWrite);
            }

            //|||||||||||||||||||||||||||||||||||||||||| ADD VOLUMETRIC BOUNCE LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD VOLUMETRIC BOUNCE LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD VOLUMETRIC BOUNCE LIGHT ||||||||||||||||||||||||||||||||||||||||||

            //feed the compute shader the textures that will be added together
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferA", addedColorsTemp);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "BufferB", voxelBounceLightVolumeBuffer);
            combineBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeWrite);

            //let the GPU add the textures together.
            combineBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //save it!
            renderTextureConverter.SaveRenderTexture3DAsTexture3D(volumeWrite, voxelCombinedVolumetricBufferAssetPath);

            Debug.Log(string.Format("'CombineVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.white;

            if (previewBounds)
                Gizmos.DrawWireCube(transform.position, voxelSize);
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        public static void SetComputeKeyword(ComputeShader computeShader, string keyword, bool value)
        {
            if (value)
                computeShader.EnableKeyword(keyword);
            else
                computeShader.DisableKeyword(keyword);
        }

        public void UpdateProgressBar(string description, float progress) => EditorUtility.DisplayProgressBar("Voxel Tracer", description, progress);

        public void CloseProgressBar() => EditorUtility.ClearProgressBar();

        public void CreateVoxelPreview()
        {
            SetupAssetFolders();

            GameObject voxelPreviewGameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            voxelPreviewGameObject.name = "Voxel Preview";
            voxelPreviewGameObject.transform.position = transform.position;
            voxelPreviewGameObject.transform.localScale = voxelSize;
            voxelPreviewGameObject.transform.SetParent(transform);

            GameObject volumetricFogPreviewGameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            volumetricFogPreviewGameObject.name = "Volumetric Fog Preview";
            volumetricFogPreviewGameObject.transform.position = transform.position;
            volumetricFogPreviewGameObject.transform.localScale = voxelSize;
            volumetricFogPreviewGameObject.transform.SetParent(transform);

            BoxCollider voxelPreviewBoxCollider = voxelPreviewGameObject.GetComponent<BoxCollider>();
            BoxCollider volumetricFogPreviewBoxCollider = volumetricFogPreviewGameObject.GetComponent<BoxCollider>();

            DestroyImmediate(voxelPreviewBoxCollider);
            DestroyImmediate(volumetricFogPreviewBoxCollider);

            MeshRenderer voxelPreviewMeshRenderer = voxelPreviewGameObject.GetComponent<MeshRenderer>();
            MeshRenderer volumetricFogPreviewMeshRenderer = volumetricFogPreviewGameObject.GetComponent<MeshRenderer>();

            Material voxelPreviewMaterial = new Material(voxelPreviewShader);
            Material volumetricFogPreviewMaterial = new Material(volumetricFogPreviewShader);

            voxelPreviewMaterial.SetVector("_VolumePos", voxelPreviewGameObject.transform.position);
            voxelPreviewMaterial.SetVector("_VolumeSize", voxelSize);

            volumetricFogPreviewMaterial.SetVector("_VolumePos", voxelPreviewGameObject.transform.position);
            volumetricFogPreviewMaterial.SetVector("_VolumeSize", voxelSize);

            AssetDatabase.CreateAsset(voxelPreviewMaterial, voxelPreviewAssetPath);
            AssetDatabase.CreateAsset(volumetricFogPreviewMaterial, volumetricFogPreviewAssetPath);

            voxelPreviewMeshRenderer.material = voxelPreviewMaterial;
            volumetricFogPreviewMeshRenderer.material = volumetricFogPreviewMaterial;
        }
    }
}