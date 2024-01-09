using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using TMPro.SpriteAssetUtilities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/*
 * SELF NOTE 1: The Anti-Aliasing when used for the generation of the voxels does help maintain geometry at low resolutions, however there is a quirk because it also samples the background color.
 * This was noticable when rendering position/normals voxel buffers where when on some geo looked darker than usual because the BG color was black.
 * Not to sure how to solve this and just might deal with it?
*/

namespace UnityVoxelTracer
{
    public class VoxelTracer : MonoBehaviour
    {
        private static int THREAD_GROUP_SIZE_X = 10;
        private static int THREAD_GROUP_SIZE_Y = 10;
        private static int THREAD_GROUP_SIZE_Z = 10;

        [Header("Voxelizer Main")]
        public string voxelName = "Voxel";
        public Vector3 voxelSize = new Vector3(10.0f, 10.0f, 10.0f);
        public float voxelDensitySize = 1.0f;
        public float geometryThicknessModifier = 0.0f;
        public bool enableAnitAliasing = false;
        public bool blendVoxelResult = false;

        [Header("Baking Options")]
        [Range(1, 8192)] public int bounceSurfaceSamples = 64;
        [Range(1, 8192)] public int bounceVolumetricSamples = 64;
        //[Range(1, 16)] public int sampleTiles = 4;
        [Range(1, 4)] public int bounces = 1;
        public bool normalOrientedHemisphereSampling = false;

        [Header("Volumetric Baking Options")]
        [Range(0, 64)] public int gaussianSamples = 0;

        [Header("Gizmos")]
        public bool previewBounds = true;

        private Texture3D voxelAlbedoBuffer;
        private Texture3D voxelNormalBuffer;
        private Texture3D voxelEmissiveBuffer;
        private Texture3D voxelDirectLightSurfaceBuffer;
        private Texture3D voxelBounceLightSurfaceBuffer;
        private Texture3D voxelDirectLightVolumeBuffer;
        private Texture3D voxelBounceLightVolumeBuffer;

        private static string localAssetFolder = "Assets/VoxelTracing";
        private static string localAssetDataFolder = "Assets/VoxelTracing/Data";

        private RenderTexture voxelCameraSlice;
        private GameObject voxelCameraGameObject;
        private Camera voxelCamera;

        private Vector3Int voxelResolution;
        private ComputeShader slicer;
        private ComputeShader voxelDirectLightTracing;
        private ComputeShader voxelBounceLightTracing;
        private ComputeShader addBuffers;
        private ComputeShader averageBuffers;
        private ComputeShader gaussianBlur;
        private Shader cameraVoxelAlbedoShader;
        private Shader cameraVoxelNormalShader;
        private Shader cameraVoxelEmissiveShader;

        private ComputeBuffer directionalLightsBuffer = null;
        private ComputeBuffer pointLightsBuffer = null;
        private ComputeBuffer spotLightsBuffer = null;
        private ComputeBuffer areaLightsBuffer = null;

        private static TextureFormat textureformat = TextureFormat.RGBAHalf;
        private static RenderTextureFormat rendertextureformat = RenderTextureFormat.ARGBHalf;

        private void GetResources()
        {
            if (slicer == null) 
                slicer = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/VolumeSlicer.compute");

            if (voxelDirectLightTracing == null) 
                voxelDirectLightTracing = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/VoxelDirectLightTracing.compute");

            if (voxelBounceLightTracing == null)
                voxelBounceLightTracing = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/VoxelBounceLightTracing.compute");

            if (gaussianBlur == null)
                gaussianBlur = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/GaussianBlur3D.compute");

            if (addBuffers == null) 
                addBuffers = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/AddBuffers.compute");

            if (averageBuffers == null) 
                averageBuffers = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/AverageBuffers.compute");

            cameraVoxelAlbedoShader = Shader.Find("Hidden/VoxelBufferAlbedo");
            cameraVoxelNormalShader = Shader.Find("Hidden/VoxelBufferNormal");
            cameraVoxelEmissiveShader = Shader.Find("Hidden/VoxelBufferEmissive");
        }

        public bool GetVoxelBuffers()
        {
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneDataFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAlbedoBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_albedo.asset", voxelName);
            string voxelNormalBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_normal.asset", voxelName);
            string voxelEmissiveBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_emissive.asset", voxelName);
            string voxelDirectLightSurfaceBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_directLightSurface.asset", voxelName);
            string voxelBounceLightSurfaceBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_bounce_1.asset", voxelName);
            string voxelDirectLightVolumeBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_directLightVolume.asset", voxelName);
            string voxelBounceLightVolumeBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_bounceVolume_1.asset", voxelName);

            voxelAlbedoBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelAlbedoBufferAssetPath);
            voxelNormalBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelNormalBufferAssetPath);
            voxelEmissiveBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelEmissiveBufferAssetPath);
            voxelDirectLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelDirectLightSurfaceBufferAssetPath);
            voxelBounceLightSurfaceBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelBounceLightSurfaceBufferAssetPath);
            voxelDirectLightVolumeBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelDirectLightVolumeBufferAssetPath);
            voxelBounceLightVolumeBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelBounceLightVolumeBufferAssetPath);

            bool test = 
                voxelAlbedoBuffer == null || 
                voxelNormalBuffer == null || 
                voxelEmissiveBuffer == null || 
                voxelDirectLightSurfaceBuffer == null || 
                voxelBounceLightSurfaceBuffer == null ||
                voxelDirectLightVolumeBuffer == null ||
                voxelBounceLightVolumeBuffer == null;

            return !test;
        }

        public void CalculateResolution()
        {
            voxelResolution = new Vector3Int((int)(voxelSize.x / voxelDensitySize), (int)(voxelSize.y / voxelDensitySize), (int)(voxelSize.z / voxelDensitySize));
        }

        public void SetupAssetFolders()
        {
            //check if there is a data folder, if not then create one
            if (AssetDatabase.IsValidFolder(localAssetDataFolder) == false)
                AssetDatabase.CreateFolder(localAssetFolder, "Data");

            //check for a folder of the same scene name
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneDataFolder = localAssetDataFolder + "/" + sceneName;

            if (activeScene.IsValid() == false || string.IsNullOrEmpty(activeScene.path))
            {
                string message = "Scene is not valid! Be sure to save the scene before you setup volumetrics for it!";
                EditorUtility.DisplayDialog("Error", message, "OK");
                Debug.LogError(message);
                return;
            }

            //check if there is a folder sharing the scene name, if there isn't then create one
            if (AssetDatabase.IsValidFolder(sceneDataFolder) == false)
                AssetDatabase.CreateFolder(localAssetDataFolder, sceneName);
        }

        public void SaveVolumeTexture(string fileName, Texture3D tex3D)
        {
            //build the paths
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string volumeAssetName = fileName + ".asset";
            string volumeAssetPath = sceneVolumetricsFolder + "/" + volumeAssetName;

            AssetDatabase.CreateAsset(tex3D, volumeAssetPath);
        }

        public static void SetComputeKeyword(ComputeShader computeShader, string keyword, bool value)
        {
            if (value)
                computeShader.EnableKeyword(keyword);
            else
                computeShader.DisableKeyword(keyword);
        }

        public void BuildLightComputeBuffers()
        {
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

        public Texture3D ThickenVolume(Texture3D result)
        {
            for (int x = 0; x < result.width; x++)
            {
                for (int y = 0; y < result.height; y++)
                {
                    for (int z = 0; z < result.depth; z++)
                    {
                        Color colorOfCurrentPixel = result.GetPixel(x, y, z);

                        bool thicken = false;

                        Color colorOfUpPixel = colorOfCurrentPixel;
                        Color colorOfDownPixel = colorOfCurrentPixel;
                        Color colorOfRightPixel = colorOfCurrentPixel;
                        Color colorOfLeftPixel = colorOfCurrentPixel;
                        Color colorOfForwardPixel = colorOfCurrentPixel;
                        Color colorOfBackPixel = colorOfCurrentPixel;

                        bool ignoreUp = y + 1 > result.height;
                        bool ignoreDown = y - 1 < 0;

                        bool ignoreRight = x + 1 <= result.width;
                        bool ignoreLeft = x + 1 <= 0;

                        bool ignoreForward = z + 1 <= result.depth;
                        bool ignoreBack = z + 1 <= 0;

                        //if (y + 1 <= result.height)
                        if (!ignoreUp)
                            colorOfUpPixel = result.GetPixel(x, y + 1, z);

                        //if (y - 0 >= 0)
                        if (!ignoreDown)
                            colorOfDownPixel = result.GetPixel(x, y - 1, z);

                        //if (x + 1 <= result.width)
                        if (!ignoreRight)
                            colorOfRightPixel = result.GetPixel(x + 1, y, z);

                        //if (x - 0 >= 0)
                        if (!ignoreLeft)
                            colorOfLeftPixel = result.GetPixel(x - 1, y, z);

                        //if (z + 1 <= result.depth)
                        if (!ignoreForward)
                            colorOfForwardPixel = result.GetPixel(x, y, z + 1);

                        //if (z - 1 <= 0)
                        if (!ignoreBack)
                            colorOfBackPixel = result.GetPixel(x, y, z - 1);

                        //Now check if this voxel is atleast neighboring 1 other voxel
                        bool isUpOpaque = colorOfUpPixel.a > 0.0f;
                        bool isDownOpaque = colorOfDownPixel.a > 0.0f;
                        bool isRightOpaque = colorOfRightPixel.a > 0.0f;
                        bool isLeftOpaque = colorOfLeftPixel.a > 0.0f;
                        bool isForwardOpaque = colorOfForwardPixel.a > 0.0f;
                        bool isBackOpaque = colorOfBackPixel.a > 0.0f;

                        //if the current voxel is opaque
                        if (colorOfCurrentPixel.a > 0.0f)
                        {
                            if (!isUpOpaque && !isDownOpaque && !isRightOpaque && !isLeftOpaque && !isForwardOpaque && !isBackOpaque)
                            {
                                result.SetPixel(x, y, z, colorOfCurrentPixel);
                                result.SetPixel(x, y, z, colorOfCurrentPixel);

                                result.SetPixel(x, y, z, colorOfCurrentPixel);
                                result.SetPixel(x, y, z, colorOfCurrentPixel);

                                result.SetPixel(x, y, z, colorOfCurrentPixel);
                                result.SetPixel(x, y, z, colorOfCurrentPixel);
                            }
                        }

                        //result.SetPixel(x, y, z, colorResult);
                    }
                }
            }

            result.Apply();

            return result;
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 0: SETUP VOXEL CAPTURE ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 0: SETUP VOXEL CAPTURE ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 0: SETUP VOXEL CAPTURE ||||||||||||||||||||||||||||||||||||||||||

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

        [ContextMenu("Step 1: Generate Albedo | Normal | Emissive Buffers")]
        public void GenerateVolumes()
        {
            float timeBeforeFunction = Time.realtimeSinceStartup;

            GenerateVolume(cameraVoxelAlbedoShader, string.Format("{0}_albedo", voxelName), rendertextureformat, TextureFormat.RGBA32);
            GenerateVolume(cameraVoxelNormalShader, string.Format("{0}_normal", voxelName), rendertextureformat, textureformat);
            GenerateVolume(cameraVoxelEmissiveShader, string.Format("{0}_emissive", voxelName), rendertextureformat, textureformat);

            Debug.Log(string.Format("Generating Albedo / Normal / Emissive buffers took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
        }

        public void GenerateVolume(Shader replacementShader, string filename, RenderTextureFormat rtFormat, TextureFormat texFormat)
        {
            UpdateProgressBar(string.Format("Generating {0}", filename), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            CalculateResolution();
            SetupAssetFolders();
            CreateVoxelCamera();

            float xOffset = voxelSize.x / voxelResolution.x;
            float yOffset = voxelSize.y / voxelResolution.y;
            float zOffset = voxelSize.z / voxelResolution.z;

            string renderTypeKey = "";
            int rtDepth = 16;

            voxelCamera.SetReplacementShader(replacementShader, renderTypeKey);
            voxelCamera.allowMSAA = enableAnitAliasing;

            Shader.SetGlobalFloat("_VertexExtrusion", Mathf.Max(zOffset, Mathf.Max(xOffset, yOffset)) * geometryThicknessModifier);

            //||||||||||||||||||||||||||||||||| X |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X |||||||||||||||||||||||||||||||||
            Texture2D[] slices_x_neg = new Texture2D[voxelResolution.x];
            Texture2D[] slices_x_pos = new Texture2D[voxelResolution.x];

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

            //||||||||||||||||||||||||||||||||| Y |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Y |||||||||||||||||||||||||||||||||
            Texture2D[] slices_y_pos = new Texture2D[voxelResolution.y];
            Texture2D[] slices_y_neg = new Texture2D[voxelResolution.y];

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

            //||||||||||||||||||||||||||||||||| Z |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| Z |||||||||||||||||||||||||||||||||
            Texture2D[] slices_z_pos = new Texture2D[voxelResolution.z];
            Texture2D[] slices_z_neg = new Texture2D[voxelResolution.z];

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

            //--------------------- COMBINE RESULTS ---------------------
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

            Debug.Log(string.Format("Generating {0} took {1} seconds.", filename, Time.realtimeSinceStartup - timeBeforeFunction));

            //--------------------- FINAL ---------------------
            SaveVolumeTexture(filename, result);
            CleanupVoxelCamera();
            voxelCameraSlice.Release();

            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT SURFACE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

        [ContextMenu("Step 2: Trace Direct Surface Lighting")]
        public void TraceDirectSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Tracing Direct Surface Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            GetVoxelBuffers();
            BuildLightComputeBuffers();
            CalculateResolution();

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = voxelDirectLightTracing.FindKernel("ComputeShader_TraceSurfaceDirectLight");

            voxelDirectLightTracing.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            SetComputeKeyword(voxelDirectLightTracing, "DIRECTIONAL_LIGHTS", directionalLightsBuffer != null);
            SetComputeKeyword(voxelDirectLightTracing, "POINT_LIGHTS", pointLightsBuffer != null);
            SetComputeKeyword(voxelDirectLightTracing, "SPOT_LIGHTS", spotLightsBuffer != null);
            SetComputeKeyword(voxelDirectLightTracing, "AREA_LIGHTS", areaLightsBuffer != null);

            if (directionalLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "DirectionalLights", directionalLightsBuffer);
            if (pointLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "PointLights", pointLightsBuffer);
            if (spotLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "SpotLights", spotLightsBuffer);
            if (areaLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "AreaLights", areaLightsBuffer);

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            voxelDirectLightTracing.SetTexture(compute_main, "SceneAlbedo", voxelAlbedoBuffer);
            voxelDirectLightTracing.SetTexture(compute_main, "SceneNormal", voxelNormalBuffer);
            voxelDirectLightTracing.SetTexture(compute_main, "SceneEmissive", voxelEmissiveBuffer);
            voxelDirectLightTracing.SetTexture(compute_main, "Write", volumeWrite);

            voxelDirectLightTracing.SetVector("VolumePosition", transform.position);
            voxelDirectLightTracing.SetVector("VolumeSize", voxelSize);

            voxelDirectLightTracing.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_directLightSurface.asset", voxelName);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();

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

        [ContextMenu("Step 3: Trace Direct Volume Lighting")]
        public void TraceDirectVolumeLighting()
        {
            UpdateProgressBar(string.Format("Tracing Direct Volume Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            GetVoxelBuffers();
            BuildLightComputeBuffers();
            CalculateResolution();

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = voxelDirectLightTracing.FindKernel("ComputeShader_TraceVolumeDirectLight");

            voxelDirectLightTracing.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelDirectLightTracing.SetVector("VolumePosition", transform.position);
            voxelDirectLightTracing.SetVector("VolumeSize", voxelSize);

            SetComputeKeyword(voxelDirectLightTracing, "DIRECTIONAL_LIGHTS", directionalLightsBuffer != null);
            SetComputeKeyword(voxelDirectLightTracing, "POINT_LIGHTS", pointLightsBuffer != null);
            SetComputeKeyword(voxelDirectLightTracing, "SPOT_LIGHTS", spotLightsBuffer != null);
            SetComputeKeyword(voxelDirectLightTracing, "AREA_LIGHTS", areaLightsBuffer != null);

            if (directionalLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "DirectionalLights", directionalLightsBuffer);
            if (pointLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "PointLights", pointLightsBuffer);
            if (spotLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "SpotLights", spotLightsBuffer);
            if (areaLightsBuffer != null) voxelDirectLightTracing.SetBuffer(compute_main, "AreaLights", areaLightsBuffer);

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            voxelDirectLightTracing.SetTexture(compute_main, "SceneAlbedo", voxelAlbedoBuffer);
            voxelDirectLightTracing.SetTexture(compute_main, "SceneNormal", voxelNormalBuffer);
            voxelDirectLightTracing.SetTexture(compute_main, "SceneEmissive", voxelEmissiveBuffer);
            voxelDirectLightTracing.SetTexture(compute_main, "Write", volumeWrite);

            voxelDirectLightTracing.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_directLightVolume.asset", voxelName);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();

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

        [ContextMenu("Step 4: Trace Bounce Surface Lighting")]
        public void TraceBounceSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Tracing Bounce Surface Lighting [{0} SAMPLES]...", bounceSurfaceSamples), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            GetVoxelBuffers();
            CalculateResolution();

            /*
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = voxelBounceLightTracing.FindKernel("ComputeShader_TraceSurfaceBounceLight");

            voxelBounceLightTracing.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelBounceLightTracing.SetVector("VolumePosition", transform.position);
            voxelBounceLightTracing.SetVector("VolumeSize", voxelSize);
            voxelBounceLightTracing.SetInt("BounceSamples", bounceSamples);
            voxelBounceLightTracing.SetFloat("RandomSeed", Random.value * 1000.0f);

            SetComputeKeyword(voxelBounceLightTracing, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            voxelBounceLightTracing.SetTexture(compute_main, "SceneAlbedo", voxelAlbedoBuffer);
            voxelBounceLightTracing.SetTexture(compute_main, "SceneNormal", voxelNormalBuffer);
            voxelBounceLightTracing.SetTexture(compute_main, "DirectLightSurface", voxelDirectLightSurfaceBuffer);
            voxelBounceLightTracing.SetTexture(compute_main, "Write", volumeWrite);

            voxelBounceLightTracing.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounce_{1}.asset", voxelName, 1);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();
            */

            ///*
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounce_{1}.asset", voxelName, 1);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = voxelBounceLightTracing.FindKernel("ComputeShader_TraceSurfaceBounceLight");

            voxelBounceLightTracing.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelBounceLightTracing.SetVector("VolumePosition", transform.position);
            voxelBounceLightTracing.SetVector("VolumeSize", voxelSize);
            //voxelBounceLightTracing.SetInt("BounceSamples", bounceSamples);
            voxelBounceLightTracing.SetInt("BounceSamples", 1);
            voxelBounceLightTracing.SetInt("MaxBounceSamples", bounceSurfaceSamples);

            SetComputeKeyword(voxelBounceLightTracing, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            for(int i = 0; i < bounceSurfaceSamples; i++)
            {
                voxelBounceLightTracing.SetFloat("RandomSeed", Random.value * 100000.0f);

                voxelBounceLightTracing.SetTexture(compute_main, "SceneAlbedo", voxelAlbedoBuffer);
                voxelBounceLightTracing.SetTexture(compute_main, "SceneNormal", voxelNormalBuffer);
                voxelBounceLightTracing.SetTexture(compute_main, "DirectLightSurface", voxelDirectLightSurfaceBuffer);
                voxelBounceLightTracing.SetTexture(compute_main, "Write", volumeWrite);

                voxelBounceLightTracing.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();
            //*/


            Debug.Log(string.Format("'TraceBounceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 5: TRACE BOUNCE VOLUME LIGHTING ||||||||||||||||||||||||||||||||||||||||||

        private void OnDispatchComplete(AsyncGPUReadbackRequest asyncGPUReadbackRequest)
        {
            
        }

        [ContextMenu("Step 5: Trace Bounce Volume Lighting")]
        public void TraceBounceVolumeLighting()
        {
            UpdateProgressBar(string.Format("Tracing Bounce Volume Lighting [{0} SAMPLES]...", bounceVolumetricSamples), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            GetVoxelBuffers();
            CalculateResolution();

            /*
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = voxelize.FindKernel("ComputeShader_TraceSurfaceBounceLight");

            voxelize.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            SetComputeKeyword(voxelize, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            voxelize.SetTexture(compute_main, "SceneAlbedo", voxelAlbedoBuffer);
            voxelize.SetTexture(compute_main, "SceneNormal", voxelNormalBuffer);
            voxelize.SetTexture(compute_main, "DirectLightSurface", voxelDirectLightSurfaceBuffer);
            voxelize.SetTexture(compute_main, "Write", volumeWrite);

            voxelize.SetVector("VolumePosition", transform.position);
            voxelize.SetVector("VolumeSize", voxelSize);

            voxelize.SetInt("Samples", samples);

            voxelize.SetFloat("RandomSeed", Random.value * 1000.0f);

            voxelize.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounce_{1}_tile_{}.asset", voxelName);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();
            */

            ///*
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounceVolume_{1}.asset", voxelName, 1);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = voxelBounceLightTracing.FindKernel("ComputeShader_TraceVolumeBounceLight");

            ComputeBuffer dummyComputeBuffer = new ComputeBuffer(1, 4);
            dummyComputeBuffer.SetData(new int[1]);

            voxelBounceLightTracing.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
            voxelBounceLightTracing.SetVector("VolumePosition", transform.position);
            voxelBounceLightTracing.SetVector("VolumeSize", voxelSize);
            //voxelBounceLightTracing.SetInt("BounceSamples", bounceSamples);
            voxelBounceLightTracing.SetInt("BounceSamples", 1);
            voxelBounceLightTracing.SetInt("MaxBounceSamples", bounceVolumetricSamples);

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            for (int i = 0; i < bounceVolumetricSamples; i++)
            {
                voxelBounceLightTracing.SetFloat("RandomSeed", Random.value * 100000.0f);

                voxelBounceLightTracing.SetBuffer(compute_main, "DummyComputeBuffer", dummyComputeBuffer);

                voxelBounceLightTracing.SetTexture(compute_main, "SceneAlbedo", voxelAlbedoBuffer);
                voxelBounceLightTracing.SetTexture(compute_main, "SceneNormal", voxelNormalBuffer);
                voxelBounceLightTracing.SetTexture(compute_main, "DirectLightSurface", voxelDirectLightSurfaceBuffer);
                voxelBounceLightTracing.SetTexture(compute_main, "Write", volumeWrite);

                float timeBeforeDispatch = Time.realtimeSinceStartup;

                //GraphicsFence graphicsFence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.ComputeProcessing);
                //AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(volumeWrite, 0, OnDispatchComplete);

                voxelBounceLightTracing.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(volumeWrite, 0, OnDispatchComplete);
                //AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(volumeWrite);
                //request.WaitForCompletion();

                //dummyComputeBuffer.GetData(new int[1]);

                Texture2D test = new Texture2D(4, 4);
                RenderTexture.active = volumeWrite;
                test.ReadPixels(new Rect(0, 0, test.width, test.height), 0, 0);

                Debug.Log(string.Format("({0} / {1}) 'ComputeShader_TraceVolumeBounceLight' dispatch {2} seconds.", i, bounceVolumetricSamples, Time.realtimeSinceStartup - timeBeforeDispatch));
            }

            RenderTexture.active = null;

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();

            Debug.Log(string.Format("'TraceBounceVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
            //*/

            /*
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int calculatedSampleCount = bounceVolumetricSamples / sampleTiles;

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;

            int kernelTraceSurfaceBounceLightV1 = voxelBounceLightTracing.FindKernel("ComputeShader_TraceVolumeBounceLight");

            for (int tileIndex = 0; tileIndex < sampleTiles; tileIndex++)
            {
                voxelBounceLightTracing.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

                SetComputeKeyword(voxelBounceLightTracing, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

                voxelBounceLightTracing.SetTexture(kernelTraceSurfaceBounceLightV1, "SceneAlbedo", voxelAlbedoBuffer);
                voxelBounceLightTracing.SetTexture(kernelTraceSurfaceBounceLightV1, "SceneNormal", voxelNormalBuffer);
                voxelBounceLightTracing.SetTexture(kernelTraceSurfaceBounceLightV1, "DirectLightSurface", voxelDirectLightSurfaceBuffer);
                voxelBounceLightTracing.SetTexture(kernelTraceSurfaceBounceLightV1, "Write", volumeWrite);

                voxelBounceLightTracing.SetVector("VolumePosition", transform.position);
                voxelBounceLightTracing.SetVector("VolumeSize", voxelSize);

                voxelBounceLightTracing.SetInt("BounceSamples", calculatedSampleCount);

                voxelBounceLightTracing.SetFloat("RandomSeed", Random.value * 1000.0f);

                voxelBounceLightTracing.Dispatch(kernelTraceSurfaceBounceLightV1, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                string newVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounceVolume_{1}_tile_{2}.asset", voxelName, 1, tileIndex);

                AssetDatabase.DeleteAsset(newVoxelAssetPath);

                renderTextureConverter.Save3D(volumeWrite, newVoxelAssetPath, textureObjectSettings);
            }

            volumeWrite.Release();

            //|||||||||||||||||||||||||||||||||||||||||| AVERAGE ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| AVERAGE ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| AVERAGE ||||||||||||||||||||||||||||||||||||||||||
            int kernelAverageBuffers = averageBuffers.FindKernel("ComputeShader_AverageBuffers");

            Texture3D combinedProxy = null;

            volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            for (int tileIndex = 0; tileIndex < sampleTiles; tileIndex++)
            {
                string savedVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounceVolume_{1}_tile_{2}.asset", voxelName, 1, tileIndex);

                Texture3D savedVoxelAsset = AssetDatabase.LoadAssetAtPath<Texture3D>(savedVoxelAssetPath);

                if (combinedProxy == null)
                    combinedProxy = savedVoxelAsset;

                averageBuffers.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

                averageBuffers.SetTexture(kernelAverageBuffers, "AverageBufferA", savedVoxelAsset);
                averageBuffers.SetTexture(kernelAverageBuffers, "AverageBufferB", combinedProxy);
                averageBuffers.SetTexture(kernelAverageBuffers, "Write", volumeWrite);

                averageBuffers.Dispatch(kernelAverageBuffers, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
            }

            string combinedVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounceVolume_{1}.asset", voxelName, 1);
            AssetDatabase.DeleteAsset(combinedVoxelAssetPath);

            renderTextureConverter.Save3D(volumeWrite, combinedVoxelAssetPath, textureObjectSettings);

            volumeWrite.Release();

            if (gaussianSamples < 1)
            {
                Debug.Log(string.Format("'TraceBounceVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
                CloseProgressBar();

                return;
            }
            else
            {
                Texture3D postAverage = AssetDatabase.LoadAssetAtPath<Texture3D>(combinedVoxelAssetPath);

                volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
                volumeWrite.dimension = TextureDimension.Tex3D;
                volumeWrite.volumeDepth = voxelResolution.z;
                volumeWrite.enableRandomWrite = true;
                volumeWrite.Create();

                int kernelGaussianBlur = gaussianBlur.FindKernel("ComputeShader_GaussianBlur");


                Texture3D blurX = null;
                Texture3D blurY = null;
                Texture3D blurZ = null;

                gaussianBlur.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
                gaussianBlur.SetVector("BlurDirection", new Vector4(1, 0, 0, 0));
                gaussianBlur.SetInt("BlurSamples", gaussianSamples);

                gaussianBlur.SetTexture(kernelGaussianBlur, "Read", postAverage);
                gaussianBlur.SetTexture(kernelGaussianBlur, "Write", volumeWrite);

                gaussianBlur.Dispatch(kernelGaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                blurX = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

                gaussianBlur.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 1, 0, 0));
                gaussianBlur.SetInt("BlurSamples", gaussianSamples);

                gaussianBlur.SetTexture(kernelGaussianBlur, "Read", blurX);
                gaussianBlur.SetTexture(kernelGaussianBlur, "Write", volumeWrite);

                gaussianBlur.Dispatch(kernelGaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                blurY = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);

                gaussianBlur.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));
                gaussianBlur.SetVector("BlurDirection", new Vector4(0, 0, 1, 0));
                gaussianBlur.SetInt("BlurSamples", gaussianSamples);

                gaussianBlur.SetTexture(kernelGaussianBlur, "Read", blurY);
                gaussianBlur.SetTexture(kernelGaussianBlur, "Write", volumeWrite);

                gaussianBlur.Dispatch(kernelGaussianBlur, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

                //blurZ = renderTextureConverter.ConvertFromRenderTexture3D(volumeWrite, true);






                string blurredVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounceVolumeBlurred_{1}.asset", voxelName, 1);
                AssetDatabase.DeleteAsset(blurredVoxelAssetPath);

                renderTextureConverter.Save3D(volumeWrite, blurredVoxelAssetPath, textureObjectSettings);

                volumeWrite.Release();

                Debug.Log(string.Format("'TraceBounceVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
                CloseProgressBar();
            }
            */
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 6: COMBINE SURFACE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

        [ContextMenu("Step 6: Combine Surface Direct and Bounce Light")]
        public void CombineSurfaceLighting()
        {
            UpdateProgressBar(string.Format("Combining Surface Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            GetVoxelBuffers();
            CalculateResolution();

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = addBuffers.FindKernel("ComputeShader_AddBuffers");

            addBuffers.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            addBuffers.SetTexture(compute_main, "AddBufferA", voxelDirectLightSurfaceBuffer);
            addBuffers.SetTexture(compute_main, "AddBufferB", voxelBounceLightSurfaceBuffer);
            addBuffers.SetTexture(compute_main, "Write", volumeWrite);

            addBuffers.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_combined.asset", voxelName);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();

            Debug.Log(string.Format("'CombineSurfaceLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| STEP 7: COMBINE VOLUME DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

        [ContextMenu("Step 7: Combine Volume Direct and Bounce Light")]
        public void CombineVolumeLighting()
        {
            UpdateProgressBar(string.Format("Combining Volume Lighting..."), 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            GetResources();
            GetVoxelBuffers();
            CalculateResolution();

            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            string sceneName = activeScene.name;
            string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;

            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

            int compute_main = addBuffers.FindKernel("ComputeShader_AddBuffers");

            addBuffers.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
            volumeWrite.dimension = TextureDimension.Tex3D;
            volumeWrite.volumeDepth = voxelResolution.z;
            volumeWrite.enableRandomWrite = true;
            volumeWrite.Create();

            addBuffers.SetTexture(compute_main, "AddBufferA", voxelDirectLightVolumeBuffer);
            //addBuffers.SetTexture(compute_main, "AddBufferB", voxelBounceLightVolumeBuffer);





            string blurredVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounceVolumeBlurred_{1}.asset", voxelName, 1);
            addBuffers.SetTexture(compute_main, "AddBufferB", AssetDatabase.LoadAssetAtPath<Texture3D>(blurredVoxelAssetPath));







            addBuffers.SetTexture(compute_main, "Write", volumeWrite);

            addBuffers.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
            string voxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_combinedVolume.asset", voxelName);

            RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, rendertextureformat, textureformat);
            RenderTextureConverter.TextureObjectSettings textureObjectSettings = new RenderTextureConverter.TextureObjectSettings()
            {
                anisoLevel = 0,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                mipMaps = true,
            };

            renderTextureConverter.Save3D(volumeWrite, voxelAssetPath, textureObjectSettings);

            volumeWrite.Release();

            Debug.Log(string.Format("'CombineVolumeLighting' took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        private void OnDrawGizmos()
        {
            CalculateResolution();

            Gizmos.color = Color.white;

            if (previewBounds)
            {
                Gizmos.DrawWireCube(transform.position, voxelSize);
            }
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        public void UpdateProgressBar(string description, float progress)
        {
            EditorUtility.DisplayProgressBar("Voxel Tracer", description, progress);
        }

        public void CloseProgressBar()
        {
            EditorUtility.ClearProgressBar();
        }
    }
}