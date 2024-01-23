//using System;
using System.Collections;
using System.Collections.Generic;
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
    public class SceneVoxelizer : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        //[Header("Scene Voxelization")]
        public string voxelName = "Voxel"; //Name of the asset
        public Vector3 voxelSize = new Vector3(10.0f, 10.0f, 10.0f); //Size of the volume
        public float voxelDensitySize = 1.0f; //Size of each voxel (Smaller = More Voxels, Larger = Less Voxels)

        //controls how many "pixels" per unit an object will have.
        public float texelDensityPerUnit = 1;

        //minimum resolution for objects in the scene (so objects too small will be capped to this value resolution wise)
        public int minimumBufferResolution = 16;

        //[OPTIMIZATION] only includes meshes that are marked contribute GI static
        public bool onlyIncludeGIContributors = true;

        //[TODO] This is W.I.P, might be removed.
        //This is supposed to help thicken geometry during voxelization to prevent leaks during tracing.
        private float geometryThicknessModifier = 0.0f;

        //[BROKEN] Anti-Aliasing to retain geometry shapes during voxelization.
        //Used to work... although now it appears break voxelization results however?
        private bool enableAnitAliasing = false;

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
        private Texture3D voxelEnvironmentLightVolumeBuffer;
        private Texture3D voxelBounceLightSurfaceBuffer;
        private Texture3D voxelDirectLightVolumeBuffer;
        private Texture3D voxelBounceLightVolumeBuffer;

        private Cubemap environmentMap;

        private string voxelAlbedoBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_albedo.asset", voxelName);
        private string voxelNormalBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_normal.asset", voxelName);
        private string voxelEmissiveBufferAssetPath => localAssetSceneDataFolder + "/" + string.Format("{0}_emissive.asset", voxelName);

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
        private ComputeShader voxelEnvironmentVolumetricLight;
        private ComputeShader addBuffers;
        private ComputeShader gaussianBlur;
        private ComputeShader voxelizeScene;
        private ComputeShader dilate;

        private string slicerAssetPath => localAssetComputeFolder + "/VolumeSlicer.compute";
        private string voxelizeSceneAssetPath => localAssetComputeFolder + "/VoxelizeScene.compute";
        private string dilateAssetPath => localAssetComputeFolder + "/Dilation.compute";

        /// <summary>
        /// Load in necessary resources for the voxel tracer.
        /// </summary>
        private bool GetResources()
        {
            slicer = AssetDatabase.LoadAssetAtPath<ComputeShader>(slicerAssetPath);
            voxelizeScene = AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelizeSceneAssetPath);
            dilate = AssetDatabase.LoadAssetAtPath<ComputeShader>(dilateAssetPath);

            if (slicer == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", slicerAssetPath));
                return false;
            }
            else if(voxelizeScene == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", voxelizeSceneAssetPath));
                return false;
            }
            else if (dilate == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", dilateAssetPath));
                return false;
            }

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

        public void GenerateVolumes()
        {
            float timeBeforeFunction = Time.realtimeSinceStartup;

            //NOTE TO SELF: Keep render texture format high precision.
            //For instance changing it to an 8 bit for the albedo buffer seems to kills color definition.

            GenerateAlbedoEmissiveBuffers();

            Debug.Log(string.Format("Generating Albedo / Normal / Emissive buffers took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
        }

        public void RenderScene(CommandBuffer commandBuffer, RenderTexture renderTexture, Camera camera, List<ObjectMetaData> objectsMetaData)
        {
            //calculate the view matrix of the camera that we are using to render the scene with.
            Matrix4x4 lookMatrix = Matrix4x4.LookAt(camera.transform.position, camera.transform.position + camera.transform.forward, camera.transform.up);
            Matrix4x4 scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
            Matrix4x4 viewMatrix = scaleMatrix * lookMatrix.inverse;

            //make the render target active, and setup projection
            commandBuffer.SetRenderTarget(renderTexture);
            commandBuffer.SetViewProjectionMatrices(viewMatrix, camera.projectionMatrix);
            commandBuffer.SetViewport(new Rect(0, 0, renderTexture.width, renderTexture.height));
            commandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));

            //create a custom material with a custom shader that will only show the buffers we feed it.
            Material objectMaterial = new Material(Shader.Find("Hidden/VoxelBufferMeta"));

            MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
            materialPropertyBlock.SetVector("unity_LightmapST", new Vector4(1, 1, 0, 0)); //cancel out any lightmap UV scaling/offsets.

            //iterate through each object we collected
            for (int i = 0; i < objectsMetaData.Count; i++)
            {
                ObjectMetaData objectMetaData = objectsMetaData[i];

                //if our object has materials
                if (objectMetaData.materials != null)
                {
                    //iterate through each material on the object
                    for (int j = 0; j < objectMetaData.materials.Length; j++)
                    {
                        //get the meta data we collected earlier of the material
                        MaterialMetaData materialMetaData = objectMetaData.materials[j];

                        //make sure it isn't empty
                        if (materialMetaData.isEmpty() == false)
                        {
                            int submeshIndex = j; //In unity, submeshes are linked to materials. If a mesh has 2 materials, then there are 2 submeshes. So the submesh index should match the material index.

                            //feed it our albedo buffer
                            materialPropertyBlock.SetTexture("_MainTex", materialMetaData.albedo);

                            //draw the mesh in the scene, rendering only its raw albedo colors.
                            commandBuffer.DrawMesh(objectMetaData.mesh, objectMetaData.transformMatrix, objectMaterial, submeshIndex, 0, materialPropertyBlock);
                        }
                    }
                }
            }

            //actually renders the scene.
            Graphics.ExecuteCommandBuffer(commandBuffer);
        }

        public List<ObjectMetaData> BuildMetaObjectBuffers()
        {
            //|||||||||||||||||||||||||||||||||||||| GATHER RENDERER HASH CODES TO EXCLUDE ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| GATHER RENDERER HASH CODES TO EXCLUDE ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| GATHER RENDERER HASH CODES TO EXCLUDE ||||||||||||||||||||||||||||||||||||||
            //If LODS exist in the scene, we will first gather them up so we can create a list of renderers to exclude later.
            //We do this so that way later we only render the first LOD0 meshes.
            //If we don't then we end up rendering all of the meshes that are apart of an LOD group, and that will not only slow things down, but skew results.
            //So we want to keep things clean and only render the first LOD level.

            //Fetch all LOD groups in the scene
            LODGroup[] lodGroups = FindObjectsOfType<LODGroup>();

            //Intalize a dynamic int array that will contain a list of hash codes for renderers that are used after LOD0
            List<int> renderersAfterLOD0_HashCodes = new List<int>();

            //iterate through each LOD group in the scene
            for (int i = 0; i < lodGroups.Length; i++)
            {
                //compile a list of hash codes for renderers that we find after LOD0
                int[] hashCodes = GetRendererHashCodesAfterLOD0(lodGroups[i]);

                //if the current LOD group has no levels past LOD0 then we are done here.
                if (hashCodes == null)
                    continue; //skip to the next iteration in the loop

                //accumulate hash codes into our dynamic list.
                for (int j = 0; j < hashCodes.Length; j++)
                    renderersAfterLOD0_HashCodes.Add(hashCodes[j]);
            }

            //|||||||||||||||||||||||||||||||||||||| CREATE ALBEDO/EMISSION BUFFERS FOR EACH MESH ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CREATE ALBEDO/EMISSION BUFFERS FOR EACH MESH ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CREATE ALBEDO/EMISSION BUFFERS FOR EACH MESH ||||||||||||||||||||||||||||||||||||||

            //formats for each of the object buffers
            RenderTextureFormat albedoFormat = RenderTextureFormat.ARGB4444;
            RenderTextureFormat emissionFormat = RenderTextureFormat.ARGB2101010;

            //fetch all mesh renderers in the scene.
            MeshRenderer[] meshRenderers = FindObjectsOfType<MeshRenderer>();

            //initalize a dynamic array of object meta data that will be filled up.
            List<ObjectMetaData> objectsMetaData = new List<ObjectMetaData>();

            //iterate through each mesh renderer in the scene
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                //current mesh renderer element
                MeshRenderer meshRenderer = meshRenderers[i];

                //get the hash code
                int meshRendererHashCode = meshRenderer.GetHashCode();

                //Compare the hash code of the current mesh renderer we have against the compiled list of hash codes we made earlier.
                //So if the current mesh renderer we have is actually apart of an LOD group, AND is not apart of an LOD0 level then skip it.
                //We only want to use renderers apart of the LOD0 level.
                if (renderersAfterLOD0_HashCodes.Contains(meshRendererHashCode))
                    continue; //skip to the next iteration in the loop.

                //get the mesh filter component so we can grab the actual mesh for drawing later.
                MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();

                //(IF ENABLED) If we only want to include meshes that contribute to GI, so saves us some additional computation
                bool includeMesh = onlyIncludeGIContributors ? GameObjectUtility.GetStaticEditorFlags(meshRenderer.gameObject).HasFlag(StaticEditorFlags.ContributeGI) : true;

                //compute texel density for each mesh renderer
                int objectTextureResolutionSquare = (int)(meshRenderer.bounds.size.magnitude * texelDensityPerUnit);

                //if it ends up being too low resolution just use the minimum resolution.
                objectTextureResolutionSquare = Mathf.Max(minimumBufferResolution, objectTextureResolutionSquare);

                //If there is a mesh filter, and we can include the mesh then lets get started!
                if (meshFilter != null && includeMesh)
                {
                    //get the mesh and it's materials
                    Mesh mesh = meshFilter.sharedMesh;
                    Material[] materials = meshRenderer.sharedMaterials;

                    //lets create our object meta data now so we can store some of this data later.
                    ObjectMetaData objectMetaData = new ObjectMetaData()
                    {
                        mesh = mesh,
                        bounds = meshRenderer.bounds,
                        transformMatrix = meshRenderer.transform.localToWorldMatrix,
                        materials = new MaterialMetaData[materials.Length]
                    };

                    //Property values used in the "META" pass in unity shaders.
                    //The "META" pass is used during lightmapping to extract albedo/emission colors from materials in a scene.
                    //Which is exactly what we need!
                    MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
                    materialPropertyBlock.SetVector("unity_MetaVertexControl", new Vector4(1, 0, 0, 0)); //Only Lightmap UVs
                    materialPropertyBlock.SetFloat("unity_OneOverOutputBoost", 1.0f);
                    materialPropertyBlock.SetFloat("unity_MaxOutputValue", 0.97f);
                    materialPropertyBlock.SetInt("unity_UseLinearSpace", QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
                    materialPropertyBlock.SetVector("unity_LightmapST", new Vector4(1, 1, 0, 0)); //Cancel out lightmapping scale/offset values if its already lightmapped.

                    //Create a projection matrix, mapped to UV space [0,1]
                    Matrix4x4 uvProjection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(0, 1, 1, 0, -50, 50), true);

                    //Create a command buffer so we can render the albedo/emissive buffers of each object.
                    using (CommandBuffer metaDataCommandBuffer = new CommandBuffer())
                    {
                        //setup projection
                        metaDataCommandBuffer.SetViewProjectionMatrices(Matrix4x4.identity, uvProjection);
                        metaDataCommandBuffer.SetViewport(new Rect(0, 0, objectTextureResolutionSquare, objectTextureResolutionSquare));
                        metaDataCommandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));

                        //iterate through each material the mesh renderer has
                        for (int j = 0; j < materials.Length; j++)
                        {
                            //create a custom material meta data, this will eventually store the object albedo/emissive buffers... if it can get them
                            MaterialMetaData materialMetaData = new MaterialMetaData();

                            //get the current material
                            Material material = materials[j];

                            //find the pass index on the material so we can render it.
                            //if it doesn't exist it will return -1 which means the material doesn't have one... and we will just have to leave materialMetaData empty.
                            int metaPassIndex = material.FindPass("Meta");
                            int submeshIndex = j; //In unity, submeshes are linked to materials. If a mesh has 2 materials, then there are 2 submeshes. So the submesh index should match the material index.

                            //The meta pass is used in the "Validate Albedo" scene draw mode... which we don't want so make sure its disabled.
                            material.DisableKeyword("EDITOR_VISUALIZATION");

                            //if the pass exists...
                            if (metaPassIndex != -1)
                            {
                                //fetch our dilation function kernel in the compute shader
                                int ComputeShader_Dilation = dilate.FindKernel("ComputeShader_Dilation");

                                //set the amount of dilation steps it will take
                                dilate.SetInt("KernelSize", 256);

                                //|||||||||||||||||||||||||||||||||||||| ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //here we will render the albedo buffer of the object.
                                //the unity meta pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                                //create our albedo render texture buffer
                                RenderTexture meshAlbedoBuffer = new RenderTexture(objectTextureResolutionSquare, objectTextureResolutionSquare, 32, albedoFormat);
                                meshAlbedoBuffer.filterMode = FilterMode.Point;
                                meshAlbedoBuffer.enableRandomWrite = true; //important
                                meshAlbedoBuffer.Create();

                                //put our render texture to use.
                                metaDataCommandBuffer.SetRenderTarget(meshAlbedoBuffer);

                                //show only the albedo colors in the meta pass.
                                materialPropertyBlock.SetVector("unity_MetaFragmentControl", new Vector4(1, 0, 0, 0)); //Show Albedo

                                //queue a draw mesh command, only rendering the meta pass on our material.
                                metaDataCommandBuffer.DrawMesh(mesh, Matrix4x4.identity, material, submeshIndex, metaPassIndex, materialPropertyBlock);

                                //actually renders our albedo buffer to the render target.
                                Graphics.ExecuteCommandBuffer(metaDataCommandBuffer);

                                //|||||||||||||||||||||||||||||||||||||| DILATE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //Now before we use the albedo buffer... we have to do additional processing on it before its even usable.
                                //Even if the resolution was crazy high we would get black outlines on the edges of the lightmap UVs which can mess up our albedo results later.
                                //So we will run a dilation filter, which will basically copy pixels around to mitigate those black outlines.

                                //reuse the same buffer, the compute shader will modify the values of this render target.
                                dilate.SetTexture(ComputeShader_Dilation, "Write", meshAlbedoBuffer);

                                //let the GPU perform dilation
                                dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);

                                //now we are finished! so lets store this render texture for later.
                                materialMetaData.albedo = meshAlbedoBuffer;
                                materialMetaData.albedo.filterMode = FilterMode.Point;

                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //here we will render the emissive buffer of the object.
                                //the unity meta pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                                //create our emissive render texture buffer
                                RenderTexture meshEmissiveBuffer = new RenderTexture(objectTextureResolutionSquare, objectTextureResolutionSquare, 32, emissionFormat);
                                meshEmissiveBuffer.filterMode = FilterMode.Point;
                                meshEmissiveBuffer.enableRandomWrite = true;
                                meshEmissiveBuffer.Create();

                                //put our render texture to use.
                                metaDataCommandBuffer.SetRenderTarget(meshEmissiveBuffer);

                                //show only the emissive colors in the meta pass.
                                materialPropertyBlock.SetVector("unity_MetaFragmentControl", new Vector4(0, 1, 0, 0)); //Show Emission

                                //queue a draw mesh command, only rendering the meta pass on our material.
                                metaDataCommandBuffer.DrawMesh(mesh, Matrix4x4.identity, material, submeshIndex, metaPassIndex, materialPropertyBlock);

                                //actually renders our emissive buffer to the render target.
                                Graphics.ExecuteCommandBuffer(metaDataCommandBuffer);

                                //|||||||||||||||||||||||||||||||||||||| DILATE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //Now before we use the emissive buffer... we have to do additional processing on it before its even usable.
                                //Even if the resolution was crazy high we would get black outlines on the edges of the lightmap UVs which can mess up our emissive results later.
                                //So we will run a dilation filter, which will basically copy pixels around to mitigate those black outlines.

                                //reuse the same buffer, the compute shader will modify the values of this render target.
                                dilate.SetTexture(ComputeShader_Dilation, "Write", meshEmissiveBuffer);

                                //let the GPU perform dilation
                                dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);

                                //now we are finished! so lets store this render texture for later.
                                materialMetaData.emission = meshEmissiveBuffer;
                                materialMetaData.emission.filterMode = FilterMode.Point;
                            }

                            //after rendering both the albedo/emissive lets store the results into our object meta data for the current material that we rendered.
                            //NOTE: its also possible here that there wasn't a meta pass so that means 'materialMetaData' is empty.
                            objectMetaData.materials[j] = materialMetaData;
                        }
                    }

                    //collect the extracted meta data from the current mesh so we can render it later.
                    objectsMetaData.Add(objectMetaData);
                }
            }

            return objectsMetaData;
        }

        /// <summary>
        /// Generates a 3D texture of the scene, renders it with the given "replacementShader", and saves the asset into "filename".
        /// </summary>
        /// <param name="replacementShader"></param>
        /// <param name="filename"></param>
        /// <param name="rtFormat"></param>
        /// <param name="texFormat"></param>
        [ContextMenu("GenerateAlbedoEmissiveBuffers")]
        public void GenerateAlbedoEmissiveBuffers()
        {
            float timeBeforeFunction = Time.realtimeSinceStartup;

            bool getResourcesResult = GetResources(); //Get all of our compute shaders ready.

            if (getResourcesResult == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.
            CreateVoxelCamera(); //Create our voxel camera rig

            int renderTextureDepthBits = 32; //bits for the render texture used by the voxel camera (technically 16 does just fine?)

            voxelCamera.allowMSAA = enableAnitAliasing;

            List<ObjectMetaData> objectsMetaData = BuildMetaObjectBuffers();

            //|||||||||||||||||||||||||||||||||||||| PREPARE SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| PREPARE SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| PREPARE SCENE VOXELIZATION ||||||||||||||||||||||||||||||||||||||

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
            RenderTexture combinedSceneVoxel = new RenderTexture(voxelResolution.x, voxelResolution.y, 0, RenderTextureFormat.ARGBFloat);
            combinedSceneVoxel.dimension = TextureDimension.Tex3D;
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
            voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);

            //run the compute shader once, which will make all pixels float4(0, 0, 0, 0) to clean things up.
            voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));

            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //||||||||||||||||||||||||||||||||| X AXIS SETUP |||||||||||||||||||||||||||||||||
            //captures the scene on the X axis.

            using (CommandBuffer sceneAlbedoCommandBuffer = new CommandBuffer())
            {
                //create a 2D render texture based off our voxel resolution to capture the scene in the X axis.
                RenderTexture voxelCameraSlice = new RenderTexture(voxelResolution.z, voxelResolution.y, renderTextureDepthBits, RenderTextureFormat.ARGBFloat);
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
                    RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData);

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
                    RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt("AxisIndex", i);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
                }

                //release the render texture slice, because we are going to create a new one with new dimensions for the next axis...
                voxelCameraSlice.DiscardContents(true, true);
                voxelCameraSlice.Release();

                /*
                //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| Y AXIS SETUP |||||||||||||||||||||||||||||||||
                //captures the scene on the Y axis.

                //create a 2D render texture based off our voxel resolution to capture the scene in the Y axis.
                voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.z, renderTextureDepthBits, albedoFormat);
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
                    RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData);

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
                    RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt("AxisIndex", i);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
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
                voxelCameraSlice = new RenderTexture(voxelResolution.x, voxelResolution.y, renderTextureDepthBits, albedoFormat);
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
                    RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData);

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
                    RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData);

                    //feed the compute shader the appropriate data, and do a dispatch so it can accumulate the scene slice into a 3D texture.
                    voxelizeScene.SetInt("AxisIndex", i);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "CameraVoxelRender", voxelCameraSlice);
                    voxelizeScene.SetTexture(ComputeShader_VoxelizeScene, "Write", combinedSceneVoxel);
                    voxelizeScene.Dispatch(ComputeShader_VoxelizeScene, Mathf.CeilToInt(voxelResolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(voxelResolution.y / THREAD_GROUP_SIZE_Y), Mathf.CeilToInt(voxelResolution.z / THREAD_GROUP_SIZE_Z));
                }
                */

                //release the render texture slice, because we are done with it...
                voxelCameraSlice.DiscardContents(true, true);
                voxelCameraSlice.Release();
                Debug.Log(string.Format("Rendering took {0} seconds.", Time.realtimeSinceStartup - timeBeforeRendering));

                //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
                //||||||||||||||||||||||||||||||||| RENDER TEXTURE 3D ---> TEXTURE 3D CONVERSION |||||||||||||||||||||||||||||||||
                //final step, save our accumulated 3D texture to the disk.

                //create our object to handle the conversion of the 3D render texture.
                RenderTextureConverter renderTextureConverter = new RenderTextureConverter(slicer, RenderTextureFormat.ARGBFloat, TextureFormat.RGBAFloat);
                Texture3D result = renderTextureConverter.ConvertFromRenderTexture3D(combinedSceneVoxel, true);
                result.filterMode = FilterMode.Point;

                //release the scene voxel render texture, because we are done with it...
                combinedSceneVoxel.DiscardContents(true, true);
                combinedSceneVoxel.Release();

                Debug.Log(string.Format("Generating took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));

                //save the final texture to the disk in our local assets folder under the current scene.
                SaveVolumeTexture(string.Format("{0}_albedo", voxelName), result);
            }

            //get rid of this junk, don't need it no more.
            CleanupVoxelCamera();

            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||

            long memorySize = 0;
            uint textures = 0;

            for (int i = 0; i < objectsMetaData.Count; i++)
            {
                ObjectMetaData objectMetaData = objectsMetaData[i];

                objectMetaData.mesh = null;

                if (objectMetaData.materials != null)
                {
                    for (int j = 0; j < objectMetaData.materials.Length; j++)
                    {
                        //if (objectMetaData.materials[j].isEmpty() == false)
                        //{
                        //memorySize += Profiler.GetRuntimeMemorySizeLong(objectMetaData.materials[j].albedo);
                        //memorySize += Profiler.GetRuntimeMemorySizeLong(objectMetaData.materials[j].emission);

                        //textures += 2;

                        //Debug.Log(string.Format("Meta Texture {0} Resolution: {1}", i + j, objectMetaData.materials[j].albedo.width));
                        //}

                        objectMetaData.materials[j].ReleaseTextures();
                    }
                }

                objectMetaData.materials = null;
            }

            //Debug.Log(string.Format("(FINAL) Meta Textures Amount: {0}", textures));
            //Debug.Log(string.Format("(FINAL) Meta Textures Runtime Memory: {0} MB [{1} B]", Mathf.RoundToInt(memorySize / (1024.0f * 1024.0f)), memorySize));
            Debug.Log(string.Format("Finished At: {0} seconds", Time.realtimeSinceStartupAsDouble - timeBeforeFunction));
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| GIZMOS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.white;
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

        /// <summary>
        /// Gets an array of renderer objects after LOD0 on an LODGroup.
        /// </summary>
        /// <param name="lodGroup"></param>
        /// <returns></returns>
        public static Renderer[] GetRenderersAfterLOD0(LODGroup lodGroup)
        {
            //get LODGroup lods
            LOD[] lods = lodGroup.GetLODs();

            //If there are no LODs...
            //Or there is only one LOD level...
            //Ignore this LODGroup and return nothing (we only want the renderers that are used for the other LOD groups)
            if (lods.Length < 2)
                return null;

            //Initalize a dynamic array list of renderers that will be filled
            List<Renderer> renderers = new List<Renderer>();

            //Skip the first LOD level...
            //And iterate through the rest of the LOD groups to get it's renderers
            for (int i = 1; i < lods.Length; i++)
            {
                for (int j = 0; j < lods[i].renderers.Length; j++)
                {
                    Renderer lodRenderer = lods[i].renderers[j];

                    if (lodRenderer != null)
                        renderers.Add(lodRenderer);
                }
            }

            //If no renderers were found, then return nothing.
            if (renderers.Count <= 0)
                return null;

            return renderers.ToArray();
        }

        /// <summary>
        /// Returns a list of hashes for the given renderer array.
        /// </summary>
        /// <param name="renderers"></param>
        /// <returns></returns>
        public static int[] GetRendererHashCodes(Renderer[] renderers)
        {
            int[] hashCodeArray = new int[renderers.Length];

            for (int i = 0; i < hashCodeArray.Length; i++)
                hashCodeArray[i] = renderers[i].GetHashCode();

            return hashCodeArray;
        }

        /// <summary>
        /// Returns a hash code array of renderers found after LOD0 in a given LOD group.
        /// </summary>
        /// <param name="lodGroup"></param>
        /// <returns></returns>
        public static int[] GetRendererHashCodesAfterLOD0(LODGroup lodGroup)
        {
            Renderer[] renderers = GetRenderersAfterLOD0(lodGroup);

            if (renderers == null || renderers.Length <= 1)
                return null;
            else
                return GetRendererHashCodes(renderers);
        }
    }
}