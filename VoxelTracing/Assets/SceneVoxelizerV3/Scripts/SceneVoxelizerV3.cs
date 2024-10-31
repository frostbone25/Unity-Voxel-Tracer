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
        private string localAssetComputeFolder = "Assets/SceneVoxelizerV3/ComputeShaders";
        private string localAssetDataFolder = "Assets/SceneVoxelizerV3/Data";
        private string voxelizeSceneAssetPath => localAssetComputeFolder + "/VoxelizeScene.compute";
        private string dilateAssetPath => localAssetComputeFolder + "/Dilation.compute";
        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();
        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;

        private ComputeShader voxelizeScene => AssetDatabase.LoadAssetAtPath<ComputeShader>(voxelizeSceneAssetPath);
        private ComputeShader dilate => AssetDatabase.LoadAssetAtPath<ComputeShader>(dilateAssetPath);
        private Shader voxelBufferMeta => Shader.Find("SceneVoxelizerV3/VoxelBufferMeta");
        private Shader voxelBufferMetaNormal => Shader.Find("SceneVoxelizerV3/VoxelBufferMetaNormal");

        private static RenderTextureFormat metaAlbedoFormat = RenderTextureFormat.ARGB4444;
        private static RenderTextureFormat metaEmissiveFormat = RenderTextureFormat.ARGBHalf;
        private static RenderTextureFormat metaNormalFormat = RenderTextureFormat.ARGB32;
        private static RenderTextureFormat voxelBufferFormat = RenderTextureFormat.ARGBHalf;

        private static TextureFormat voxelAlbedoBufferFormat = TextureFormat.RGBA32;
        private static TextureFormat voxelEmissiveBufferFormat = TextureFormat.RGBAHalf;
        private static TextureFormat voxelNormalBufferFormat = TextureFormat.RGBA32;

        private static TextureWrapMode metaTextureWrapMode = TextureWrapMode.Clamp;

        private FilterMode metaTextureFilterMode => useBilinearFiltering ? FilterMode.Bilinear : FilterMode.Point;

        private RenderTextureConverterV2 renderTextureConverterV2 => new RenderTextureConverterV2();

        private GameObject voxelCameraGameObject;
        private Camera voxelCamera;

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
            else if (dilate == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", dilateAssetPath));
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

        public void GenerateVolumes()
        {
            float timeBeforeFunction = Time.realtimeSinceStartup;

            //NOTE TO SELF: Keep render texture format high precision.
            //For instance changing it to an 8 bit for the albedo buffer seems to kills color definition.

            GenerateAlbedoEmissiveNormalBuffers();

            Debug.Log(string.Format("Generating Albedo / Emissive / Normal buffers took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
        }

        public void RenderScene(CommandBuffer commandBuffer, RenderTexture renderTexture, Camera camera, List<ObjectMetaData> objectsMetaData, Material objectMaterial, int renderPassType)
        {
            //calculate the view matrix of the camera that we are using to render the scene with.
            Matrix4x4 lookMatrix = Matrix4x4.LookAt(camera.transform.position, camera.transform.position + camera.transform.forward, camera.transform.up);
            Matrix4x4 scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
            Matrix4x4 viewMatrix = scaleMatrix * lookMatrix.inverse;

            //make the render target active, and setup projection
            commandBuffer.SetRenderTarget(renderTexture);
            commandBuffer.SetViewProjectionMatrices(viewMatrix, camera.projectionMatrix);
            commandBuffer.SetViewport(new Rect(0, 0, renderTexture.width, renderTexture.height));
            commandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame

            MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
            materialPropertyBlock.SetVector(ShaderIDs.unity_LightmapST, new Vector4(1, 1, 0, 0)); //cancel out any lightmap UV scaling/offsets.

            //get camera frustum planes
            Plane[] cameraFrustumPlanes = GeometryUtility.CalculateFrustumPlanes(camera);

            //iterate through each object we collected
            for (int i = 0; i < objectsMetaData.Count; i++)
            {
                ObjectMetaData objectMetaData = objectsMetaData[i];

                //(IF ENABLED) calculate camera frustum culling during this instance of rendering
                if(useBoundingBoxCullingForRendering)
                {
                    //test the extracted object bounds against the planes, if the object is NOT within the camera frustum planes...
                    if (!GeometryUtility.TestPlanesAABB(cameraFrustumPlanes, objectMetaData.bounds))
                        continue; //then continue to the next object to render, no reason to keep fucking around with it because we won't see it
                }

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

                            //feed it our buffer
                            switch(renderPassType)
                            {
                                case 0:
                                    materialPropertyBlock.SetTexture(ShaderIDs._MainTex, materialMetaData.albedoBuffer);
                                    break;
                                case 1:
                                    materialPropertyBlock.SetTexture(ShaderIDs._MainTex, materialMetaData.emissiveBuffer);
                                    break;
                                case 2:
                                    materialPropertyBlock.SetTexture(ShaderIDs._MainTex, materialMetaData.normalBuffer);
                                    break;
                            }

                            //draw the mesh in the scene, rendering only its packed buffer colors.
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
            float timeBeforeFunction = Time.realtimeSinceStartup;

            //|||||||||||||||||||||||||||||||||||||| RENDERING VARIABLES SETUP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDERING VARIABLES SETUP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDERING VARIABLES SETUP ||||||||||||||||||||||||||||||||||||||

            //Property values used in the "META" pass in unity shaders.
            //The "META" pass is used during lightmapping to extract albedo/emission colors from materials in a scene.
            //Which is exactly what we need!
            MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
            materialPropertyBlock.SetVector(ShaderIDs.unity_MetaVertexControl, new Vector4(1, 0, 0, 0)); //Only Lightmap UVs
            materialPropertyBlock.SetFloat(ShaderIDs.unity_OneOverOutputBoost, 1.0f);
            materialPropertyBlock.SetFloat(ShaderIDs.unity_MaxOutputValue, 0.97f);
            materialPropertyBlock.SetInt(ShaderIDs.unity_UseLinearSpace, QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
            materialPropertyBlock.SetVector(ShaderIDs.unity_LightmapST, new Vector4(1, 1, 0, 0)); //Cancel out lightmapping scale/offset values if its already lightmapped.

            //Create a projection matrix, mapped to UV space [0,1]
            Matrix4x4 uvProjection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(0, 1, 1, 0, -50, 50), true);

            //fetch our dilation function kernel in the compute shader
            int ComputeShader_Dilation2D = dilate.FindKernel("ComputeShader_Dilation2D");

            //set the amount of dilation steps it will take
            dilate.SetInt("KernelSize", dilationPixelSize);

            Material meshNormalMaterial = new Material(voxelBufferMetaNormal);

            //|||||||||||||||||||||||||||||||||||||| CREATE ALBEDO/EMISSION BUFFERS FOR EACH MESH ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CREATE ALBEDO/EMISSION BUFFERS FOR EACH MESH ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CREATE ALBEDO/EMISSION BUFFERS FOR EACH MESH ||||||||||||||||||||||||||||||||||||||

            //fetch all mesh renderers in the scene.
            MeshRenderer[] meshRenderers = FindObjectsOfType<MeshRenderer>();

            //initalize a dynamic array of object meta data that will be filled up.
            List<ObjectMetaData> objectsMetaData = new List<ObjectMetaData>();

            //iterate through each mesh renderer in the scene
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                //current mesh renderer element
                MeshRenderer meshRenderer = meshRenderers[i];

                //get the mesh filter component so we can grab the actual mesh for drawing later.
                MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();

                //conditional boolean that will determine if we use the mesh or not.
                bool includeMesh = true;

                //(IF ENABLED) If we only want to include meshes that contribute to GI, saving us some additional computation
                if (onlyUseGIContributors)
                    includeMesh = includeMesh && GameObjectUtility.GetStaticEditorFlags(meshRenderer.gameObject).HasFlag(StaticEditorFlags.ContributeGI);

                //(IF ENABLED) If we only want to include meshes that do shadowcasting, saving us from more computation
                if (onlyUseShadowCasters)
                    includeMesh = includeMesh && (meshRenderer.shadowCastingMode == ShadowCastingMode.On || meshRenderer.shadowCastingMode != ShadowCastingMode.TwoSided);

                //(IF ENABLED) Only include meshes within voxelization bounds, saving us hopefully from additional computation
                if (onlyUseMeshesWithinBounds)
                    includeMesh = includeMesh && ContainBounds(voxelBounds, meshRenderer.bounds);

                bool isMeshLayerValid = objectLayerMask == (objectLayerMask | (1 << meshFilter.gameObject.layer));

                //compute texel density for each mesh renderer
                int objectTextureResolutionSquare = (int)(meshRenderer.bounds.size.magnitude * texelDensityPerUnit);

                //if it ends up being too low resolution just use the minimum resolution.
                objectTextureResolutionSquare = Mathf.Max(minimumBufferResolution, objectTextureResolutionSquare);

                //If there is a mesh filter, and we can include the mesh then lets get started!
                if (meshFilter != null && includeMesh && isMeshLayerValid)
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

                    //Create a command buffer so we can render the albedo/emissive buffers of each object.
                    using (CommandBuffer metaDataCommandBuffer = new CommandBuffer())
                    {
                        //setup projection
                        metaDataCommandBuffer.SetViewProjectionMatrices(Matrix4x4.identity, uvProjection);
                        metaDataCommandBuffer.SetViewport(new Rect(0, 0, objectTextureResolutionSquare, objectTextureResolutionSquare));
                        metaDataCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame

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
                                //|||||||||||||||||||||||||||||||||||||| ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                                //here we will render the albedo buffer of the object.
                                //the unity meta pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                                //create our albedo render texture buffer
                                materialMetaData.albedoBuffer = new RenderTexture(objectTextureResolutionSquare, objectTextureResolutionSquare, 32, metaAlbedoFormat);
                                materialMetaData.albedoBuffer.wrapMode = metaTextureWrapMode;
                                materialMetaData.albedoBuffer.filterMode = metaTextureFilterMode;
                                materialMetaData.albedoBuffer.enableRandomWrite = true; //important
                                materialMetaData.albedoBuffer.Create();

                                //put our render texture to use.
                                metaDataCommandBuffer.SetRenderTarget(materialMetaData.albedoBuffer);

                                //show only the albedo colors in the meta pass.
                                materialPropertyBlock.SetVector(ShaderIDs.unity_MetaFragmentControl, new Vector4(1, 0, 0, 0)); //Show Albedo

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

                                if(performDilation)
                                {
                                    //reuse the same buffer, the compute shader will modify the values of this render target.
                                    dilate.SetTexture(ComputeShader_Dilation2D, ShaderIDs.Write2D, materialMetaData.albedoBuffer);

                                    //let the GPU perform dilation
                                    dilate.Dispatch(ComputeShader_Dilation2D, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);
                                }

                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //here we will render the emissive buffer of the object.
                                //the unity meta pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                                //create our emissive render texture buffer
                                materialMetaData.emissiveBuffer = new RenderTexture(objectTextureResolutionSquare, objectTextureResolutionSquare, 32, metaEmissiveFormat);
                                materialMetaData.emissiveBuffer.wrapMode = metaTextureWrapMode;
                                materialMetaData.emissiveBuffer.filterMode = metaTextureFilterMode;
                                materialMetaData.emissiveBuffer.enableRandomWrite = true;
                                materialMetaData.emissiveBuffer.Create();

                                //put our render texture to use.
                                metaDataCommandBuffer.SetRenderTarget(materialMetaData.emissiveBuffer);

                                //show only the emissive colors in the meta pass.
                                materialPropertyBlock.SetVector(ShaderIDs.unity_MetaFragmentControl, new Vector4(0, 1, 0, 0)); //Show Emission

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

                                if (performDilation)
                                {
                                    //reuse the same buffer, the compute shader will modify the values of this render target.
                                    dilate.SetTexture(ComputeShader_Dilation2D, ShaderIDs.Write2D, materialMetaData.emissiveBuffer);

                                    //let the GPU perform dilation
                                    dilate.Dispatch(ComputeShader_Dilation2D, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);
                                }

                                //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //here we will render the normal buffer of the object.
                                //the unity meta pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                                materialMetaData.normalBuffer = new RenderTexture(objectTextureResolutionSquare, objectTextureResolutionSquare, 32, metaNormalFormat);
                                materialMetaData.normalBuffer.wrapMode = metaTextureWrapMode;
                                materialMetaData.normalBuffer.filterMode = metaTextureFilterMode;
                                materialMetaData.normalBuffer.enableRandomWrite = true;
                                materialMetaData.normalBuffer.Create();

                                metaDataCommandBuffer.SetRenderTarget(materialMetaData.normalBuffer);

                                metaDataCommandBuffer.DrawMesh(mesh, Matrix4x4.identity, meshNormalMaterial, submeshIndex, 0, null);

                                Graphics.ExecuteCommandBuffer(metaDataCommandBuffer);

                                //|||||||||||||||||||||||||||||||||||||| DILATE NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //Now before we use the normal buffer... we have to do additional processing on it before its even usable.
                                //Even if the resolution was crazy high we would get black outlines on the edges of the lightmap UVs which can mess up our normal results later.
                                //So we will run a dilation filter, which will basically copy pixels around to mitigate those black outlines.

                                if (performDilation)
                                {
                                    //reuse the same buffer, the compute shader will modify the values of this render target.
                                    dilate.SetTexture(ComputeShader_Dilation2D, ShaderIDs.Write2D, materialMetaData.normalBuffer);

                                    //let the GPU perform dilation
                                    dilate.Dispatch(ComputeShader_Dilation2D, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);
                                }
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

            //|||||||||||||||||||||||||||||||||||||| (DEBUG) LOG META DATA MEMORY USAGE ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| (DEBUG) LOG META DATA MEMORY USAGE ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| (DEBUG) LOG META DATA MEMORY USAGE ||||||||||||||||||||||||||||||||||||||

            long memorySize = 0;
            uint textures = 0;

            for (int i = 0; i < objectsMetaData.Count; i++)
            {
                memorySize += objectsMetaData[i].GetDebugMemorySize();
                textures += (uint)(objectsMetaData[i].materials.Length * 3);
            }

            Debug.Log(string.Format("Meta Pass Extraction took {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));
            Debug.Log(string.Format("Meta Textures {0} | Total Runtime Memory: {1} MB [{2} B]", textures, memorySize / (1024.0f * 1024.0f), memorySize));

            return objectsMetaData;
        }

        /// <summary>
        /// Generates a 3D texture of the scene, renders it with the given "replacementShader", and saves the asset into "filename".
        /// </summary>
        /// <param name="replacementShader"></param>
        /// <param name="filename"></param>
        /// <param name="rtFormat"></param>
        /// <param name="texFormat"></param>
        [ContextMenu("GenerateAlbedoEmissiveNormalBuffers")]
        public void GenerateAlbedoEmissiveNormalBuffers()
        {
            UpdateProgressBar("Preparing to generate albedo/emissive/normal...", 0.5f);

            float timeBeforeFunction = Time.realtimeSinceStartup;

            if (HasResources() == false)
                return; //if both resource gathering functions returned false, that means something failed so don't continue

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.
            CreateVoxelCamera(); //Create our voxel camera rig

            int renderTextureDepthBits = 32; //bits for the render texture used by the voxel camera (technically 16 does just fine?)

            UpdateProgressBar("Building object meta buffers...", 0.5f);

            List<ObjectMetaData> objectsMetaData = BuildMetaObjectBuffers();

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

            Material objectMaterial = new Material(voxelBufferMeta);
            objectMaterial.SetInt("_CullMode", doubleSidedGeometry ? (int)CullMode.Off : (int)CullMode.Back);

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
                        RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData, objectMaterial, j);

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
                        RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData, objectMaterial, j);

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
                        RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData, objectMaterial, j);

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
                        RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData, objectMaterial, j);

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
                        RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData, objectMaterial, j);

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
                        RenderScene(sceneAlbedoCommandBuffer, voxelCameraSlice, voxelCamera, objectsMetaData, objectMaterial, j);

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

            float timeBeforeCleanUp = Time.realtimeSinceStartup;

            for (int i = 0; i < objectsMetaData.Count; i++)
                objectsMetaData[i].CleanUp();

            Debug.Log(string.Format("Clean Up took {0} seconds.", Time.realtimeSinceStartup - timeBeforeCleanUp));
            Debug.Log(string.Format("Total Function Time: {0} seconds.", Time.realtimeSinceStartup - timeBeforeFunction));

            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| LOD FUNCTIONS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| LOD FUNCTIONS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| LOD FUNCTIONS ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

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

        public static bool ContainBounds(Bounds bounds, Bounds target) => bounds.Contains(target.center) || bounds.Contains(target.min) || bounds.Contains(target.max);
    }
}