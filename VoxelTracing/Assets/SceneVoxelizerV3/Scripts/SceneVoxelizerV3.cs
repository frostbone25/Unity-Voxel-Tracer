//using System;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using RenderTextureConverting;
using UnityEngine.Experimental.Rendering;

/*
 * NOTE 1: The Anti-Aliasing when used for the generation of the voxels does help maintain geometry at low resolutions, however there is a quirk because it also samples the background color.
 * This was noticable when rendering position/normals voxel buffers where when on some geo looked darker than usual because the BG color was black.
 * Not to sure how to solve this and just might deal with it?
 * 
 * NOTE TO SELF: Supersampling?
 * 
 * NOTE 2: Might be worth investing time into writing a voxel normal estimator, and a dynamically changing sample type... I'll explain
 * 
 * While generating a voxel buffer of scene normals do work, and is rather trivial there are issues with it.
 * When they are used to orient hemispheres for importance sampling, if a voxel normal is facing the wrong direction, the hemisphere will be oriented incorrectly.
 * As a result sometimes objects will appear to be just purely black or incorrect.
 * So in that case it might be better just to estimate them with the surface albedo to help alleviate this and better align hemispheres with voxels.
 * 
 * In addition to that, sometimes geometry can be only one voxel thin.
 * In that case hemisphere sampling doesn't work, and we should be switching to full sphere sampling so we can get everything around correctly.
*/

namespace SceneVoxelizer3
{
    public class SceneVoxelizerV3 : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        [Header("Voxelization Properties")]
        public string voxelName = "Voxel"; //Name of the asset
        public Vector3 voxelSize = new Vector3(10.0f, 10.0f, 10.0f); //Size of the volume
        public float voxelDensitySize = 1.0f; //Size of each voxel (Smaller = More Voxels, Larger = Less Voxels)

        [Header("Meta Pass Properties")]
        //this controls how many "pixels" per unit an object will have.
        //this is for "meta" textures representing the different buffers of an object (albedo, normal, emissive)
        //LARGER VALUES: more pixels allocated | better quality/accuracy | more memory usage (bigger meta textures for objects)
        //SMALLER VALUES: less pixels allocated | worse quality/accuracy | less memory usage (smaller meta textures for objects)
        public float texelDensityPerUnit = 1;

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

        //this controls whether bilinear filtering is used for the final generated meta textures.
        //this can soften details/sharpness for textures but this could also potentially help with artifacting.
        //ENABLED: This will enable bilinear filtering on meta textures
        //DISABLED: This will enable bilinear filtering on meta textures
        public bool useBilinearFiltering = false;

        [Header("Rendering")]
        //this will perform blending with multiple captured voxel slices of the scene albedo buffer
        //the scene is captured in multiple slices in 6 different axis's, "overdraw" happens for alot of pixels.
        //so during voxelization if a pixel already has data written, we write again but blend with the original result, in theory this should lead to better accuracy of the buffer because each slice depending on the axis is not the exact same every time.
        //ENABLED: averages multiple slices if there is overdraw of pixels, potentially better accuracy.
        //DISABLED: on each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.
        public bool blendAlbedoVoxelSlices = false;

        //this will perform blending with multiple captured voxel slices of the scene albedo buffer
        //the scene is captured in multiple slices in 6 different axis's, "overdraw" happens for alot of pixels.
        //so during voxelization if a pixel already has data written, we write again but blend with the original result, in theory this should lead to better accuracy of the buffer because each slice depending on the axis is not the exact same every time.
        //ENABLED: averages multiple slices if there is overdraw of pixels, potentially better accuracy.
        //DISABLED: on each slice, only the first instance of the color is written, if the same pixel is drawn then it's ignored.
        //NOTE: This could lead to inaccuracy on some surfaces and could create skewed results since some surfaces depending on how they are captured, will have their vectors altered.
        public bool blendEmissiveVoxelSlices = false;

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

        [Header("Optimizations")]
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

        [Header("Gizmos")]
        public bool previewBounds;

        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        //Size of the thread groups for compute shaders.
        //These values should match the #define ones in the compute shaders.
        private static int THREAD_GROUP_SIZE_X = 8;
        private static int THREAD_GROUP_SIZE_Y = 8;
        private static int THREAD_GROUP_SIZE_Z = 8;

        private Vector3Int voxelResolution => new Vector3Int((int)(voxelSize.x / voxelDensitySize), (int)(voxelSize.y / voxelDensitySize), (int)(voxelSize.z / voxelDensitySize));
        private Bounds voxelBounds => new Bounds(transform.position, voxelSize);

        private string localAssetFolder = "Assets/SceneVoxelizerV3";
        private string localAssetShadersFolder = "Assets/SceneVoxelizerV3/Shaders";
        private string localAssetDataFolder = "Assets/SceneVoxelizerV3/Data";
        private string voxelizeSceneAssetPath => localAssetShadersFolder + "/VoxelizeScene.compute";
        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();
        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;

        private ComputeShader voxelizeScene => AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelizeSceneAssetPath);

        private static RenderTextureFormat voxelBufferFormat = RenderTextureFormat.ARGBHalf;
        private static TextureFormat voxelAlbedoBufferFormat = TextureFormat.RGBA32;
        private static TextureFormat voxelEmissiveBufferFormat = TextureFormat.RGBAHalf;
        private static TextureFormat voxelNormalBufferFormat = TextureFormat.RGBA32;

        private RenderTextureConverterV2 renderTextureConverterV2 => new RenderTextureConverterV2();

        private GameObject voxelCameraGameObject;
        private Camera voxelCamera;

        private MetaPassRenderingV1.MetaPassRenderingV1 metaPassRenderer;

        /// <summary>
        /// Load in necessary resources for the voxel tracer.
        /// </summary>
        private bool HasResources()
        {
            if (voxelizeScene == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelizeSceneAssetPath));
                return false;
            }

            return true;
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
            voxelCamera.orthographic = true;
            voxelCamera.nearClipPlane = 0.0f;
            voxelCamera.farClipPlane = voxelDensitySize;
            voxelCamera.clearFlags = CameraClearFlags.SolidColor;
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

        [ContextMenu("GenerateAlbedoEmissiveNormalBuffers")]
        public void GenerateAlbedoEmissiveNormalBuffers()
        {
            metaPassRenderer = new MetaPassRenderingV1.MetaPassRenderingV1();
            metaPassRenderer.dilationPixelSize = dilationPixelSize;
            metaPassRenderer.minimumBufferResolution = minimumBufferResolution;
            metaPassRenderer.objectLayerMask = objectLayerMask;
            metaPassRenderer.onlyUseGIContributors = onlyUseGIContributors;
            metaPassRenderer.onlyUseMeshesWithinBounds = onlyUseMeshesWithinBounds;
            metaPassRenderer.onlyUseShadowCasters = onlyUseShadowCasters;
            metaPassRenderer.performDilation = performDilation;
            metaPassRenderer.texelDensityPerUnit = texelDensityPerUnit;
            metaPassRenderer.useBilinearFiltering = useBilinearFiltering;
            metaPassRenderer.useBoundingBoxCullingForRendering = useBoundingBoxCullingForRendering;
            metaPassRenderer.sceneObjectsBounds = voxelBounds;
            metaPassRenderer.doubleSidedGeometry = doubleSidedGeometry;

            if (HasResources() == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            UpdateProgressBar("Preparing to generate albedo/emissive/normal...", 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.
            CreateVoxelCamera(); //Create our voxel camera rig

            int renderTextureDepthBits = 32; //bits for the render texture used by the voxel camera (technically 16 does just fine?)

            UpdateProgressBar("Building object meta buffers...", 0.5f);

            List<MetaPassRenderingV1.ObjectMetaData> objectMetaData = metaPassRenderer.ExtractSceneObjectMetaBuffers();

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
            int ComputeShader_ClearTexture3D = voxelizeScene.FindKernel("ComputeShader_ClearTexture3D");

            //make sure the voxelize shader knows our voxel resolution beforehand.
            voxelizeScene.SetVector(ShaderIDs.VolumeResolution, new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            //create our 3D render texture, which will be accumulating 2D slices of the scene captured at various axis.
            RenderTexture combinedSceneVoxel = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, voxelBufferFormat);
            combinedSceneVoxel.dimension = TextureDimension.Tex3D;
            combinedSceneVoxel.volumeDepth = voxelResolution.z;
            combinedSceneVoxel.wrapMode = TextureWrapMode.Clamp;
            combinedSceneVoxel.filterMode = FilterMode.Point;
            combinedSceneVoxel.enableRandomWrite = true;
            combinedSceneVoxel.Create();

            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the X axis.

            using (CommandBuffer sceneAlbedoCommandBuffer = new CommandBuffer())
            {
                for(int j = 0; j < 3; j++)
                {
                    float timeBeforeRendering = Time.realtimeSinceStartup;

                    switch (j)
                    {
                        case 0:
                            SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendAlbedoVoxelSlices);
                            break;
                        case 1:
                            SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendEmissiveVoxelSlices);
                            break;
                        case 2:
                            SetComputeKeyword(voxelizeScene, "BLEND_SLICES", blendNormalVoxelSlices);
                            break;
                    }

                    //||||||||||||||||||||||||||||||||| RESIDUAL INFO CLEANUP |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| RESIDUAL INFO CLEANUP |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| RESIDUAL INFO CLEANUP |||||||||||||||||||||||||||||||||

                    voxelizeScene.SetTexture(ComputeShader_ClearTexture3D, ShaderIDs.Write, combinedSceneVoxel);
                    voxelizeScene.Dispatch(ComputeShader_ClearTexture3D, voxelResolution.x, voxelResolution.y, voxelResolution.z);

                    //create a 2D render texture based off our voxel resolution to capture the scene in the X axis.
                    RenderTexture voxelCameraSlice = new RenderTexture(voxelResolution.z, voxelResolution.y, renderTextureDepthBits, voxelBufferFormat);
                    voxelCameraSlice.filterMode = combinedSceneVoxel.filterMode;
                    voxelCameraSlice.wrapMode = combinedSceneVoxel.wrapMode;
                    voxelCameraSlice.enableRandomWrite = true;
                    voxelCamera.targetTexture = voxelCameraSlice;
                    voxelCamera.orthographicSize = voxelSize.y * 0.5f;

                    //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //orient the voxel camera to face the positive X axis.
                    voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 90.0f, 0);

                    for (int i = 0; i < voxelResolution.x; i++)
                    {
                        //step through the scene on the X axis
                        voxelCameraGameObject.transform.position = transform.position - new Vector3(voxelSize.x / 2.0f, 0, 0) + new Vector3(xOffset * i, 0, 0);
                        metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelCameraSlice, j);

                        //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                        voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_POS, ShaderIDs.Write, combinedSceneVoxel);
                        voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_POS, voxelCameraSlice.width, voxelCameraSlice.height, 1);
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
                        metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelCameraSlice, j);

                        //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                        voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_X_NEG, ShaderIDs.Write, combinedSceneVoxel);
                        voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_X_NEG, voxelCameraSlice.width, voxelCameraSlice.height, 1);
                    }

                    //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
                    voxelCameraSlice.Release();

                    //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                    //captures the scene on the Y axis.

                    //create a 2D render texture based off our voxel resolution to capture the scene in the Y axis.
                    voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.z, renderTextureDepthBits, voxelBufferFormat);
                    voxelCameraSlice.filterMode = combinedSceneVoxel.filterMode;
                    voxelCameraSlice.wrapMode = combinedSceneVoxel.wrapMode;
                    voxelCameraSlice.enableRandomWrite = true;
                    voxelCamera.targetTexture = voxelCameraSlice;
                    voxelCamera.orthographicSize = voxelSize.z * 0.5f;

                    //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //orient the voxel camera to face the positive Y axis.
                    voxelCameraGameObject.transform.eulerAngles = new Vector3(-90.0f, 0, 0);

                    for (int i = 0; i < voxelResolution.y; i++)
                    {
                        //step through the scene on the Y axis
                        voxelCameraGameObject.transform.position = transform.position - new Vector3(0, voxelSize.y / 2.0f, 0) + new Vector3(0, yOffset * i, 0);
                        metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelCameraSlice, j);

                        //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                        voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_POS, ShaderIDs.Write, combinedSceneVoxel);
                        voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_POS, voxelCameraSlice.width, voxelCameraSlice.height, 1);
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
                        metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelCameraSlice, j);

                        //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                        voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Y_NEG, ShaderIDs.Write, combinedSceneVoxel);
                        voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Y_NEG, voxelCameraSlice.width, voxelCameraSlice.height, 1);
                    }

                    //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
                    voxelCameraSlice.Release();

                    //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
                    //captures the scene on the Z axis.

                    //create a 2D render texture based off our voxel resolution to capture the scene in the Z axis.
                    voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.y, renderTextureDepthBits, voxelBufferFormat);
                    voxelCameraSlice.filterMode = combinedSceneVoxel.filterMode;
                    voxelCameraSlice.wrapMode = combinedSceneVoxel.wrapMode;
                    voxelCameraSlice.enableRandomWrite = true;
                    voxelCamera.targetTexture = voxelCameraSlice;
                    voxelCamera.orthographicSize = voxelSize.y * 0.5f;

                    //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
                    //orient the voxel camera to face the positive Z axis.
                    voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 0, 0);

                    for (int i = 0; i < voxelResolution.z; i++)
                    {
                        //step through the scene on the Z axis
                        voxelCameraGameObject.transform.position = transform.position - new Vector3(0, 0, voxelSize.z / 2.0f) + new Vector3(0, 0, zOffset * i);
                        metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelCameraSlice, j);

                        //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                        voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_POS, ShaderIDs.Write, combinedSceneVoxel);
                        voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_POS, voxelCameraSlice.width, voxelCameraSlice.height, 1);
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
                        metaPassRenderer.RenderScene(objectMetaData, voxelCamera, voxelCameraSlice, j);

                        //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                        voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                        voxelizeScene.SetTexture(ComputeShader_VoxelizeScene_Z_NEG, ShaderIDs.Write, combinedSceneVoxel);
                        voxelizeScene.Dispatch(ComputeShader_VoxelizeScene_Z_NEG, voxelCameraSlice.width, voxelCameraSlice.height, 1);
                    }

                    //release the render texture slice, because we are done with it...
                    voxelCameraSlice.Release();

                    //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
                    //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
                    //final step, save our accumulated 3D texture to the disk.

                    float timeBeforeVolumeSaving = 0.0f;

                    switch (j)
                    {
                        case 0:
                            Debug.Log(string.Format("Rendering Albedo took {0} seconds.", Time.realtimeSinceStartup - timeBeforeRendering));
                            timeBeforeVolumeSaving = Time.realtimeSinceStartup;
                            //RenderTextureConverter.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_albedo.asset", localAssetSceneDataFolder, voxelName));
                            renderTextureConverterV2.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_albedo.asset", localAssetSceneDataFolder, voxelName));
                            //renderTextureConverterV2.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_albedo.asset", localAssetSceneDataFolder, voxelName), voxelAlbedoBufferFormat, true);
                            //renderTextureConverterV2.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_albedo.asset", localAssetSceneDataFolder, voxelName), GraphicsFormat.R8G8B8A8_UNorm);
                            Debug.Log(string.Format("Albedo Volume Saving took {0} seconds.", Time.realtimeSinceStartup - timeBeforeVolumeSaving));
                            break;
                        case 1:
                            Debug.Log(string.Format("Rendering Emissive took {0} seconds.", Time.realtimeSinceStartup - timeBeforeRendering));
                            timeBeforeVolumeSaving = Time.realtimeSinceStartup;
                            //RenderTextureConverter.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_emissive.asset", localAssetSceneDataFolder, voxelName));
                            renderTextureConverterV2.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_emissive.asset", localAssetSceneDataFolder, voxelName));
                            //renderTextureConverterV2.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_emissive.asset", localAssetSceneDataFolder, voxelName), voxelEmissiveBufferFormat, true);
                            Debug.Log(string.Format("Emissive Volume Saving took {0} seconds.", Time.realtimeSinceStartup - timeBeforeVolumeSaving));
                            break;
                        case 2:
                            Debug.Log(string.Format("Rendering Normal took {0} seconds.", Time.realtimeSinceStartup - timeBeforeRendering));
                            timeBeforeVolumeSaving = Time.realtimeSinceStartup;
                            //RenderTextureConverter.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_normal.asset", localAssetSceneDataFolder, voxelName));
                            renderTextureConverterV2.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_normal.asset", localAssetSceneDataFolder, voxelName));
                            //renderTextureConverterV2.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, string.Format("{0}/SceneVoxelizerV3_{1}_normal.asset", localAssetSceneDataFolder, voxelName), voxelNormalBufferFormat, true);
                            Debug.Log(string.Format("Normal Volume Saving took {0} seconds.", Time.realtimeSinceStartup - timeBeforeVolumeSaving));
                            break;
                    }
                }
            }

            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||

            //get rid of this junk, don't need it no more.
            CleanupVoxelCamera();

            metaPassRenderer.CleanUpSceneObjectMetaBuffers(objectMetaData);

            Debug.Log(string.Format("Total Function Time: {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));

            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.white;

            if (previewBounds)
                Gizmos.DrawWireCube(transform.position, voxelSize);
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        public void UpdateProgressBar(string description, float progress) => EditorUtility.DisplayProgressBar("Scene Voxelizer V3", description, progress);

        public void CloseProgressBar() => EditorUtility.ClearProgressBar();

        public static void SetComputeKeyword(ComputeShader computeShader, string keyword, bool value)
        {
            if (value)
                computeShader.EnableKeyword(keyword);
            else
                computeShader.DisableKeyword(keyword);
        }
    }
}