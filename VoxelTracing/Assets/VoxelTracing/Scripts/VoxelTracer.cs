using System.Collections;
using System.Collections.Generic;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/*
 * NOTE 1: The Anti-Aliasing when used for the generation of the voxels does help maintain geometry at low resolutions, however there is a quirk because it also samples the background color.
 * This was noticable when rendering position/normals voxel buffers where when on some geo looked darker than usual because the BG color was black.
 * Not to sure how to solve this and just might deal with it?
 * 
 * NOTE 2: For best volumetric bounce light results, set bounce light samples for the "surface" tracing very high so volumetric bounce results appear cleaner.
 * 
 * NOTE 3: Bounces do work, however energy needs to be lost (or averaged out?) as more bounces are done. 
 * At the moment they will keep additively getting brighter the more bounces you do... which is not how it should be.
 * 
 * NOTE 4: Surface Bounce Samples, and Volumetric Bounce samples are seperated for good reason.
 * Doing any kind of shading or sampling in the volumetric tracing functions are WAY more heavy than just doing it on surfaces.
 * 
 * NOTE 5: Theoretically, as an optimization for bounced volumetric lighting, we actually don't need to do any additional bounces for it.
 * What matters is that the surface lighting has the bounces it needs, all we do is just throw up the samples in the air and average them.
 * No need to do bounces for the volumetric lighting itself because that will VERY QUICKLY get intensive.
 * 
 * NOTE 6: Might be worth investing time into writing a voxel normal estimator, and a dynamically changing sample type... I'll explain
 * 
 * While generating a voxel buffer of scene normals do work, and is rather trivial there are issues with it.
 * When they are used to orient hemispheres for importance sampling, if a voxel normal is facing the wrong direction, the hemisphere will be oriented incorrectly.
 * As a result sometimes objects will appear to be just purely black or incorrect.
 * So in that case it might be better just to estimate them with the surface albedo to help alleviate this and better align hemispheres with voxels.
 * 
 * In addition to that, sometimes geometry can be only one voxel thin.
 * In that case hemisphere sampling doesn't work, and we should be switching to full sphere sampling so we can get everything around correctly.
 * 
 * NOTE 7: For workload splitting up and optimization.
 * 
 * While the current solutions implemented for splitting up workload by doing 1 sample and accumulating over time does help...
 * We need to find a way to wait for when the GPU is actually complete with such a dispatch, and then make sure that the GPU is free from any computation before issuing another one.
 * It seems like we can't issue dispatches back to back, it seems to overburden the GPU and then we get hit with TDR (driver timeout) which crashes the editor.
 * There has to be a way we can better spread the workload across over time without overburdening the GPU too much, and yet still achieve the high sample counts we want.
*/

namespace UnityVoxelTracer
{
    public class VoxelTracer : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        [Header("Scene Voxelization")]
        public string voxelName = "Voxel"; //Name of the asset
        public Vector3 voxelSize = new Vector3(10.0f, 10.0f, 10.0f); //Size of the volume
        public float voxelDensitySize = 1.0f; //Size of each voxel (Smaller = More Voxels, Larger = Less Voxels)

        //[TODO] This is W.I.P, might be removed.
        //This is supposed to help thicken geometry during voxelization to prevent leaks during tracing.
        private float geometryThicknessModifier = 0.0f;

        //[BROKEN] Anti-Aliasing to retain geometry shapes during voxelization.
        //Used to work... although now it appears break voxelization results however?
        private bool enableAnitAliasing = false;

        //[DEPRECATE] Blend multiple voxels together during voxelization.
        //This isn't really needed, and only used with the old scene voxelization technique.
        //public bool blendVoxelResult = false;

        [Header("Environment Options")]
        public bool enableEnvironmentLighting = true;

        [Range(32, 4096)] public int environmentResolution = 128;

        public Cubemap customEnvironmentMap;

        [Header("Bake Options")]

        //Amount of samples used to bounce light when doing "surface" shading on the voxels.
        [Range(1, 8192)] public int bounceSurfaceSamples = 128;

        //Amount of samples used to bounce light when doing "volumetric" shading.
        [Range(1, 8192)] public int bounceVolumetricSamples = 64;

        //Amount of surface shading bounces to do.
        [Range(1, 8)] public int bounces = 4;

        //Amount of samples used for environment light when doing "surface" shading on the voxels.
        [Range(1, 8192)] public int environmentSurfaceSamples = 64;

        //Amount of samples used for environment light when doing "surface" shading on the voxels.
        [Range(1, 8192)] public int environmentVolumetricSamples = 64;

        //Improve surface shading quality by using a cosine hemisphere oriented with the surface normal.
        //Results in better ray allocation at lower sample counts (though at the moment there are issues with scene normals)
        public bool normalOrientedHemisphereSampling = true;

        [Header("Misc")]

        [Range(1, 32)] public int GPU_Readback_Limit = 4;

        public bool showDetailedProgress = true;

        [Header("Post Volumetric Bake Options")]
        //Applies a 3D gaussian blur to the direct volumetric light term to smooth results out.
        //High samples though means that leaks can occur as this is not voxel/geometry aware.
        [Range(0, 64)] public int volumetricDirectGaussianSamples = 0;

        //Applies a 3D gaussian blur to the bounced volumetric light term to smooth results out.
        //High samples though means that leaks can occur as this is not voxel/geometry aware.
        [Range(0, 64)] public int volumetricBounceGaussianSamples = 0;

        [Header("Gizmos")]
        public bool previewBounds = true;

        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        //Size of the thread groups for compute shaders.
        //These values should match the #define ones in the compute shaders.
        private static int THREAD_GROUP_SIZE_X = 8;
        private static int THREAD_GROUP_SIZE_Y = 8;
        private static int THREAD_GROUP_SIZE_Z = 8;

        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();

        private string localAssetFolder = "Assets/VoxelTracing";
        private string localAssetComputeFolder = "Assets/VoxelTracing/ComputeShaders";
        private string localAssetDataFolder = "Assets/VoxelTracing/Data";

        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;

        private Texture3D voxelAlbedoBuffer;
        private Texture3D voxelNormalBuffer;
        private Texture3D voxelEmissiveBuffer;

        private Texture3D voxelDirectLightSurfaceBuffer;
        private Texture3D voxelEnvironmentLightSurfaceBuffer;
        private Texture3D voxelBounceLightSurfaceBuffer;

        private Texture3D voxelDirectLightVolumeBuffer;
        private Texture3D voxelBounceLightVolumeBuffer;

        private Cubemap environmentMap;

        private string voxelAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_albedo.asset", voxelName);
        private string voxelNormalBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_normal.asset", voxelName);
        private string voxelEmissiveBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_emissive.asset", voxelName);
        private string voxelDirectLightSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_directSurface.asset", voxelName);
        private string voxelEnvironmentLightSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_environmentSurface.asset", voxelName);
        private string voxelBounceLightSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_bounceSurface.asset", voxelName);
        private string voxelCombinedSurfaceBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedSurface.asset", voxelName);
        private string voxelDirectLightVolumeBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_directVolumetric.asset", voxelName);
        private string voxelBounceLightVolumeBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_bounceVolumetric.asset", voxelName);
        private string voxelCombinedVolumetricBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_combinedVolumetric.asset", voxelName);
        private string environmentMapAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_environment.exr", voxelName);

        private RenderTexture voxelCameraSlice;
        private GameObject voxelCameraGameObject;
        private Camera voxelCamera;

        private Vector3Int voxelResolution
        {
            get
            {
                return new Vector3Int((int)(voxelSize.x / voxelDensitySize), (int)(voxelSize.y / voxelDensitySize), (int)(voxelSize.z / voxelDensitySize));
            }
        }

        private ComputeShader slicer;
        private ComputeShader voxelDirectSurfaceLight;
        private ComputeShader voxelDirectVolumetricLight;
        private ComputeShader voxelBounceSurfaceLight;
        private ComputeShader voxelBounceVolumetricLight;
        private ComputeShader voxelEnvironmentSurfaceLight;
        private ComputeShader addBuffers;
        private ComputeShader averageBuffers;
        private ComputeShader multiplyBuffers;
        private ComputeShader gaussianBlur;
        private ComputeShader voxelizeScene;

        private Shader cameraVoxelAlbedoShader => Shader.Find("Hidden/VoxelBufferAlbedo"); 
        private Shader cameraVoxelNormalShader => Shader.Find("Hidden/VoxelBufferNormal");
        private Shader cameraVoxelEmissiveShader => Shader.Find("Hidden/VoxelBufferEmissive");
        private Shader cameraVoxelHiddenShader => Shader.Find("Hidden/VoxelBufferHidden");

        private ComputeBuffer directionalLightsBuffer = null;
        private ComputeBuffer pointLightsBuffer = null;
        private ComputeBuffer spotLightsBuffer = null;
        private ComputeBuffer areaLightsBuffer = null;

        private static TextureFormat textureformat = TextureFormat.RGBAHalf;
        private static RenderTextureFormat rendertextureformat = RenderTextureFormat.ARGBHalf;

        /// <summary>
        /// Load in necessary resources for the voxel tracer.
        /// </summary>
        private void GetResources()
        {
            if (slicer == null) 
                slicer = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/VolumeSlicer.compute");

            if (voxelDirectSurfaceLight == null)
                voxelDirectSurfaceLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/VoxelDirectSurfaceLight.compute");

            if (voxelDirectVolumetricLight == null)
                voxelDirectVolumetricLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/VoxelDirectVolumetricLight.compute");

            if (voxelBounceSurfaceLight == null)
                voxelBounceSurfaceLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/VoxelBounceSurfaceLight.compute");

            if (voxelBounceVolumetricLight == null)
                voxelBounceVolumetricLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/VoxelBounceVolumetricLight.compute");

            if (voxelEnvironmentSurfaceLight == null)
                voxelEnvironmentSurfaceLight = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/VoxelEnvironmentSurfaceLight.compute");

            if (gaussianBlur == null) 
                gaussianBlur = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/GaussianBlur3D.compute");
            
            if (addBuffers == null) 
                addBuffers = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/AddBuffers.compute");
            
            if (averageBuffers == null) 
                averageBuffers = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/AverageBuffers.compute");
            
            if (multiplyBuffers == null) 
                multiplyBuffers = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/MultiplyBuffers.compute");

            if (voxelizeScene == null) 
                voxelizeScene = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetComputeFolder + "/VoxelizeScene.compute");
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
            voxelEnvironmentLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelEnvironmentLightSurfaceBufferAssetPath);
            voxelBounceLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelBounceLightSurfaceBufferAssetPath);
            voxelDirectLightVolumeBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelDirectLightVolumeBufferAssetPath);
            voxelBounceLightVolumeBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelBounceLightVolumeBufferAssetPath);

            if (customEnvironmentMap == null)
                environmentMap = AssetDatabase.LoadAssetAtPath<Cubemap>(environmentMapAssetPath);
            else
                environmentMap = customEnvironmentMap;
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
                directionalLightsBuffer = new ComputeBuffer(voxelLightDirectionals.Count, VoxelLightDirectional.GetByteSize() * voxelLightDirectionals.Count);
                directionalLightsBuffer.SetData(voxelLightDirectionals.ToArray());
            }

            //build point light buffer
            if (voxelLightPoints.Count > 0)
            {
                pointLightsBuffer = new ComputeBuffer(voxelLightPoints.Count, VoxelLightPoint.GetByteSize() * voxelLightPoints.Count);
                pointLightsBuffer.SetData(voxelLightPoints.ToArray());
            }

            //build spot light buffer
            if (voxelLightSpots.Count > 0)
            {
                spotLightsBuffer = new ComputeBuffer(voxelLightSpots.Count, VoxelLightSpot.GetByteSize() * voxelLightSpots.Count);
                spotLightsBuffer.SetData(voxelLightSpots.ToArray());
            }

            //build area light buffer
            if (voxelLightAreas.Count > 0)
            {
                areaLightsBuffer = new ComputeBuffer(voxelLightAreas.Count, VoxelLightArea.GetByteSize() * voxelLightAreas.Count);
                areaLightsBuffer.SetData(voxelLightAreas.ToArray());
            }
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

        [ContextMenu("Step 1: Generate Albedo | Normal | Emissive Buffers")]
        public void GenerateVolumes()
        {
            float timeBeforeFunction = Time.realtimeSinceStartup;

            //NOTE TO SELF: Keep render texture format high precision.
            //For instance changing it to an 8 bit for the albedo buffer seems to kills color definition.

            //Old voxelization code, it works but it's rather slow.
            //GenerateVolumeOLD(cameraVoxelAlbedoShader, string.Format("{0}_albedo", voxelName), rendertextureformat, TextureFormat.RGBA32);
            //GenerateVolumeOLD(cameraVoxelNormalShader, string.Format("{0}_normal", voxelName), rendertextureformat, textureformat);
            //GenerateVolumeOLD(cameraVoxelEmissiveShader, string.Format("{0}_emissive", voxelName), rendertextureformat, textureformat);

            //New voxelization code, uses a compute shader to speed things up (about 3X faster).
            GenerateVolumeNew(cameraVoxelAlbedoShader, string.Format("{0}_albedo", voxelName), rendertextureformat, TextureFormat.RGBA32);
            GenerateVolumeNew(cameraVoxelNormalShader, string.Format("{0}_normal", voxelName), rendertextureformat, textureformat);
            GenerateVolumeNew(cameraVoxelEmissiveShader, string.Format("{0}_emissive", voxelName), rendertextureformat, textureformat);

            Debug.Log(string.Format("Generating Albedo / Normal / Emissive buffers took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
        }

        /// <summary>
        /// Generates a 3D texture of the scene, renders it with the given "replacementShader", and saves the asset into "filename".
        /// </summary>
        /// <param name="replacementShader"></param>
        /// <param name="filename"></param>
        /// <param name="rtFormat"></param>
        /// <param name="texFormat"></param>
        public void GenerateVolumeNew(Shader replacementShader, string filename, RenderTextureFormat rtFormat, TextureFormat savedTextureFormat)
        {
            UpdateProgressBar(string.Format("Generating {0}", filename), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.
            CreateVoxelCamera(); //Create our voxel camera rig

            //string renderTypeKey = "Geometry"; 
            //string renderTypeKey = "Opaque";
            //string renderTypeKey = "AlphaTest";
            //string renderTypeKey = "Transparent";
            int renderTextureDepthBits = 32; //bits for the render texture used by the voxel camera (technically 16 does just fine?)

            //render with the given replacement shader (which should represent one of our buffers).
            //voxelCamera.SetReplacementShader(cameraVoxelHiddenShader, "Transparent");
            voxelCamera.SetReplacementShader(replacementShader, "");
            //voxelCamera.SetReplacementShader(replacementShader, renderTypeKey);
            voxelCamera.allowMSAA = enableAnitAliasing;

            voxelCamera.Render();

            //compute per voxel position offset values.
            float xOffset = voxelSize.x / voxelResolution.x;
            float yOffset = voxelSize.y / voxelResolution.y;
            float zOffset = voxelSize.z / voxelResolution.z;

            //[REMOVE] Scuffed way of thickening geometry, doesn't really work as well as I'd hope and this should probably just be removed.
            Shader.SetGlobalFloat("_VertexExtrusion", Mathf.Max(zOffset, Mathf.Max(xOffset, yOffset)) * geometryThicknessModifier);

            //pre-fetch our voxelize kernel function in the compute shader.
            int ComputeShader_VoxelizeScene = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene");

            //make sure the voxelize shader knows our voxel resolution beforehand.
            voxelizeScene.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            //create our 3D render texture, which will be accumulating 2D slices of the scene captured at various axis.
            RenderTexture combinedSceneVoxel = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            combinedSceneVoxel.dimension = TextureDimension.Tex3D;
            combinedSceneVoxel.volumeDepth = voxelResolution.z;
            combinedSceneVoxel.enableRandomWrite = true;
            combinedSceneVoxel.Create();

            float timeBeforeRendering = Time.realtimeSinceStartup;

            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the X axis.

            //create a 2D render texture based off our voxel resolution to capture the scene in the X axis.
            voxelCameraSlice = new RenderTexture(voxelResolution.z, voxelResolution.y, renderTextureDepthBits, rtFormat);
            voxelCameraSlice.antiAliasing = enableAnitAliasing ? 8 : 1;
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCameraSlice.enableRandomWrite = true;
            voxelCamera.targetTexture = voxelCameraSlice;
            voxelCamera.orthographicSize = voxelSize.y * 0.5f;

            //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //orient the voxel camera to face the positive X axis.
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 90.0f, 0);

            //make sure the voxelize compute shader knows which axis we are going to be accumulating on.
            //this is important as each axis requires some specific swizzling so that it shows up correctly.
            SetComputeKeyword(voxelizeScene, "X_POS", true);
            SetComputeKeyword(voxelizeScene, "X_NEG", false);
            SetComputeKeyword(voxelizeScene, "Y_POS", false);
            SetComputeKeyword(voxelizeScene, "Y_NEG", false);
            SetComputeKeyword(voxelizeScene, "Z_POS", false);
            SetComputeKeyword(voxelizeScene, "Z_NEG", false);

            for (int i = 0; i < voxelResolution.x; i++)
            {
                //step through the scene on the X axis
                voxelCameraGameObject.transform.position = transform.position - new Vector3(voxelSize.x / 2.0f, 0, 0) + new Vector3(xOffset * i, 0, 0);
                voxelCamera.Render();

                //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                voxelizeScene.SetInt("AxisIndex", i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //||||||||||||||||||||||||||||||||| X NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //orient the voxel camera to face the negative X axis.
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, -90.0f, 0);

            //make sure the voxelize compute shader knows which axis we are going to be accumulating on.
            //this is important as each axis requires some specific swizzling so that it shows up correctly.
            SetComputeKeyword(voxelizeScene, "X_POS", false);
            SetComputeKeyword(voxelizeScene, "X_NEG", true);
            SetComputeKeyword(voxelizeScene, "Y_POS", false);
            SetComputeKeyword(voxelizeScene, "Y_NEG", false);
            SetComputeKeyword(voxelizeScene, "Z_POS", false);
            SetComputeKeyword(voxelizeScene, "Z_NEG", false);

            for (int i = 0; i < voxelResolution.x; i++)
            {
                //step through the scene on the X axis
                voxelCameraGameObject.transform.position = transform.position + new Vector3(voxelSize.x / 2.0f, 0, 0) - new Vector3(xOffset * i, 0, 0);
                voxelCamera.Render();

                //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                voxelizeScene.SetInt("AxisIndex", i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
            voxelCameraSlice.Release();

            //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the Y axis.

            //create a 2D render texture based off our voxel resolution to capture the scene in the Y axis.
            voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.z, renderTextureDepthBits, rtFormat);
            voxelCameraSlice.antiAliasing = enableAnitAliasing ? 8 : 1;
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCameraSlice.enableRandomWrite = true;
            voxelCamera.targetTexture = voxelCameraSlice;
            voxelCamera.orthographicSize = voxelSize.z * 0.5f;

            //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //orient the voxel camera to face the positive Y axis.
            voxelCameraGameObject.transform.eulerAngles = new Vector3(-90.0f, 0, 0);

            //make sure the voxelize compute shader knows which axis we are going to be accumulating on.
            //this is important as each axis requires some specific swizzling so that it shows up correctly.
            SetComputeKeyword(voxelizeScene, "X_POS", false);
            SetComputeKeyword(voxelizeScene, "X_NEG", false);
            SetComputeKeyword(voxelizeScene, "Y_POS", true);
            SetComputeKeyword(voxelizeScene, "Y_NEG", false);
            SetComputeKeyword(voxelizeScene, "Z_POS", false);
            SetComputeKeyword(voxelizeScene, "Z_NEG", false);

            for (int i = 0; i < voxelResolution.y; i++)
            {
                //step through the scene on the Y axis
                voxelCameraGameObject.transform.position = transform.position - new Vector3(0, voxelSize.y / 2.0f, 0) + new Vector3(0, yOffset * i, 0);
                voxelCamera.Render();

                //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                voxelizeScene.SetInt("AxisIndex", i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //||||||||||||||||||||||||||||||||| Y NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //orient the voxel camera to face the negative Y axis.
            voxelCameraGameObject.transform.eulerAngles = new Vector3(90.0f, 0, 0);

            //make sure the voxelize compute shader knows which axis we are going to be accumulating on.
            //this is important as each axis requires some specific swizzling so that it shows up correctly.
            SetComputeKeyword(voxelizeScene, "X_POS", false);
            SetComputeKeyword(voxelizeScene, "X_NEG", false);
            SetComputeKeyword(voxelizeScene, "Y_POS", false);
            SetComputeKeyword(voxelizeScene, "Y_NEG", true);
            SetComputeKeyword(voxelizeScene, "Z_POS", false);
            SetComputeKeyword(voxelizeScene, "Z_NEG", false);

            for (int i = 0; i < voxelResolution.y; i++)
            {
                //step through the scene on the Y axis
                voxelCameraGameObject.transform.position = transform.position + new Vector3(0, voxelSize.y / 2.0f, 0) - new Vector3(0, yOffset * i, 0);
                voxelCamera.Render();

                //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                voxelizeScene.SetInt("AxisIndex", i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
            voxelCameraSlice.Release();

            //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the Z axis.

            //create a 2D render texture based off our voxel resolution to capture the scene in the Z axis.
            voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.y, renderTextureDepthBits, rtFormat);
            voxelCameraSlice.antiAliasing = enableAnitAliasing ? 8 : 1;
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCameraSlice.enableRandomWrite = true;
            voxelCamera.targetTexture = voxelCameraSlice;
            voxelCamera.orthographicSize = voxelSize.y * 0.5f;

            //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z POSITIVE AXIS |||||||||||||||||||||||||||||||||
            //orient the voxel camera to face the positive Z axis.
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 0, 0);

            //make sure the voxelize compute shader knows which axis we are going to be accumulating on.
            //this is important as each axis requires some specific swizzling so that it shows up correctly.
            SetComputeKeyword(voxelizeScene, "X_POS", false);
            SetComputeKeyword(voxelizeScene, "X_NEG", false);
            SetComputeKeyword(voxelizeScene, "Y_POS", false);
            SetComputeKeyword(voxelizeScene, "Y_NEG", false);
            SetComputeKeyword(voxelizeScene, "Z_POS", true);
            SetComputeKeyword(voxelizeScene, "Z_NEG", false);

            for (int i = 0; i < voxelResolution.z; i++)
            {
                //step through the scene on the Z axis
                voxelCameraGameObject.transform.position = transform.position - new Vector3(0, 0, voxelSize.z / 2.0f) + new Vector3(0, 0, zOffset * i);
                voxelCamera.Render();

                //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                voxelizeScene.SetInt("AxisIndex", i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //||||||||||||||||||||||||||||||||| Z NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z NEGATIVE AXIS |||||||||||||||||||||||||||||||||
            //orient the voxel camera to face the negative Z axis.
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 180.0f, 0);

            //make sure the voxelize compute shader knows which axis we are going to be accumulating on.
            //this is important as each axis requires some specific swizzling so that it shows up correctly.
            SetComputeKeyword(voxelizeScene, "X_POS", false);
            SetComputeKeyword(voxelizeScene, "X_NEG", false);
            SetComputeKeyword(voxelizeScene, "Y_POS", false);
            SetComputeKeyword(voxelizeScene, "Y_NEG", false);
            SetComputeKeyword(voxelizeScene, "Z_POS", false);
            SetComputeKeyword(voxelizeScene, "Z_NEG", true);

            for (int i = 0; i < voxelResolution.z; i++)
            {
                //step through the scene on the Z axis
                voxelCameraGameObject.transform.position = transform.position + new Vector3(0, 0, voxelSize.z / 2.0f) - new Vector3(0, 0, zOffset * i);
                voxelCamera.Render();

                //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                voxelizeScene.SetInt("AxisIndex", i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //release the render texture slice, because we are done with it...
            voxelCameraSlice.Release();
            Debug.Log(string.Format("{0} rendering took {1} seconds.", filename, Time.realtimeSinceStartup - timeBeforeRendering));

            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //final step, save our accumulated 3D texture to the disk.

            //create our object to handle the conversion of the 3D render texture.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, savedTextureFormat);
            Texture3D result = renderTextureConverter.ConvertFromRenderTexture3D(combinedSceneVoxel, true);
            result.filterMode = FilterMode.Point;

            //release the scene voxel render texture, because we are done with it...
            combinedSceneVoxel.Release();

            Debug.Log(string.Format("Generating {0} took {1} seconds.", filename, Time.realtimeSinceStartup - timeBeforeFunction));

            //save the final texture to the disk in our local assets folder under the current scene.
            SaveVolumeTexture(filename, result);

            //get rid of this junk, don't need it no more.
            CleanupVoxelCamera();

            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 1.5: CAPTURE ENVIRONMENT ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 1.5: CAPTURE ENVIRONMENT ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 1.5: CAPTURE ENVIRONMENT ||||||||||||||||||||||||||||||||||||||||||

        [ContextMenu("Step 1.5: Capture Environment")]
        public void CaptureEnvironment()
        {
            if (customEnvironmentMap != null)
                return;

            GameObject environmentCameraGameObject = new GameObject("EnvironmentProbe");
            ReflectionProbe environmentProbe = environmentCameraGameObject.AddComponent<ReflectionProbe>();

            environmentProbe.cullingMask = 0;
            environmentProbe.hdr = true;
            environmentProbe.resolution = environmentResolution;

            Lightmapping.BakeReflectionProbe(environmentProbe, environmentMapAssetPath);

            environmentMap = AssetDatabase.LoadAssetAtPath<Cubemap>(environmentMapAssetPath);

            DestroyImmediate(environmentProbe);
            DestroyImmediate(environmentCameraGameObject);
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform direct SURFACE lighting on the voxelized scene.
        //
        //This is the among the lightest compute shader functions we have...
        //However it can get intensive with a large amount of lights, or with a dense enough voxel resolution.
        //This should be optimized later with random importance sampling of lights.
        //
        //But... for the time being compared to the bounce functions later this is relatively light and doesn't cause GPU driver timeouts.

        [ContextMenu("Step 2: Trace Direct Surface Lighting")]
        public void TraceDirectSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Tracing Direct Surface Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            GetGeneratedContent(); //Load up all of our generated content so we can use it.
            BuildLightComputeBuffers(); //Get all unity scene lights ready to use in the compute shader.

            //fetch our main direct surface light function kernel in the compute shader
            int ComputeShader_TraceSurfaceDirectLight = voxelDirectSurfaceLight.FindKernel("ComputeShader_TraceSurfaceDirectLight");

            //make sure the compute shader knows the following parameters.
            voxelDirectSurfaceLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelDirectSurfaceLight.SetVector("VolumePosition", transform.position);
            voxelDirectSurfaceLight.SetVector("VolumeSize", voxelSize);

            //make sure the compute shader knows what sets of lights we have.
            SetComputeKeyword(voxelDirectSurfaceLight, "DIRECTIONAL_LIGHTS", directionalLightsBuffer != null);
            SetComputeKeyword(voxelDirectSurfaceLight, "POINT_LIGHTS", pointLightsBuffer != null);
            SetComputeKeyword(voxelDirectSurfaceLight, "SPOT_LIGHTS", spotLightsBuffer != null);
            SetComputeKeyword(voxelDirectSurfaceLight, "AREA_LIGHTS", areaLightsBuffer != null);

            //feed the compute shader the constructed compute buffers of the unity lights we gathered if they exist.
            if (directionalLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "DirectionalLights", directionalLightsBuffer);
            if (pointLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "PointLights", pointLightsBuffer);
            if (spotLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "SpotLights", spotLightsBuffer);
            if (areaLightsBuffer != null) voxelDirectSurfaceLight.SetBuffer(ComputeShader_TraceSurfaceDirectLight, "AreaLights", areaLightsBuffer);

            //consruct our render texture that we will write into
            RenderTexture directSurfaceTrace = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            directSurfaceTrace.dimension = TextureDimension.Tex3D;
            directSurfaceTrace.volumeDepth = voxelResolution.z;
            directSurfaceTrace.enableRandomWrite = true;
            directSurfaceTrace.Create();

            //feed our compute shader the appropriate buffers so we can use them.
            voxelDirectSurfaceLight.SetTexture(ComputeShader_TraceSurfaceDirectLight, "SceneAlbedo", voxelAlbedoBuffer); //most important one, contains scene color and "occlusion".
            //voxelDirectSurfaceLight.SetTexture(ComputeShader_TraceSurfaceDirectLight, "SceneNormal", voxelNormalBuffer); //this actually isn't needed and used at the moment.
            voxelDirectSurfaceLight.SetTexture(ComputeShader_TraceSurfaceDirectLight, "SceneEmissive", voxelEmissiveBuffer); //this just gets added at the end, but it's important.
            voxelDirectSurfaceLight.SetTexture(ComputeShader_TraceSurfaceDirectLight, "Write", directSurfaceTrace);

            //let the GPU compute direct surface lighting, and hope it can manage it :D
            voxelDirectSurfaceLight.Dispatch(ComputeShader_TraceSurfaceDirectLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //after surviving the onslaught of computations, we will save our results to the disk.

            //create this so we can convert our 3D render texture to a Texture3D and save it to the disk.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //save it!
            renderTextureConverter.Save3D(directSurfaceTrace, voxelDirectLightSurfaceBufferAssetPath, textureObjectSettings);

            //we are done with this, so clean up.
            directSurfaceTrace.Release();

            //we are done with the compute buffers of the unity lights, so clean them up.
            if (directionalLightsBuffer != null) directionalLightsBuffer.Release();
            if (pointLightsBuffer != null) pointLightsBuffer.Release();
            if (spotLightsBuffer != null) spotLightsBuffer.Release();
            if (areaLightsBuffer != null) areaLightsBuffer.Release();

            Debug.Log(string.Format("'TraceDirectSurfaceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE DIRECT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE DIRECT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE DIRECT VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform direct VOLUMETRIC lighting on the voxelized scene.
        //
        //This is definetly slightly more expensive than the surface tracing version.
        //It can definetly get intensive with a large amount of lights, or with a dense enough voxel resolution.
        //This should be optimized later with random importance sampling of lights just like the one before.
        //
        //But... for the time being compared to the bounce functions later this is relatively light and doesn't cause GPU driver timeouts. 

        [ContextMenu("Step 3: Trace Direct Volume Lighting")]
        public void TraceDirectVolumeLighting()
        {
            UpdateProgressBar(string.Format("Tracing Direct Volume Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            GetGeneratedContent(); //Load up all of our generated content so we can use it.
            BuildLightComputeBuffers(); //Get all unity scene lights ready to use in the compute shader.

            //create this early so we utilize it's functions to convert our 3D render texture to a Texture3D.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings resultTextureSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //fetch our main direct volumetric light function kernel in the compute shader
            int ComputeShader_TraceVolumeDirectLight = voxelDirectVolumetricLight.FindKernel("ComputeShader_TraceVolumeDirectLight");

            //make sure the compute shader knows the following parameters.
            voxelDirectVolumetricLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelDirectVolumetricLight.SetVector("VolumePosition", transform.position);
            voxelDirectVolumetricLight.SetVector("VolumeSize", voxelSize);

            //make sure the compute shader knows what sets of lights we have.
            SetComputeKeyword(voxelDirectVolumetricLight, "DIRECTIONAL_LIGHTS", directionalLightsBuffer != null);
            SetComputeKeyword(voxelDirectVolumetricLight, "POINT_LIGHTS", pointLightsBuffer != null);
            SetComputeKeyword(voxelDirectVolumetricLight, "SPOT_LIGHTS", spotLightsBuffer != null);
            SetComputeKeyword(voxelDirectVolumetricLight, "AREA_LIGHTS", areaLightsBuffer != null);

            //feed the compute shader the constructed compute buffers of the unity lights we gathered if they exist.
            if (directionalLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "DirectionalLights", directionalLightsBuffer);
            if (pointLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "PointLights", pointLightsBuffer);
            if (spotLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "SpotLights", spotLightsBuffer);
            if (areaLightsBuffer != null) voxelDirectVolumetricLight.SetBuffer(ComputeShader_TraceVolumeDirectLight, "AreaLights", areaLightsBuffer);

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //feed our compute shader the appropriate buffers so we can use them.
            voxelDirectVolumetricLight.SetTexture(ComputeShader_TraceVolumeDirectLight, "SceneAlbedo", voxelAlbedoBuffer); //most important one, contains scene color and "occlusion".
            //voxelDirectVolumetricLight.SetTexture(ComputeShader_TraceVolumeDirectLight, "SceneNormal", voxelNormalBuffer); //this actually isn't needed and used at the moment.
            //voxelDirectVolumetricLight.SetTexture(ComputeShader_TraceVolumeDirectLight, "SceneEmissive", voxelEmissiveBuffer); //this also isn't used.
            voxelDirectVolumetricLight.SetTexture(ComputeShader_TraceVolumeDirectLight, "Write", volumeWrite);

            //let the GPU compute direct volumetric lighting, and hope it can manage it :D
            voxelDirectVolumetricLight.Dispatch(ComputeShader_TraceVolumeDirectLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

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

                //convert the raw volumetric bounce light render texture into a texture3D so that it can be read.
                Texture3D tempRawVolumetricBounceLight = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

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
                Texture3D tempBlurX = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

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
                Texture3D tempBlurY = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

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

            //save it!
            renderTextureConverter.Save3D(volumeWrite, voxelDirectLightVolumeBufferAssetPath, resultTextureSettings);

            //we are done with this, so clean up.
            volumeWrite.Release();

            //we are done with the compute buffers of the unity lights, so clean them up.
            if (directionalLightsBuffer != null) directionalLightsBuffer.Release();
            if (pointLightsBuffer != null) pointLightsBuffer.Release();
            if (spotLightsBuffer != null) spotLightsBuffer.Release();
            if (areaLightsBuffer != null) areaLightsBuffer.Release();

            Debug.Log(string.Format("'TraceDirectVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 4: TRACE BOUNCE SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 4: TRACE BOUNCE SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 4: TRACE BOUNCE SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform bounced SURFACE lighting on the voxelized scene.
        //
        //This is the second most intensive operation we do.
        //Luckily it doesn't scale with the amount of lights we have, but it does obviously scale with voxel resolution and the amount of samples we do.

        [ContextMenu("Step 4: Trace Bounce Surface Lighting")]
        public void TraceBounceSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Bouncing Surface Light... [BOUNCES: {0}] [SAMPLES: {1}]", bounces, bounceSurfaceSamples), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //create this early so we utilize it's functions to convert our 3D render texture to a Texture3D.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //the current bounce that we are on, we will start off with bouncing from the direct surface lighting.
            Texture3D bounceTemp = voxelDirectLightSurfaceBuffer;

            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            if(enableEnvironmentLighting)
            {
                //fetch our function kernel in the compute shader
                int ComputeShader_AddBuffers = addBuffers.FindKernel("ComputeShader_AddBuffers");

                //make sure the compute shader knows our voxel resolution beforehand.
                addBuffers.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

                //feed the compute shader the textures that will be added together
                addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferA", bounceTemp);
                addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferB", voxelEnvironmentLightSurfaceBuffer);
                addBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeWrite);

                //let the GPU add the textures together.
                addBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                bounceTemp = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);
            }

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            //fetch our main bounce surface light function kernel in the compute shader
            int ComputeShader_TraceSurfaceBounceLight = voxelBounceSurfaceLight.FindKernel("ComputeShader_TraceSurfaceBounceLight");

            //make sure the compute shader knows the following parameters.
            voxelBounceSurfaceLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelBounceSurfaceLight.SetVector("VolumePosition", transform.position);
            voxelBounceSurfaceLight.SetVector("VolumeSize", voxelSize);
            voxelBounceSurfaceLight.SetInt("MaxBounceSamples", bounceSurfaceSamples);

            //if enabled, use a normal oriented cosine hemisphere for better ray allocation/quality (though it has some issues/quirks)
            SetComputeKeyword(voxelBounceSurfaceLight, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

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

                    //let the GPU compute bounced surface lighting, and hope it can manage it :(
                    voxelBounceSurfaceLight.Dispatch(ComputeShader_TraceSurfaceBounceLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                    //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                    if (i % GPU_Readback_Limit == 0)
                    {
                        AsyncGPUReadback.Request(volumeWrite, 0);
                        AsyncGPUReadback.WaitAllRequests();
                    }

                    if(showDetailedProgress)
                        UpdateProgressBar(string.Format("Bouncing Surface Light... [BOUNCES: {0} / {1}] [SAMPLES: {2} / {3}]", i + 1, bounces, j + 1, bounceSurfaceSamples), 0.5f);
                }

                //if we are doing more than 1 bounce
                if (i > 0)
                {
                    //convert our finished bounced lighting into a Texture3D so we can reuse it again for the next bounce
                    //TODO: Issue another compute shader here to start potentially averaging the results of each bounce.
                    //The problem with the current solution is that too many bounces makes things too bright and there is no energy loss which is not correct.
                    bounceTemp = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);
                }
            }

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //if we have survived the onslaught of computations... FANTASTIC! lets save our results to the disk before we lose it.

            if (showDetailedProgress)
                UpdateProgressBar(string.Format("Saving to disk..."), 0.5f);

            //SAVE IT!
            renderTextureConverter.Save3D(volumeWrite, voxelBounceLightSurfaceBufferAssetPath, textureObjectSettings);

            //we are done with this, so clean up.
            volumeWrite.Release();

            Debug.Log(string.Format("'TraceBounceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform bounced VOLUMETRIC lighting on the voxelized scene.
        //
        //This is by far the most intensive operation we do.
        //Luckily it doesn't scale with the amount of lights we have, but it does obviously scale with voxel resolution and the amount of samples we do.

        [ContextMenu("Step 5: Trace Bounce Volume Lighting")]
        public void TraceBounceVolumeLighting()
        {
            UpdateProgressBar(string.Format("Bouncing Volume Light... [SAMPLES: {0}]", bounceVolumetricSamples), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //create this early so we utilize it's functions to convert our 3D render texture to a Texture3D.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //the current bounce that we are on, we will start off with bouncing from the direct surface lighting.
            Texture3D bounceTemp = voxelDirectLightSurfaceBuffer;

            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADDING FIRST BOUNCE ENVIRONMENT LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            if (enableEnvironmentLighting)
            {
                //fetch our function kernel in the compute shader
                int ComputeShader_AddBuffers = addBuffers.FindKernel("ComputeShader_AddBuffers");

                //make sure the compute shader knows our voxel resolution beforehand.
                addBuffers.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

                //feed the compute shader the textures that will be added together
                addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferA", bounceTemp);
                addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferB", voxelEnvironmentLightSurfaceBuffer);
                addBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeWrite);

                //let the GPU add the textures together.
                addBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                bounceTemp = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);
            }

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTING BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

            //fetch our main bounce volumetric light function kernel in the compute shader
            int ComputeShader_TraceVolumeBounceLight = voxelBounceVolumetricLight.FindKernel("ComputeShader_TraceVolumeBounceLight");

            //make sure the compute shader knows the following parameters.
            voxelBounceVolumetricLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelBounceVolumetricLight.SetVector("VolumePosition", transform.position);
            voxelBounceVolumetricLight.SetVector("VolumeSize", voxelSize);
            voxelBounceVolumetricLight.SetInt("MaxBounceSamples", bounceVolumetricSamples);

            for (int i = 0; i < bounceVolumetricSamples; i++)
            {
                //randomize the seed for noise sampling (THIS IS IMPORTANT)
                voxelBounceVolumetricLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                //feed our compute shader the appropriate buffers so we can use them.
                voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "SceneAlbedo", voxelAlbedoBuffer); //important, used for "occlusion" checking.
                //voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "SceneNormal", voxelNormalBuffer); //this isn't used at all.
                voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "DirectLightSurface", bounceTemp); //important, the main color that we will be bouncing around.
                voxelBounceVolumetricLight.SetTexture(ComputeShader_TraceVolumeBounceLight, "Write", volumeWrite);

                voxelBounceVolumetricLight.Dispatch(ComputeShader_TraceVolumeBounceLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                if (i % GPU_Readback_Limit == 0)
                {
                    AsyncGPUReadback.Request(volumeWrite, 0);
                    AsyncGPUReadback.WaitAllRequests();
                }

                if (showDetailedProgress)
                    UpdateProgressBar(string.Format("Bouncing Volume Light... [SAMPLES: {0} / {1}]", i + 1, bounceVolumetricSamples), 0.5f);
            }

            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| POST 3D GAUSSIAN BLUR ||||||||||||||||||||||||||||||||||||||||||
            //In an attempt to squeeze more out of less...
            //We will now perform a 3D gaussian blur to smooth out the results from the bounced volumetric light.
            //(IF ITS ENABLED)

            if (volumetricBounceGaussianSamples > 0)
            {
                if (showDetailedProgress)
                    UpdateProgressBar(string.Format("Performing Gaussian Blur..."), 0.5f);

                //fetch our main gaussian blur function kernel in the compute shader
                int ComputeShader_GaussianBlur = gaussianBlur.FindKernel("ComputeShader_GaussianBlur");

                //convert the raw volumetric bounce light render texture into a texture3D so that it can be read.
                Texture3D tempRawVolumetricBounceLight = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

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
                Texture3D tempBlurX = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

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
                Texture3D tempBlurY = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

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

            if (showDetailedProgress)
                UpdateProgressBar(string.Format("Saving to disk..."), 0.5f);

            //SAVE IT!
            renderTextureConverter.Save3D(volumeWrite, voxelBounceLightVolumeBufferAssetPath, textureObjectSettings);

            //we are done with this, so clean up.
            volumeWrite.Release();

            Debug.Log(string.Format("'TraceBounceVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 5.5: TRACE ENVIRONMENT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5.5: TRACE ENVIRONMENT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5.5: TRACE ENVIRONMENT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we perform environment SURFACE lighting on the voxelized scene.

        [ContextMenu("Step 5.5: Trace Environment Surface Lighting")]
        public void TraceEnvironmentSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Environment Surface Light... [SAMPLES: {0}]", environmentSurfaceSamples), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //create this early so we utilize it's functions to convert our 3D render texture to a Texture3D.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //fetch our main bounce surface light function kernel in the compute shader
            int ComputeShader_TraceSurfaceEnvironmentLight = voxelEnvironmentSurfaceLight.FindKernel("ComputeShader_TraceSurfaceEnvironmentLight");

            //make sure the compute shader knows the following parameters.
            voxelEnvironmentSurfaceLight.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelEnvironmentSurfaceLight.SetVector("VolumePosition", transform.position);
            voxelEnvironmentSurfaceLight.SetVector("VolumeSize", voxelSize);
            voxelEnvironmentSurfaceLight.SetInt("MaxEnvironmentSamples", environmentSurfaceSamples);

            //if enabled, use a normal oriented cosine hemisphere for better ray allocation/quality (though it has some issues/quirks)
            SetComputeKeyword(voxelEnvironmentSurfaceLight, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            for (int i = 0; i < environmentSurfaceSamples; i++)
            {
                //randomize the seed for noise sampling (THIS IS IMPORTANT)
                voxelEnvironmentSurfaceLight.SetFloat("RandomSeed", Random.value * 100000.0f);

                //feed our compute shader the appropriate buffers so we can use them.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "SceneAlbedo", voxelAlbedoBuffer); //important, used for "occlusion" checking.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "SceneNormal", voxelNormalBuffer); //important, used to help orient hemispheres when enabled.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "EnvironmentMap", environmentMap); //important, the main color that we will be bouncing around.
                voxelEnvironmentSurfaceLight.SetTexture(ComputeShader_TraceSurfaceEnvironmentLight, "Write", volumeWrite);

                //let the GPU compute bounced surface lighting, and hope it can manage it :(
                voxelEnvironmentSurfaceLight.Dispatch(ComputeShader_TraceSurfaceEnvironmentLight, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //Perform a deliberate stall on the CPU so we can make sure that we don't issue too many dispatches to the GPU and overburden it.
                if (i % GPU_Readback_Limit == 0)
                {
                    AsyncGPUReadback.Request(volumeWrite, 0);
                    AsyncGPUReadback.WaitAllRequests();
                }

                if (showDetailedProgress)
                    UpdateProgressBar(string.Format("Environment Surface Light... [SAMPLES: {0} / {1}]", i + 1, environmentSurfaceSamples), 0.5f);
            }

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //if we have survived the onslaught of computations... FANTASTIC! lets save our results to the disk before we lose it.

            if (showDetailedProgress)
                UpdateProgressBar(string.Format("Saving to disk..."), 0.5f);

            //SAVE IT!
            renderTextureConverter.Save3D(volumeWrite, voxelEnvironmentLightSurfaceBufferAssetPath, textureObjectSettings);

            //we are done with this, so clean up.
            volumeWrite.Release();

            Debug.Log(string.Format("'TraceBounceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we simply combine the generated surface light buffers into one single texture.
        //This is a light operation, so no worries here.

        [ContextMenu("Step 6: Combine Surface Direct and Bounce Light")]
        public void CombineSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Combining Surface Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //create this early so we can use it to do 3D render texture conversions.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| ADD SURFACE BOUNCE AND DIRECT LIGHT ||||||||||||||||||||||||||||||||||||||||||

            //fetch our function kernel in the compute shader
            int ComputeShader_AddBuffers = addBuffers.FindKernel("ComputeShader_AddBuffers");

            //make sure the compute shader knows our voxel resolution beforehand.
            addBuffers.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            //feed the compute shader the textures that will be added together
            addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferA", voxelDirectLightSurfaceBuffer);
            addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferB", voxelBounceLightSurfaceBuffer);
            //addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferB", multiplyResult);
            addBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeWrite);

            //let the GPU add the textures together.
            addBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //after those simple computations lets go ahead and save our results to the disk.

            //save it!
            renderTextureConverter.Save3D(volumeWrite, voxelCombinedSurfaceBufferAssetPath, textureObjectSettings);

            //we are done with this, so clean up.
            volumeWrite.Release();

            Debug.Log(string.Format("'CombineSurfaceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we simply combine the generated volumetric light buffers into one single texture.
        //This is a light operation, so no worries here.

        [ContextMenu("Step 7: Combine Volume Direct and Bounce Light")]
        public void CombineVolumeLighting()
        {
            UpdateProgressBar(string.Format("Combining Volume Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources(); //Get all of our compute shaders ready.
            GetGeneratedContent(); //Load up all of our generated content so we can use it.

            //fetch our function kernel in the compute shader
            int ComputeShader_AddBuffers = addBuffers.FindKernel("ComputeShader_AddBuffers");

            //make sure the compute shader knows our voxel resolution beforehand.
            addBuffers.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            //consruct our render texture that we will write into
            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            //feed the compute shader the textures that will be added together
            addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferA", voxelDirectLightVolumeBuffer);
            addBuffers.SetTexture(ComputeShader_AddBuffers, "AddBufferB", voxelBounceLightVolumeBuffer);
            addBuffers.SetTexture(ComputeShader_AddBuffers, "Write", volumeWrite);

            //let the GPU add the textures together.
            addBuffers.Dispatch(ComputeShader_AddBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //after those simple computations lets go ahead and save our results to the disk.

            //create this so we can convert our 3D render texture to a Texture3D and save it to the disk.
            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //save it!
            renderTextureConverter.Save3D(volumeWrite, voxelCombinedVolumetricBufferAssetPath, textureObjectSettings);

            //we are done with this, so clean up.
            volumeWrite.Release();

            Debug.Log(string.Format("'CombineVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| FINAL: COMPUTE VOXEL TRACING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| FINAL: COMPUTE VOXEL TRACING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| FINAL: COMPUTE VOXEL TRACING ||||||||||||||||||||||||||||||||||||||||||
        //This is where we combine all of the functions to voxel trace the given volume, with all of the steps.

        [ContextMenu("ComputeVoxelTracing")]
        public void ComputeVoxelTracing()
        {
            GenerateVolumes();
            CaptureEnvironment();
            TraceDirectSurfaceLighting();
            TraceDirectVolumeLighting();
            TraceBounceSurfaceLighting();
            TraceBounceVolumeLighting();
            CombineSurfaceLighting();
            CombineVolumeLighting();
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

        /*
        public void GenerateVolumeOLD(Shader replacementShader, string filename, RenderTextureFormat rtFormat, TextureFormat texFormat)
        {
            UpdateProgressBar(string.Format("Generating {0}", filename), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            SetupAssetFolders();
            CreateVoxelCamera();

            float xOffset = voxelSize.x / voxelResolution.x;
            float yOffset = voxelSize.y / voxelResolution.y;
            float zOffset = voxelSize.z / voxelResolution.z;

            string renderTypeKey = "";
            int rtDepth = 32;

            voxelCamera.SetReplacementShader(replacementShader, renderTypeKey);
            voxelCamera.allowMSAA = enableAnitAliasing;

            Shader.SetGlobalFloat("_VertexExtrusion", Mathf.Max(zOffset, Mathf.Max(xOffset, yOffset)) * geometryThicknessModifier);

            Texture2D[] slices_x_neg = new Texture2D[voxelResolution.x];
            Texture2D[] slices_x_pos = new Texture2D[voxelResolution.x];
            Texture2D[] slices_y_pos = new Texture2D[voxelResolution.y];
            Texture2D[] slices_y_neg = new Texture2D[voxelResolution.y];
            Texture2D[] slices_z_pos = new Texture2D[voxelResolution.z];
            Texture2D[] slices_z_neg = new Texture2D[voxelResolution.z];

            float timeBeforeRendering = Time.realtimeSinceStartup;

            //||||||||||||||||||||||||||||||||| X |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X |||||||||||||||||||||||||||||||||
            voxelCameraSlice = new RenderTexture(voxelResolution.z, voxelResolution.y, rtDepth, rtFormat);
            voxelCameraSlice.antiAliasing = enableAnitAliasing ? 8 : 1;
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCamera.targetTexture = voxelCameraSlice;
            voxelCamera.orthographicSize = voxelSize.y * 0.5f;

            //--------------------- X POSITIVE ---------------------
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 90.0f, 0);

            for (int i = 0; i < voxelResolution.x; i++)
            {
                voxelCameraGameObject.transform.position = transform.position - new Vector3(voxelSize.x / 2.0f, 0, 0) + new Vector3(xOffset * i, 0, 0);
                voxelCamera.Render();
                slices_x_neg[i] = RenderTextureConverter.ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
            }

            //--------------------- X NEGATIVE ---------------------
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, -90.0f, 0);

            for (int i = 0; i < voxelResolution.x; i++)
            {
                voxelCameraGameObject.transform.position = transform.position + new Vector3(voxelSize.x / 2.0f, 0, 0) - new Vector3(xOffset * i, 0, 0);
                voxelCamera.Render();
                slices_x_pos[i] = RenderTextureConverter.ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
            }

            voxelCameraSlice.Release();

            //||||||||||||||||||||||||||||||||| Y |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y |||||||||||||||||||||||||||||||||
            voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.z, rtDepth, rtFormat);
            voxelCameraSlice.antiAliasing = enableAnitAliasing ? 8 : 1;
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCamera.targetTexture = voxelCameraSlice;
            voxelCamera.orthographicSize = voxelSize.z * 0.5f;

            //--------------------- Y POSITIVE ---------------------
            voxelCameraGameObject.transform.eulerAngles = new Vector3(-90.0f, 0, 0);

            for (int i = 0; i < voxelResolution.y; i++)
            {
                voxelCameraGameObject.transform.position = transform.position - new Vector3(0, voxelSize.y / 2.0f, 0) + new Vector3(0, yOffset * i, 0);
                voxelCamera.Render();
                slices_y_pos[i] = RenderTextureConverter.ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
            }

            //--------------------- Y NEGATIVE ---------------------
            voxelCameraGameObject.transform.eulerAngles = new Vector3(90.0f, 0, 0);

            for (int i = 0; i < voxelResolution.y; i++)
            {
                voxelCameraGameObject.transform.position = transform.position + new Vector3(0, voxelSize.y / 2.0f, 0) - new Vector3(0, yOffset * i, 0);
                voxelCamera.Render();
                slices_y_neg[i] = RenderTextureConverter.ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
            }

            voxelCameraSlice.Release();

            //||||||||||||||||||||||||||||||||| Z |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z |||||||||||||||||||||||||||||||||
            voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.y, rtDepth, rtFormat);
            voxelCameraSlice.antiAliasing = enableAnitAliasing ? 8 : 1;
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCamera.targetTexture = voxelCameraSlice;
            voxelCamera.orthographicSize = voxelSize.y * 0.5f;

            //--------------------- Z POSITIVE ---------------------
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 0, 0);

            for (int i = 0; i < voxelResolution.z; i++)
            {
                voxelCameraGameObject.transform.position = transform.position - new Vector3(0, 0, voxelSize.z / 2.0f) + new Vector3(0, 0, zOffset * i);
                voxelCamera.Render();
                slices_z_pos[i] = RenderTextureConverter.ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
            }

            //--------------------- Z NEGATIVE ---------------------
            voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 180.0f, 0);

            for (int i = 0; i < voxelResolution.z; i++)
            {
                voxelCameraGameObject.transform.position = transform.position + new Vector3(0, 0, voxelSize.z / 2.0f) - new Vector3(0, 0, zOffset * i);
                voxelCamera.Render();
                slices_z_neg[i] = RenderTextureConverter.ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
            }

            voxelCameraSlice.Release();

            Debug.Log(string.Format("{0} rendering took {1} seconds.", filename, Time.realtimeSinceStartup - timeBeforeRendering));

            //--------------------- COMBINE RESULTS ---------------------
            float timeBeforeCombine = Time.realtimeSinceStartup;

            Texture3D result = new Texture3D(voxelResolution.x, voxelResolution.y, voxelResolution.z, texFormat, true);
            result.filterMode = FilterMode.Point;

            for (int x = 0; x < result.width; x++)
            {
                for (int y = 0; y < result.height; y++)
                {
                    for (int z = 0; z < result.depth; z++)
                    {
                        Color colorResult = new Color(0, 0, 0, 0);

                        Color color_x_pos = slices_x_pos[(result.width - 1) - x].GetPixel(z, y);
                        Color color_x_neg = slices_x_neg[x].GetPixel((result.depth - 1) - z, y);

                        Color color_y_pos = slices_y_pos[y].GetPixel(x, (result.depth - 1) - z);
                        Color color_y_neg = slices_y_neg[(result.height - 1) - y].GetPixel(x, z);

                        Color color_z_pos = slices_z_pos[z].GetPixel(x, y);
                        Color color_z_neg = slices_z_neg[(result.depth - 1) - z].GetPixel((result.width - 1) - x, y);

                        if (blendVoxelResult)
                        {
                            int alphaIndex = 0;

                            if (color_x_pos.a > 0.0f)
                            {
                                colorResult += color_x_pos;
                                alphaIndex++;
                            }

                            if (color_x_neg.a > 0.0f)
                            {
                                colorResult += color_x_neg;
                                alphaIndex++;
                            }

                            if (color_y_pos.a > 0.0f)
                            {
                                colorResult += color_y_pos;
                                alphaIndex++;
                            }

                            if (color_y_neg.a > 0.0f)
                            {
                                colorResult += color_y_neg;
                                alphaIndex++;
                            }

                            if (color_z_pos.a > 0.0f)
                            {
                                colorResult += color_z_pos;
                                alphaIndex++;
                            }

                            if (color_z_neg.a > 0.0f)
                            {
                                colorResult += color_z_neg;
                                alphaIndex++;
                            }

                            if (alphaIndex > 0)
                                colorResult = new Color(colorResult.r / alphaIndex, colorResult.g / alphaIndex, colorResult.b / alphaIndex, colorResult.a);
                        }
                        else
                        {
                            if (color_x_pos.a > 0.0f)
                                colorResult += color_x_pos;
                            else if (color_x_neg.a > 0.0f)
                                colorResult += color_x_neg;
                            else if (color_y_pos.a > 0.0f)
                                colorResult += color_y_pos;
                            else if (color_y_neg.a > 0.0f)
                                colorResult += color_y_neg;
                            else if (color_z_pos.a > 0.0f)
                                colorResult += color_z_pos;
                            else if (color_z_neg.a > 0.0f)
                                colorResult += color_z_neg;
                        }

                        result.SetPixel(x, y, z, colorResult);
                    }
                }
            }

            result.Apply();

            Debug.Log(string.Format("{0} combining took {1} seconds.", filename, Time.realtimeSinceStartup - timeBeforeCombine));
            Debug.Log(string.Format("Generating {0} took {1} seconds.", filename, Time.realtimeSinceStartup - timeBeforeFunction));

            //--------------------- FINAL ---------------------
            SaveVolumeTexture(filename, result);
            CleanupVoxelCamera();
            voxelCameraSlice.Release();

            CloseProgressBar();
        }
        */
    }
}