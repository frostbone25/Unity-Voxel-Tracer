using BakedVolumetrics;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices.ComTypes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using static UnityEngine.Rendering.PostProcessing.PostProcessResources;

/*
 * SELF NOTE 1: The Anti-Aliasing when used for the generation of the voxels does help maintain geometry at low resolutions, however there is a quirk because it also samples the background color.
 * This was noticable when rendering position/normals voxel buffers where when on some geo looked darker than usual because the BG color was black.
 * Not to sure how to solve this and just might deal with it?
*/

public class VoxelizeScene : MonoBehaviour
{
    private static string localAssetFolder = "Assets/VoxelTracing";
    private static string localAssetDataFolder = "Assets/VoxelTracing/Data";

    [Header("Voxelizer Main")]
    public string voxelName = "Voxel";
    public Vector3 voxelSize = new Vector3(10.0f, 10.0f, 10.0f);
    public float voxelDensitySize = 1.0f;
    public float geometryThicknessModifier = 0.0f;
    public bool enableAnitAliasing = false;
    public bool blendVoxelResult = false;

    [Header("Baking Options")]
    [Range(1, 512)] public int samples = 64;
    [Range(1, 16)] public int sampleTiles = 4;

    public int bounces = 1;
    public bool normalOrientedHemisphereSampling = false;

    [Header("Gizmos")]
    public bool previewBounds = true;

    [Header("Buffers")]
    public Texture3D voxelAlbedoBuffer;
    public Texture3D voxelNormalBuffer;
    public Texture3D voxelEmissiveBuffer;
    public Texture3D voxelDirectLightSurfaceBuffer;
    public Texture3D voxelBounceLightSurfaceBuffer;
    public Texture3D voxelDirectLightVolumeBuffer;
    public Texture3D voxelBounceLightVolumeBuffer;

    private RenderTexture voxelCameraSlice;
    private GameObject voxelCameraGameObject;
    private Camera voxelCamera;

    private Light[] sceneLights
    {
        get
        {
            return FindObjectsOfType<Light>();
        }
    }

    private Vector3Int voxelResolution;
    private ComputeShader slicer;
    private ComputeShader voxelize;
    private Shader cameraVoxelAlbedoShader;
    private Shader cameraVoxelNormalShader;
    private Shader cameraVoxelEmissiveShader;

    private static TextureFormat textureformat = TextureFormat.RGBAHalf;
    private static RenderTextureFormat rendertextureformat = RenderTextureFormat.ARGBHalf;

    private void GetResources()
    {
        if (slicer == null) slicer = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/Slicer.compute");
        if (voxelize == null) voxelize = AssetDatabase.LoadAssetAtPath<ComputeShader>(localAssetFolder + "/VoxelTracing.compute");

        cameraVoxelAlbedoShader = Shader.Find("Hidden/VoxelBufferAlbedo");
        cameraVoxelNormalShader = Shader.Find("Hidden/VoxelBufferNormal");
        cameraVoxelEmissiveShader = Shader.Find("Hidden/VoxelBufferEmissive");
    }

    private void GetVoxelBuffers()
    {
        UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
        string sceneName = activeScene.name;
        string sceneDataFolder = localAssetDataFolder + "/" + sceneName;
        string voxelAlbedoBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_albedo.asset", voxelName);
        string voxelNormalBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_normal.asset", voxelName);
        string voxelEmissiveBufferAssetPath = sceneDataFolder + "/" + string.Format("{0}_emissive.asset", voxelName);

        voxelAlbedoBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelAlbedoBufferAssetPath);
        voxelNormalBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelNormalBufferAssetPath);
        voxelEmissiveBuffer = AssetDatabase.LoadAssetAtPath<Texture3D>(voxelEmissiveBufferAssetPath);
    }

    private void CalculateResolution()
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

    private Texture2D ConvertFromRenderTexture2D(RenderTexture rt, TextureFormat texFormat)
    {
        Texture2D output = new Texture2D(rt.width, rt.height, texFormat, false);
        output.alphaIsTransparency = true;

        RenderTexture.active = rt;

        output.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        output.Apply();

        return output;
    }

    private void SaveVolumeTexture(string fileName, Texture3D tex3D)
    {
        //build the paths
        UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
        string sceneName = activeScene.name;
        string sceneVolumetricsFolder = localAssetDataFolder + "/" + sceneName;
        string volumeAssetName = fileName + ".asset";
        string volumeAssetPath = sceneVolumetricsFolder + "/" + volumeAssetName;

        AssetDatabase.CreateAsset(tex3D, volumeAssetPath);
    }

    private static void SetComputeKeyword(ComputeShader computeShader, string keyword, bool value)
    {
        if (value)
            computeShader.EnableKeyword(keyword);
        else
            computeShader.DisableKeyword(keyword);
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
                    if(colorOfCurrentPixel.a > 0.0f)
                    {
                        if(!isUpOpaque && !isDownOpaque && !isRightOpaque && !isLeftOpaque && !isForwardOpaque && !isBackOpaque)
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

    private void CreateVoxelCamera()
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

    private void CleanupVoxelCamera()
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
        GenerateVolume(cameraVoxelAlbedoShader, string.Format("{0}_albedo", voxelName), rendertextureformat, textureformat);
        GenerateVolume(cameraVoxelNormalShader, string.Format("{0}_normal", voxelName), rendertextureformat, textureformat);
        GenerateVolume(cameraVoxelEmissiveShader, string.Format("{0}_emissive", voxelName), rendertextureformat, textureformat);
    }

    public void GenerateVolume(Shader replacementShader, string filename, RenderTextureFormat rtFormat, TextureFormat texFormat)
    {
        float timeBeforeGenerating = Time.realtimeSinceStartup;

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
            slices_x_neg[i] = ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
        }

        //--------------------- X NEGATIVE ---------------------
        voxelCameraGameObject.transform.eulerAngles = new Vector3(0, -90.0f, 0);

        for (int i = 0; i < voxelResolution.x; i++)
        {
            voxelCameraGameObject.transform.position = transform.position + new Vector3(voxelSize.x / 2.0f, 0, 0) - new Vector3(xOffset * i, 0, 0);
            voxelCamera.Render();
            slices_x_pos[i] = ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
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
            slices_y_pos[i] = ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
        }

        //--------------------- Y NEGATIVE ---------------------
        voxelCameraGameObject.transform.eulerAngles = new Vector3(90.0f, 0, 0);

        for (int i = 0; i < voxelResolution.y; i++)
        {
            voxelCameraGameObject.transform.position = transform.position + new Vector3(0, voxelSize.y / 2.0f, 0) - new Vector3(0, yOffset * i, 0);
            voxelCamera.Render();
            slices_y_neg[i] = ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
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
            slices_z_pos[i] = ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
        }

        //--------------------- Z NEGATIVE ---------------------
        voxelCameraGameObject.transform.eulerAngles = new Vector3(0, 180.0f, 0);

        for (int i = 0; i < voxelResolution.z; i++)
        {
            voxelCameraGameObject.transform.position = transform.position + new Vector3(0, 0, voxelSize.z / 2.0f) - new Vector3(0, 0, zOffset * i);
            voxelCamera.Render();
            slices_z_neg[i] = ConvertFromRenderTexture2D(voxelCameraSlice, texFormat);
        }

        //--------------------- COMBINE RESULTS ---------------------
        Texture3D result = new Texture3D(voxelResolution.x, voxelResolution.y, voxelResolution.z, DefaultFormat.HDR, TextureCreationFlags.MipChain);
        result.filterMode = FilterMode.Point;

        for (int x = 0; x < result.width; x++)
        {
            for(int y = 0; y < result.height; y++)
            {
                for(int z = 0; z < result.depth; z++)
                {
                    Color colorResult = new Color(0, 0, 0, 0);

                    Color color_x_pos = slices_x_pos[(result.width - 1) - x].GetPixel(z, y);
                    Color color_x_neg = slices_x_neg[x].GetPixel((result.depth - 1) - z, y);

                    Color color_y_pos = slices_y_pos[y].GetPixel(x, (result.depth - 1) - z);
                    Color color_y_neg = slices_y_neg[(result.height - 1) - y].GetPixel(x, z);

                    Color color_z_pos = slices_z_pos[z].GetPixel(x, y);
                    Color color_z_neg = slices_z_neg[(result.depth - 1) - z].GetPixel((result.width - 1) - x, y);

                    if(blendVoxelResult)
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

        Debug.Log(string.Format("Generating {0} took {1} seconds.", filename, Time.realtimeSinceStartup - timeBeforeGenerating));

        //--------------------- FINAL ---------------------
        SaveVolumeTexture(filename, result);
        CleanupVoxelCamera();
        voxelCameraSlice.Release();
    }

    //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT LIGHTING ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| STEP 2: TRACE DIRECT LIGHTING ||||||||||||||||||||||||||||||||||||||||||

    [ContextMenu("Step 2: Trace Direct Lighting")]
    public void TraceDirectLighting()
    {
        GetResources();
        //GetVoxelBuffers();

        //|||||||||||||||||||||||||||||||||||||||||| GET SCENE LIGHTS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| GET SCENE LIGHTS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| GET SCENE LIGHTS ||||||||||||||||||||||||||||||||||||||||||
        List<VoxelLightDirectional> voxelLightDirectionals = new List<VoxelLightDirectional>();
        List<VoxelLightPoint> voxelLightPoints = new List<VoxelLightPoint>();
        List<VoxelLightSpot> voxelLightSpots = new List<VoxelLightSpot>();
        List<VoxelLightArea> voxelLightAreas = new List<VoxelLightArea>();

        foreach (Light sceneLight in sceneLights)
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

        //|||||||||||||||||||||||||||||||||||||||||| BUILD SCENE LIGHT BUFFERS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| BUILD SCENE LIGHT BUFFERS ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| BUILD SCENE LIGHT BUFFERS ||||||||||||||||||||||||||||||||||||||||||

        ComputeBuffer directionalLightsBuffer = null;
        ComputeBuffer pointLightsBuffer = null;
        ComputeBuffer spotLightsBuffer = null;
        ComputeBuffer areaLightsBuffer = null;

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

        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

        int compute_main = voxelize.FindKernel("ComputeShader_TraceSurfaceDirectLight_V1");

        voxelize.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

        SetComputeKeyword(voxelize, "DIRECTIONAL_LIGHTS", directionalLightsBuffer != null);
        SetComputeKeyword(voxelize, "POINT_LIGHTS", pointLightsBuffer != null);
        SetComputeKeyword(voxelize, "SPOT_LIGHTS", spotLightsBuffer != null);
        SetComputeKeyword(voxelize, "AREA_LIGHTS", areaLightsBuffer != null);

        if (directionalLightsBuffer != null) voxelize.SetBuffer(compute_main, "DirectionalLights", directionalLightsBuffer);
        if (pointLightsBuffer != null) voxelize.SetBuffer(compute_main, "PointLights", pointLightsBuffer);
        if (spotLightsBuffer != null) voxelize.SetBuffer(compute_main, "SpotLights", spotLightsBuffer);
        if (areaLightsBuffer != null) voxelize.SetBuffer(compute_main, "AreaLights", areaLightsBuffer);

        RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
        volumeWrite.dimension = TextureDimension.Tex3D;
        volumeWrite.volumeDepth = voxelResolution.z;
        volumeWrite.enableRandomWrite = true;
        volumeWrite.Create();

        voxelize.SetTexture(compute_main, "SceneAlbedo", voxelAlbedoBuffer);
        voxelize.SetTexture(compute_main, "SceneNormal", voxelNormalBuffer);
        voxelize.SetTexture(compute_main, "SceneEmissive", voxelEmissiveBuffer);
        voxelize.SetTexture(compute_main, "Write", volumeWrite);

        voxelize.SetVector("VolumePosition", transform.position);
        voxelize.SetVector("VolumeSize", voxelSize);

        voxelize.SetInt("Samples", samples);

        voxelize.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / 8f), Mathf.CeilToInt(voxelResolution.y / 8f), Mathf.CeilToInt(voxelResolution.z / 8f));

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
    }

    //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| STEP 3: TRACE BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

    [ContextMenu("Step 3: Trace Bounce Lighting")]
    public void TraceBounceLighting()
    {
        GetResources();
        //GetVoxelBuffers();

        /*
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

        int compute_main = voxelize.FindKernel("ComputeShader_TraceSurfaceBounceLight_V1");

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

        voxelize.SetFloat("_Seed", Random.value * 1000.0f);

        voxelize.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / 8f), Mathf.CeilToInt(voxelResolution.y / 8f), Mathf.CeilToInt(voxelResolution.z / 8f));

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



        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

        int calculatedSampleCount = samples / sampleTiles;

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

        int kernelTraceSurfaceBounceLightV1 = voxelize.FindKernel("ComputeShader_TraceSurfaceBounceLight_V1");
        int kernelAverageBuffers = voxelize.FindKernel("ComputeShader_AverageBuffers");

        for (int tileIndex = 0; tileIndex < sampleTiles; tileIndex++)
        {
            voxelize.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            SetComputeKeyword(voxelize, "NORMAL_ORIENTED_HEMISPHERE_SAMPLING", normalOrientedHemisphereSampling);

            voxelize.SetTexture(kernelTraceSurfaceBounceLightV1, "SceneAlbedo", voxelAlbedoBuffer);
            voxelize.SetTexture(kernelTraceSurfaceBounceLightV1, "SceneNormal", voxelNormalBuffer);
            voxelize.SetTexture(kernelTraceSurfaceBounceLightV1, "DirectLightSurface", voxelDirectLightSurfaceBuffer);
            voxelize.SetTexture(kernelTraceSurfaceBounceLightV1, "Write", volumeWrite);

            voxelize.SetVector("VolumePosition", transform.position);
            voxelize.SetVector("VolumeSize", voxelSize);

            voxelize.SetInt("Samples", calculatedSampleCount);

            voxelize.SetFloat("_Seed", Random.value * 1000.0f);

            voxelize.Dispatch(kernelTraceSurfaceBounceLightV1, Mathf.CeilToInt(voxelResolution.x / 8f), Mathf.CeilToInt(voxelResolution.y / 8f), Mathf.CeilToInt(voxelResolution.z / 8f));

            string newVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounce_{1}_tile_{2}.asset", voxelName, 1, tileIndex);

            AssetDatabase.DeleteAsset(newVoxelAssetPath);

            renderTextureConverter.Save3D(volumeWrite, newVoxelAssetPath, textureObjectSettings);
        }

        volumeWrite.Release();

        //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| RESULT ||||||||||||||||||||||||||||||||||||||||||
        Texture3D combinedProxy = null;

        volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
        volumeWrite.dimension = TextureDimension.Tex3D;
        volumeWrite.volumeDepth = voxelResolution.z;
        volumeWrite.enableRandomWrite = true;
        volumeWrite.Create();

        for (int tileIndex = 0; tileIndex < sampleTiles; tileIndex++)
        {
            string savedVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounce_{1}_tile_{2}.asset", voxelName, 1, tileIndex);

            Texture3D savedVoxelAsset = AssetDatabase.LoadAssetAtPath<Texture3D>(savedVoxelAssetPath);

            if (combinedProxy == null)
                combinedProxy = savedVoxelAsset;

            voxelize.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

            voxelize.SetTexture(kernelAverageBuffers, "AverageBufferA", savedVoxelAsset);
            voxelize.SetTexture(kernelAverageBuffers, "AverageBufferB", combinedProxy);
            voxelize.SetTexture(kernelAverageBuffers, "Write", volumeWrite);

            voxelize.Dispatch(kernelAverageBuffers, Mathf.CeilToInt(voxelResolution.x / 8f), Mathf.CeilToInt(voxelResolution.y / 8f), Mathf.CeilToInt(voxelResolution.z / 8f));
        }

        string combinedVoxelAssetPath = sceneVolumetricsFolder + "/" + string.Format("{0}_bounce_{1}.asset", voxelName, 1);
        AssetDatabase.DeleteAsset(combinedVoxelAssetPath);

        renderTextureConverter.Save3D(volumeWrite, combinedVoxelAssetPath, textureObjectSettings);

        volumeWrite.Release();
    }

    //|||||||||||||||||||||||||||||||||||||||||| STEP 4: COMBINE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| STEP 4: COMBINE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| STEP 4: COMBINE DIRECT AND BOUNCE LIGHTING ||||||||||||||||||||||||||||||||||||||||||

    [ContextMenu("Step 4: Combine Direct and Bounce Light")]
    public void CombineLighting()
    {
        GetResources();
        //GetVoxelBuffers();

        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| COMPUTE SHADER ||||||||||||||||||||||||||||||||||||||||||

        int compute_main = voxelize.FindKernel("ComputeShader_AddBuffers");

        voxelize.SetVector("VolumeResolution", new Vector4(voxelResolution.x, voxelResolution.y, voxelResolution.z, 0));

        RenderTexture volumeWrite = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, rendertextureformat);
        volumeWrite.dimension = TextureDimension.Tex3D;
        volumeWrite.volumeDepth = voxelResolution.z;
        volumeWrite.enableRandomWrite = true;
        volumeWrite.Create();

        voxelize.SetTexture(compute_main, "AddBufferA", voxelDirectLightSurfaceBuffer);
        voxelize.SetTexture(compute_main, "AddBufferB", voxelBounceLightSurfaceBuffer);
        voxelize.SetTexture(compute_main, "Write", volumeWrite);

        voxelize.SetVector("VolumePosition", transform.position);
        voxelize.SetVector("VolumeSize", voxelSize);

        voxelize.Dispatch(compute_main, Mathf.CeilToInt(voxelResolution.x / 8f), Mathf.CeilToInt(voxelResolution.y / 8f), Mathf.CeilToInt(voxelResolution.z / 8f));

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
        EditorUtility.DisplayProgressBar("Voxelizer", description, progress);
    }

    public void CloseProgressBar()
    {
        EditorUtility.ClearProgressBar();
    }
}