using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityVoxelTracer;
using UnityEngineInternal;
using UnityEngine.Rendering;
using System;
using UnityEngine.Profiling;
using UnityEngine.XR;
using System.Linq;
using RenderTextureConverting;
using UnityEditor.SceneManagement;

namespace CameraMetaPass1
{

    [ExecuteInEditMode]
    public class CameraMetaPassV1 : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        [Header("Rendering Properties")]
        public Vector2Int resolution = new Vector2Int(1920, 1080);

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

        public int colorDilationPixelSize = 128;
        public int alphaDilationPixelSize = 1;

        //public bool doubleSidedGeometry = true;

        [Header("Optimizations")]
        //this will only use mesh renderers that are marked "Contribute Global Illumination".
        //ENABLED: this will only use meshes in the scene marked for GI | faster voxelization | less memory usage (less objects needing meta textures)
        //DISABLED: every mesh renderer in the scene will be used | slower voxelization | more memory usage (more objects needing meta textures)
        public bool onlyUseGIContributors = true;

        //this will only use mesh renderers that have shadow casting enabled.
        //ENABLED: this will only use meshes in the scene marked for GI | faster voxelization | less memory usage (less objects needing meta textures)
        //DISABLED: every mesh renderer in the scene will be used | slower voxelization | more memory usage (more objects needing meta textures)
        public bool onlyUseShadowCasters = true;

        //use the bounding boxes on meshes during "voxelization" to render only what is visible
        //ENABLED: renders objects only visible in each voxel slice | much faster voxelization
        //DISABLED: renders all objects | much slower voxelization |
        public bool useBoundingBoxCullingForRendering = true;

        //only use objects that match the layer mask requirements
        public LayerMask objectLayerMask = 1;

        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        //Size of the thread groups for compute shaders.
        //These values should match the #define ones in the compute shaders.
        private static int THREAD_GROUP_SIZE_X = 8;
        private static int THREAD_GROUP_SIZE_Y = 8;

        private static string localAssetFolder = "Assets/CameraMetaPassV1";
        private static string localAssetComputeFolder = "Assets/CameraMetaPassV1/ComputeShaders";
        private static string localAssetDataFolder = "Assets/CameraMetaPassV1/Data";

        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();
        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;
        private string dilateAssetPath => localAssetComputeFolder + "/Dilation.compute";

        private static RenderTextureFormat metaAlbedoFormat = RenderTextureFormat.ARGB32;
        private static RenderTextureFormat metaEmissiveFormat = RenderTextureFormat.ARGBHalf;
        private static RenderTextureFormat metaNormalFormat = RenderTextureFormat.ARGB32;
        private static RenderTextureFormat sceneRenderFormat = RenderTextureFormat.ARGB32;

        private ComputeShader dilate => AssetDatabase.LoadAssetAtPath<ComputeShader>(dilateAssetPath);

        private Shader voxelBufferMeta => Shader.Find("CameraMetaPassV1/VoxelBufferMeta");
        private Shader voxelBufferMetaNormal => Shader.Find("CameraMetaPassV1/VoxelBufferMetaNormal");

        private Camera camera => GetComponent<Camera>();

        private RenderTextureConverterV2 renderTextureConverter => new RenderTextureConverterV2();

        /// <summary>
        /// Load in necessary resources.
        /// </summary>
        private bool HasResources()
        {
            if (dilate == null)
            {
                Debug.LogError(string.Format("{0} does not exist!", dilateAssetPath));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sets up the local asset directory to store the generated files.
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

            //fetch all mesh renderers in the scene.
            MeshRenderer[] meshRenderers = FindObjectsOfType<MeshRenderer>();

            //initalize a dynamic array of object meta data that will be filled up.
            List<ObjectMetaData> objectsMetaData = new List<ObjectMetaData>();

            Material meshNormalMaterial = new Material(voxelBufferMetaNormal);

            //Property values used in the "META" pass in unity shaders.
            //The "META" pass is used during lightmapping to extract albedo/emission colors from materials in a scene.
            //Which is exactly what we need!
            MaterialPropertyBlock materialPropertyBlockMeta = new MaterialPropertyBlock();
            materialPropertyBlockMeta.SetVector("unity_MetaVertexControl", new Vector4(1, 0, 0, 0)); //Only Lightmap UVs
            materialPropertyBlockMeta.SetFloat("unity_OneOverOutputBoost", 1.0f);
            materialPropertyBlockMeta.SetFloat("unity_MaxOutputValue", 0.97f);
            materialPropertyBlockMeta.SetInt("unity_UseLinearSpace", QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
            materialPropertyBlockMeta.SetVector("unity_LightmapST", new Vector4(1, 1, 0, 0)); //Cancel out lightmapping scale/offset values if its already lightmapped.

            //Create a projection matrix, mapped to UV space [0,1]
            Matrix4x4 uvProjection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(0, 1, 1, 0, -50, 50), true);

            //fetch our dilation function kernel in the compute shader
            int ComputeShader_Dilation = dilate.FindKernel("ComputeShader_Dilation");

            //set the amount of dilation steps it will take
            dilate.SetInt("KernelSize", dilationPixelSize);

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

                //conditional boolean that will determine if we use the mesh or not.
                bool includeMesh = true;

                //(IF ENABLED) If we only want to include meshes that contribute to GI, saving us some additional computation
                if (onlyUseGIContributors)
                    includeMesh = includeMesh && GameObjectUtility.GetStaticEditorFlags(meshRenderer.gameObject).HasFlag(StaticEditorFlags.ContributeGI);

                //(IF ENABLED) If we only want to include meshes that do shadowcasting, saving us from more computation
                if (onlyUseShadowCasters)
                    includeMesh = includeMesh && (meshRenderer.shadowCastingMode == ShadowCastingMode.On || meshRenderer.shadowCastingMode != ShadowCastingMode.TwoSided);

                bool isMeshLayerValid = objectLayerMask == (objectLayerMask | (1 << meshFilter.gameObject.layer));

                //compute texel density for each mesh renderer
                int objectTextureResolutionSquare = (int)(meshRenderer.bounds.size.magnitude * texelDensityPerUnit);

                //if it ends up being too low resolution just use the minimum resolution.
                objectTextureResolutionSquare = Math.Max(minimumBufferResolution, objectTextureResolutionSquare);

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
                                materialMetaData.albedoBuffer.filterMode = FilterMode.Point;
                                materialMetaData.albedoBuffer.enableRandomWrite = true; //important
                                materialMetaData.albedoBuffer.Create();

                                //put our render texture to use.
                                metaDataCommandBuffer.SetRenderTarget(materialMetaData.albedoBuffer);

                                //show only the albedo colors in the meta pass.
                                materialPropertyBlockMeta.SetVector("unity_MetaFragmentControl", new Vector4(1, 0, 0, 0)); //Show Albedo

                                //queue a draw mesh command, only rendering the meta pass on our material.
                                metaDataCommandBuffer.DrawMesh(mesh, Matrix4x4.identity, material, submeshIndex, metaPassIndex, materialPropertyBlockMeta);

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
                                    dilate.SetTexture(ComputeShader_Dilation, "Write", materialMetaData.albedoBuffer);

                                    //let the GPU perform dilation
                                    dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);
                                }

                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //here we will render the emissive buffer of the object.
                                //the unity meta pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                                //create our emissive render texture buffer
                                materialMetaData.emissiveBuffer = new RenderTexture(objectTextureResolutionSquare, objectTextureResolutionSquare, 32, metaEmissiveFormat);
                                materialMetaData.emissiveBuffer.filterMode = FilterMode.Point;
                                materialMetaData.emissiveBuffer.enableRandomWrite = true;
                                materialMetaData.emissiveBuffer.Create();

                                //put our render texture to use.
                                metaDataCommandBuffer.SetRenderTarget(materialMetaData.emissiveBuffer);

                                //show only the emissive colors in the meta pass.
                                materialPropertyBlockMeta.SetVector("unity_MetaFragmentControl", new Vector4(0, 1, 0, 0)); //Show Emission

                                //queue a draw mesh command, only rendering the meta pass on our material.
                                metaDataCommandBuffer.DrawMesh(mesh, Matrix4x4.identity, material, submeshIndex, metaPassIndex, materialPropertyBlockMeta);

                                //actually renders our emissive buffer to the render target.
                                Graphics.ExecuteCommandBuffer(metaDataCommandBuffer);
                                //metaDataCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame

                                //|||||||||||||||||||||||||||||||||||||| DILATE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| DILATE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                                //Now before we use the emissive buffer... we have to do additional processing on it before its even usable.
                                //Even if the resolution was crazy high we would get black outlines on the edges of the lightmap UVs which can mess up our emissive results later.
                                //So we will run a dilation filter, which will basically copy pixels around to mitigate those black outlines.

                                if (performDilation)
                                {
                                    //reuse the same buffer, the compute shader will modify the values of this render target.
                                    dilate.SetTexture(ComputeShader_Dilation, "Write", materialMetaData.emissiveBuffer);

                                    //let the GPU perform dilation
                                    dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);
                                }

                                //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                                //here we will render the normal buffer of the object.
                                //the this custom shader pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                                //create our normal render texture buffer
                                materialMetaData.normalBuffer = new RenderTexture(objectTextureResolutionSquare, objectTextureResolutionSquare, 32, metaNormalFormat);
                                materialMetaData.normalBuffer.filterMode = FilterMode.Point;
                                materialMetaData.normalBuffer.enableRandomWrite = true;
                                materialMetaData.normalBuffer.Create();

                                //put our render texture to use.
                                metaDataCommandBuffer.SetRenderTarget(materialMetaData.normalBuffer);

                                //queue a draw mesh command, rendering the first pass on our material.
                                metaDataCommandBuffer.DrawMesh(mesh, Matrix4x4.identity, meshNormalMaterial, submeshIndex, 0, null);

                                //actually renders our normal buffer to the render target.
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
                                    dilate.SetTexture(ComputeShader_Dilation, "Write", materialMetaData.normalBuffer);

                                    //let the GPU perform dilation
                                    dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(objectTextureResolutionSquare / THREAD_GROUP_SIZE_Y), 1);
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

            Debug.Log(string.Format("Meta Textures {0} | Total Runtime Memory: {1} MB [{2} B]", textures, Mathf.RoundToInt(memorySize / (1024.0f * 1024.0f)), memorySize));

            return objectsMetaData;
        }


        [ContextMenu("RenderSceneMetaPassForCamera")]
        public void RenderAllMeshesNormally()
        {
            //check if we have the necessary resources to continue, if we don't then we can't
            if (HasResources() == false)
                return;

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.

            double timeBeforeFunction = Time.realtimeSinceStartupAsDouble;

            //|||||||||||||||||||||||||||||||||||||| BUILD SCENE META BUFFERS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| BUILD SCENE META BUFFERS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| BUILD SCENE META BUFFERS ||||||||||||||||||||||||||||||||||||||

            UpdateProgressBar("Building Scene Meta Buffer...", 0.5f);

            List<ObjectMetaData> objectsMetaData = BuildMetaObjectBuffers();

            //|||||||||||||||||||||||||||||||||||||| RENDER SCENE BUFFERS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER SCENE BUFFERS ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| RENDER SCENE BUFFERS ||||||||||||||||||||||||||||||||||||||

            UpdateProgressBar("Rendering Scene Buffers...", 0.5f);

            //create a render target that we will render the scene into.
            RenderTexture sceneBuffer = new RenderTexture(resolution.x, resolution.y, 32, sceneRenderFormat);
            sceneBuffer.filterMode = FilterMode.Point;
            sceneBuffer.enableRandomWrite = true;
            sceneBuffer.Create();

            //get camera frustum planes
            Plane[] cameraFrustumPlanes = GeometryUtility.CalculateFrustumPlanes(camera);

            using (CommandBuffer sceneCommandBuffer = new CommandBuffer())
            {
                //calculate the view matrix of the camera that we are using to render the scene with.
                Matrix4x4 lookMatrix = Matrix4x4.LookAt(camera.transform.position, camera.transform.position + camera.transform.forward, camera.transform.up);
                Matrix4x4 scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
                Matrix4x4 viewMatrix = scaleMatrix * lookMatrix.inverse;

                //make the render target active, and setup projection
                sceneCommandBuffer.SetRenderTarget(sceneBuffer);
                sceneCommandBuffer.SetViewProjectionMatrices(viewMatrix, camera.projectionMatrix);
                sceneCommandBuffer.SetViewport(new Rect(0, 0, sceneBuffer.width, sceneBuffer.height));
                sceneCommandBuffer.ClearRenderTarget(true, true, Color.clear); //IMPORTANT: clear contents before we render a new frame

                //create a custom material with a custom shader that will only show the buffers we feed it.
                Material objectMaterial = new Material(voxelBufferMeta);

                MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
                materialPropertyBlock.SetVector("unity_LightmapST", new Vector4(1, 1, 0, 0)); //cancel out any lightmap UV scaling/offsets.

                for (int x = 0; x < 3; x++)
                {
                    //iterate through each object we collected
                    for (int i = 0; i < objectsMetaData.Count; i++)
                    {
                        ObjectMetaData objectMetaData = objectsMetaData[i];

                        //(IF ENABLED) calculate camera frustum culling during this instance of rendering
                        if (useBoundingBoxCullingForRendering)
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

                                    //feed it our albedo buffer
                                    switch (x)
                                    {
                                        case 0:
                                            materialPropertyBlock.SetTexture("_MainTex", materialMetaData.albedoBuffer);
                                            break;
                                        case 1:
                                            materialPropertyBlock.SetTexture("_MainTex", materialMetaData.emissiveBuffer);
                                            break;
                                        case 2:
                                            materialPropertyBlock.SetTexture("_MainTex", materialMetaData.normalBuffer);
                                            break;
                                    }

                                    //draw the mesh in the scene, rendering only its raw albedo colors.
                                    sceneCommandBuffer.DrawMesh(objectMetaData.mesh, objectMetaData.transformMatrix, objectMaterial, submeshIndex, 0, materialPropertyBlock);
                                }
                            }
                        }
                    }

                    //actually renders the scene.
                    Graphics.ExecuteCommandBuffer(sceneCommandBuffer);

                    switch (x)
                    {
                        case 0:
                            renderTextureConverter.SaveRenderTexture2DAsTexture2D(sceneBuffer, string.Format("{0}/SceneAlbedo.asset", localAssetSceneDataFolder));
                            break;
                        case 1:
                            renderTextureConverter.SaveRenderTexture2DAsTexture2D(sceneBuffer, string.Format("{0}/SceneEmissive.asset", localAssetSceneDataFolder));
                            break;
                        case 2:
                            renderTextureConverter.SaveRenderTexture2DAsTexture2D(sceneBuffer, string.Format("{0}/SceneNormal.asset", localAssetSceneDataFolder));
                            break;
                    }
                }
            }

            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||

            for (int i = 0; i < objectsMetaData.Count; i++)
                objectsMetaData[i].CleanUp();

            Debug.Log(string.Format("Finished At: {0} seconds", Time.realtimeSinceStartupAsDouble - timeBeforeFunction));

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

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        public void UpdateProgressBar(string description, float progress) => EditorUtility.DisplayProgressBar("Camera Meta Pass V1", description, progress);

        public void CloseProgressBar() => EditorUtility.ClearProgressBar();
    }
}