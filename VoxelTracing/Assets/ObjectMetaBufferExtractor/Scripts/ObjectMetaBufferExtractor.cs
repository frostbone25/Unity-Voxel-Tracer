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

namespace ObjectMetaBufferExtractor
{
    [ExecuteInEditMode]
    public class ObjectMetaBufferExtractor : MonoBehaviour
    {
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        [Header("Rendering Properties")]
        public Vector2Int resolution = new Vector2Int(1024, 1024);

        [Header("Meta Pass Properties")]
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

        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||

        //Size of the thread groups for compute shaders.
        //These values should match the #define ones in the compute shaders.
        private static int THREAD_GROUP_SIZE_X = 8;
        private static int THREAD_GROUP_SIZE_Y = 8;

        private static string localAssetFolder = "Assets/ObjectMetaBufferExtractor";
        private static string localAssetShadersFolder = "Assets/ObjectMetaBufferExtractor/Shaders";
        private static string localAssetDataFolder = "Assets/ObjectMetaBufferExtractor/Data";
        private string dilateAssetPath => localAssetShadersFolder + "/Dilation.compute";
        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();
        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;

        private static RenderTextureFormat metaAlbedoFormat = RenderTextureFormat.ARGB32;
        private static RenderTextureFormat metaEmissiveFormat = RenderTextureFormat.ARGBHalf;
        private static RenderTextureFormat metaNormalFormat = RenderTextureFormat.ARGB32;

        private ComputeShader dilate => AssetDatabase.LoadAssetAtPath<ComputeShader>(dilateAssetPath);
        private Shader objectNormalBuffer => Shader.Find("ObjectMetaBufferExtractor/ObjectNormalBuffer");

        private MeshRenderer meshRenderer => GetComponent<MeshRenderer>();
        private MeshFilter meshFilter => GetComponent<MeshFilter>();

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

        [ContextMenu("ExtractMetaFromObject")]
        public void ExtractMetaFromObject()
        {
            if (meshRenderer == null || meshFilter == null)
                return;

            //check if we have the necessary resources to continue, if we don't then we can't
            if (HasResources() == false)
                return;

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.

            double timeBeforeFunction = Time.realtimeSinceStartupAsDouble;

            UpdateProgressBar("Extracting Meta Buffers From Object...", 0.5f);

            Material meshNormalMaterial = new Material(objectNormalBuffer);

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

            //get the mesh and it's materials
            Material[] materials = meshRenderer.sharedMaterials;

            //lets create our object meta data now so we can store some of this data later.
            ObjectMetaData objectMetaData = new ObjectMetaData()
            {
                mesh = meshFilter.sharedMesh,
                bounds = meshRenderer.bounds,
                transformMatrix = meshRenderer.transform.localToWorldMatrix,
                materials = new MaterialMetaData[materials.Length]
            };

            //Create a command buffer so we can render the albedo/emissive buffers of each object.
            using (CommandBuffer metaDataCommandBuffer = new CommandBuffer())
            {
                //setup projection
                metaDataCommandBuffer.SetViewProjectionMatrices(Matrix4x4.identity, uvProjection);
                metaDataCommandBuffer.SetViewport(new Rect(0, 0, resolution.x, resolution.y));
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
                        materialMetaData.albedoBuffer = new RenderTexture(resolution.x, resolution.y, 32, metaAlbedoFormat);
                        materialMetaData.albedoBuffer.filterMode = FilterMode.Point;
                        materialMetaData.albedoBuffer.enableRandomWrite = true; //important
                        materialMetaData.albedoBuffer.Create();

                        //put our render texture to use.
                        metaDataCommandBuffer.SetRenderTarget(materialMetaData.albedoBuffer);

                        //show only the albedo colors in the meta pass.
                        materialPropertyBlockMeta.SetVector("unity_MetaFragmentControl", new Vector4(1, 0, 0, 0)); //Show Albedo

                        //queue a draw mesh command, only rendering the meta pass on our material.
                        metaDataCommandBuffer.DrawMesh(objectMetaData.mesh, Matrix4x4.identity, material, submeshIndex, metaPassIndex, materialPropertyBlockMeta);

                        //actually renders our albedo buffer to the render target.
                        Graphics.ExecuteCommandBuffer(metaDataCommandBuffer);

                        //|||||||||||||||||||||||||||||||||||||| DILATE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                        //|||||||||||||||||||||||||||||||||||||| DILATE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                        //|||||||||||||||||||||||||||||||||||||| DILATE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
                        //Now before we use the albedo buffer... we have to do additional processing on it before its even usable.
                        //Even if the resolution was crazy high we would get black outlines on the edges of the lightmap UVs which can mess up our albedo results later.
                        //So we will run a dilation filter, which will basically copy pixels around to mitigate those black outlines.

                        if (performDilation)
                        {
                            //reuse the same buffer, the compute shader will modify the values of this render target.
                            dilate.SetTexture(ComputeShader_Dilation, "Write", materialMetaData.albedoBuffer);

                            //let the GPU perform dilation
                            dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(resolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(resolution.y / THREAD_GROUP_SIZE_Y), 1);
                        }

                        //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                        //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                        //|||||||||||||||||||||||||||||||||||||| EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
                        //here we will render the emissive buffer of the object.
                        //the unity meta pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                        //create our emissive render texture buffer
                        materialMetaData.emissiveBuffer = new RenderTexture(resolution.x, resolution.y, 32, metaEmissiveFormat);
                        materialMetaData.emissiveBuffer.filterMode = FilterMode.Point;
                        materialMetaData.emissiveBuffer.enableRandomWrite = true;
                        materialMetaData.emissiveBuffer.Create();

                        //put our render texture to use.
                        metaDataCommandBuffer.SetRenderTarget(materialMetaData.emissiveBuffer);

                        //show only the emissive colors in the meta pass.
                        materialPropertyBlockMeta.SetVector("unity_MetaFragmentControl", new Vector4(0, 1, 0, 0)); //Show Emission

                        //queue a draw mesh command, only rendering the meta pass on our material.
                        metaDataCommandBuffer.DrawMesh(objectMetaData.mesh, Matrix4x4.identity, material, submeshIndex, metaPassIndex, materialPropertyBlockMeta);

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
                            dilate.SetTexture(ComputeShader_Dilation, "Write", materialMetaData.emissiveBuffer);

                            //let the GPU perform dilation
                            dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(resolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(resolution.y / THREAD_GROUP_SIZE_Y), 1);
                        }

                        //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                        //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                        //|||||||||||||||||||||||||||||||||||||| NORMAL BUFFER ||||||||||||||||||||||||||||||||||||||
                        //here we will render the normal buffer of the object.
                        //the this custom shader pass basically unwraps the UV1 (Lightmap UVs) to the screen.

                        //create our normal render texture buffer
                        materialMetaData.normalBuffer = new RenderTexture(resolution.x, resolution.y, 32, metaNormalFormat);
                        materialMetaData.normalBuffer.filterMode = FilterMode.Point;
                        materialMetaData.normalBuffer.enableRandomWrite = true;
                        materialMetaData.normalBuffer.Create();

                        //put our render texture to use.
                        metaDataCommandBuffer.SetRenderTarget(materialMetaData.normalBuffer);

                        //queue a draw mesh command, rendering the first pass on our material.
                        metaDataCommandBuffer.DrawMesh(objectMetaData.mesh, Matrix4x4.identity, meshNormalMaterial, submeshIndex, 0, null);

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
                            dilate.Dispatch(ComputeShader_Dilation, Mathf.CeilToInt(resolution.x / THREAD_GROUP_SIZE_X), Mathf.CeilToInt(resolution.y / THREAD_GROUP_SIZE_Y), 1);
                        }
                    }

                    //after rendering both the albedo/emissive lets store the results into our object meta data for the current material that we rendered.
                    //NOTE: its also possible here that there wasn't a meta pass so that means 'materialMetaData' is empty.
                    objectMetaData.materials[j] = materialMetaData;
                }
            }

            //|||||||||||||||||||||||||||||||||||||| (DEBUG) LOG META DATA MEMORY USAGE ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| (DEBUG) LOG META DATA MEMORY USAGE ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| (DEBUG) LOG META DATA MEMORY USAGE ||||||||||||||||||||||||||||||||||||||

            long memorySize = objectMetaData.GetDebugMemorySize();
            uint textures = (uint)(objectMetaData.materials.Length * 3);

            Debug.Log(string.Format("Meta Textures {0} | Total Runtime Memory: {1} MB [{2} B]", textures, Mathf.RoundToInt(memorySize / (1024.0f * 1024.0f)), memorySize));

            //|||||||||||||||||||||||||||||||||||||| SAVE BUFFERS TO DISK ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SAVE BUFFERS TO DISK ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| SAVE BUFFERS TO DISK ||||||||||||||||||||||||||||||||||||||

            for(int i = 0; i < objectMetaData.materials.Length; i++)
            {
                renderTextureConverter.SaveRenderTexture2DAsTexture2D(objectMetaData.materials[i].albedoBuffer, string.Format("{0}/{1}_{2}_albedo.asset", localAssetSceneDataFolder, i, name));
                renderTextureConverter.SaveRenderTexture2DAsTexture2D(objectMetaData.materials[i].emissiveBuffer, string.Format("{0}/{1}_{2}_emissive.asset", localAssetSceneDataFolder, i, name));
                renderTextureConverter.SaveRenderTexture2DAsTexture2D(objectMetaData.materials[i].normalBuffer, string.Format("{0}/{1}_{2}_normal.asset", localAssetSceneDataFolder, i, name));
            }

            Debug.Log(string.Format("Finished At: {0} seconds", Time.realtimeSinceStartupAsDouble - timeBeforeFunction));

            CloseProgressBar();
        }

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||| UTILITIES ||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        public void UpdateProgressBar(string description, float progress) => EditorUtility.DisplayProgressBar("Object Meta Buffer Extractor", description, progress);

        public void CloseProgressBar() => EditorUtility.ClearProgressBar();
    }
}