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

/*
 * NOTE 1: The Anti-Aliasing when used for the generation of the voxels does help maintain geometry at low resolutions, however there is a quirk because it also samples the background color.
 * This was noticable when rendering position/normals voxel buffers where when on some geo looked darker than usual because the BG color was black.
 * Not to sure how to solve this and just might deal with it?
 * 
 * NOTE TO SELF: Supersampling?
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

namespace SceneVoxelizer1
{
    public class SceneVoxelizerV1 : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        [Header("Voxelization Properties")]
        public string voxelName = "Voxel"; //Name of the asset
        public Vector3 voxelSize = new Vector3(10.0f, 10.0f, 10.0f); //Size of the volume
        public float voxelDensitySize = 1.0f; //Size of each voxel (Smaller = More Voxels, Larger = Less Voxels)

        [Header("Gizmos")]
        public bool previewBounds;

        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        private Vector3Int voxelResolution => new Vector3Int((int)(voxelSize.x / voxelDensitySize), (int)(voxelSize.y / voxelDensitySize), (int)(voxelSize.z / voxelDensitySize));

        //Size of the thread groups for compute shaders.
        //These values should match the #define ones in the compute shaders.
        private static int THREAD_GROUP_SIZE_X = 8;
        private static int THREAD_GROUP_SIZE_Y = 8;
        private static int THREAD_GROUP_SIZE_Z = 8;

        private static string localAssetFolder = "Assets/SceneVoxelizerV1";
        private static string localAssetComputeFolder = "Assets/SceneVoxelizerV1/ComputeShaders";
        private static string localAssetDataFolder = "Assets/SceneVoxelizerV1/Data";
        private string voxelizeSceneAssetPath => localAssetComputeFolder + "/VoxelizeScene.compute";
        private string voxelBufferFileName => string.Format("SceneVoxelizerV1_{0}", voxelName);
        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();
        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;

        private GameObject voxelCameraGameObject;
        private Camera voxelCamera;
        private ComputeShader voxelizeScene;

        private static RenderTextureFormat renderTextureFormat = RenderTextureFormat.ARGB32;

        private RenderTextureConverterV2 renderTextureConverter => new RenderTextureConverterV2();

        /// <summary>
        /// Load in necessary resources for the voxel tracer.
        /// </summary>
        private bool GetResources()
        {
            voxelizeScene = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelizeSceneAssetPath);

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

        /// <summary>
        /// Saves a given Texture3D asset to the local voxel asset directory under the current scene name.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="tex3D"></param>
        public void SaveVolumeTexture(string fileName, Texture3D tex3D) => AssetDatabase.CreateAsset(tex3D, localAssetSceneDataFolder + "/" + fileName + ".asset");

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

        [ContextMenu("GenerateVolume")]
        public void GenerateVolume()
        {
            UpdateProgressBar(string.Format("Generating {0}", voxelBufferFileName), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.
            CreateVoxelCamera(); //Create our voxel camera rig

            int renderTextureDepthBits = 32; //bits for the render texture used by the voxel camera (technically 16 does just fine?)

            //compute per voxel position offset values.
            float xOffset = voxelSize.x / voxelResolution.x;
            float yOffset = voxelSize.y / voxelResolution.y;
            float zOffset = voxelSize.z / voxelResolution.z;

            //pre-fetch our voxelize kernel function in the compute shader.
            int ComputeShader_VoxelizeScene = voxelizeScene.FindKernel("ComputeShader_VoxelizeScene");

            //make sure the voxelize shader knows our voxel resolution beforehand.
            voxelizeScene.SetVector(ShaderIDs.VolumeResolution, new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            //create our 3D render texture, which will be accumulating 2D slices of the scene captured at various axis.
            RenderTexture combinedSceneVoxel = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, renderTextureFormat);
            combinedSceneVoxel.dimension = TextureDimension.Tex3D;
            combinedSceneVoxel.wrapMode = TextureWrapMode.Clamp;
            combinedSceneVoxel.filterMode = FilterMode.Point;
            combinedSceneVoxel.volumeDepth = voxelResolution.z;
            combinedSceneVoxel.enableRandomWrite = true;
            combinedSceneVoxel.Create();

            float timeBeforeRendering = Time.realtimeSinceStartup;

            //||||||||||||||||||||||||||||||||| RESIDUAL INFO CLEANUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RESIDUAL INFO CLEANUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RESIDUAL INFO CLEANUP |||||||||||||||||||||||||||||||||
            //disable all keywords
            SetComputeKeyword(voxelizeScene, "X_POS", false);
            SetComputeKeyword(voxelizeScene, "X_NEG", false);
            SetComputeKeyword(voxelizeScene, "Y_POS", false);
            SetComputeKeyword(voxelizeScene, "Y_NEG", false);
            SetComputeKeyword(voxelizeScene, "Z_POS", false);
            SetComputeKeyword(voxelizeScene, "Z_NEG", false);

            //assign the 
            voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.Write, combinedSceneVoxel);

            //run the compute shader once, which will make all pixels float4(0, 0, 0, 0) to clean things up.
            voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the X axis.

            //create a 2D render texture based off our voxel resolution to capture the scene in the X axis.
            RenderTexture voxelCameraSlice = new RenderTexture(voxelResolution.z, voxelResolution.y, renderTextureDepthBits, renderTextureFormat);
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCameraSlice.wrapMode = TextureWrapMode.Clamp;
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
                voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.Write, combinedSceneVoxel);
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
                voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.Write, combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
            voxelCameraSlice.DiscardContents(true, true);
            voxelCameraSlice.Release();

            //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the Y axis.

            //create a 2D render texture based off our voxel resolution to capture the scene in the Y axis.
            voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.z, renderTextureDepthBits, renderTextureFormat);
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCameraSlice.wrapMode = TextureWrapMode.Clamp;
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
                voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.Write, combinedSceneVoxel);
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
                voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.Write, combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
            voxelCameraSlice.DiscardContents(true, true);
            voxelCameraSlice.Release();

            //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the Z axis.

            //create a 2D render texture based off our voxel resolution to capture the scene in the Z axis.
            voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.y, renderTextureDepthBits, renderTextureFormat);
            voxelCameraSlice.filterMode = FilterMode.Point;
            voxelCameraSlice.wrapMode = TextureWrapMode.Clamp;
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
                voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.Write, combinedSceneVoxel);
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
                voxelizeScene.SetInt(ShaderIDs.AxisIndex, i);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.CameraVoxelRender, voxelCameraSlice);
                voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, ShaderIDs.Write, combinedSceneVoxel);
                voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //release the render texture slice, because we are done with it...
            voxelCameraSlice.DiscardContents(true, true);
            voxelCameraSlice.Release();
            Debug.Log(string.Format("{0} rendering took {1} seconds.", voxelBufferFileName, Time.realtimeSinceStartup - timeBeforeRendering));

            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
            //final step, save our accumulated 3D texture to the disk.

            renderTextureConverter.SaveRenderTexture3DAsTexture3D(combinedSceneVoxel, localAssetSceneDataFolder + "/" + voxelBufferFileName + ".asset");

            Debug.Log(string.Format("Generating {0} took {1} seconds.", voxelBufferFileName, Time.realtimeSinceStartup - timeBeforeFunction));

            //get rid of this junk, don't need it no more.
            CleanupVoxelCamera();

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

        public static void SetComputeKeyword(ComputeShader computeShader, string keyword, bool value)
        {
            if (value)
                computeShader.EnableKeyword(keyword);
            else
                computeShader.DisableKeyword(keyword);
        }

        public void UpdateProgressBar(string description, float progress) => EditorUtility.DisplayProgressBar("SceneVoxelizerV1", description, progress);

        public void CloseProgressBar() => EditorUtility.ClearProgressBar();
    }
}