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

[ExecuteInEditMode]
public class MetaPassTesting : MonoBehaviour
{
    /*
     * [IDEA]: We might be able to do some data-packing in our meta shader so that we only render the scene once, then unpack our results into two different buffers.
     * 
     * For instance with albedo, 8 bits is good enough for each channel, and we need alpha so thats 32 bits.
     * We can technically pack those 32 bits into a single 32 bit float. (Note that floats don't have uniform precision)
     * 
     * With emission, we can normalize the color to [0,1] and store it in 24 bits (RGB)
     * For intensity we can probably knock down it's precison to 16 bits, effectively half precision.
     * So that means in total emission will need 40 bits.
     * 
     * In total the packed data is 72 bits. (or 88 if we preserve emission intensity as a 32-bit float)
     * 
     * We can render the scene once in ARGBFloat, which has 128 bits in total (32-bits floats for each channel)
     * RED (R32 Float) - Albedo Red (8 bit) | Albedo Green (8 bit) | Albedo Blue (8 bit) | Albedo Alpha (8 bit)
     * GREEN (G32 Float) - Emissive Normalized Red (8 bit) | Emissive Normalized Green (8 bit) | Emissive Normalized Blue (8 bit) | EMPTY (8 bit)
     * BLUE (B32 Float) - Emissive Intensity (16 bit)
     * ALPHA (A32 Float) - EMPTY
     * 
     * NOTE 2: We can also use Lightmap UVs, and it's scale/offsets defined for each mesh renderer to our advantage if a scene is lightmapped.
     * We can atlas the meta buffers into a single texture to save additional memory costs.
     * However pema noted that this is only possible IF a scene was already lightmapped.
     * 
     * In theory we can do our own atlasing... but thats alot of work :(
    */

    //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| PUBLIC VARIABLES ||||||||||||||||||||||||||||||||||||||||||

    //controls how many "pixels" per unit an object will have.
    public float texelDensityPerUnit = 1;

    //minimum resolution for objects in the scene (so objects too small will be capped to this value resolution wise)
    public int minimumResolution = 16;

    //[OPTIMIZATION] only includes meshes that are marked contribute GI static
    public bool onlyIncludeGIContributors = true;

    //the camera that will render the scene
    public Camera camera;

    //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||
    //|||||||||||||||||||||||||||||||||||||||||| PRIVATE VARIABLES ||||||||||||||||||||||||||||||||||||||||||

    //Size of the thread groups for compute shaders.
    //These values should match the #define ones in the compute shaders.
    private static int THREAD_GROUP_SIZE_X = 8;
    private static int THREAD_GROUP_SIZE_Y = 8;

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
        for(int i = 1; i < lods.Length; i++)
        {
            for (int j = 0; j < lods[i].renderers.Length; j++)
            {
                Renderer lodRenderer = lods[i].renderers[j];

                if(lodRenderer != null)
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


    [ContextMenu("RenderSceneMetaPassForCamera")]
    public void RenderAllMeshesNormally()
    {
        double timeBeforeFunction = Time.realtimeSinceStartupAsDouble;

        //fetch the compute shader that handles dilation
        ComputeShader dilate = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Dilation.compute");

        //formats for each of the object buffers
        RenderTextureFormat albedoFormat = RenderTextureFormat.ARGB4444;
        RenderTextureFormat emissionFormat = RenderTextureFormat.ARGB2101010;

        //parameters for the final scene render at the end
        Vector2Int sceneResolution = new Vector2Int(1920, 1080);
        RenderTextureFormat sceneRenderTextureFormat = RenderTextureFormat.ARGBFloat;
        TextureFormat savedSceneFormat = TextureFormat.RGBAHalf;

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
        for(int i = 0; i < lodGroups.Length; i++)
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
            objectTextureResolutionSquare = Math.Max(minimumResolution, objectTextureResolutionSquare);

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

        //|||||||||||||||||||||||||||||||||||||| SCENE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SCENE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SCENE ALBEDO BUFFER ||||||||||||||||||||||||||||||||||||||
        //In here we will render the scene almost like normal... except we will render with a custom shader that displays the raw albedo colors.

        using (CommandBuffer sceneAlbedoCommandBuffer = new CommandBuffer())
        {
            //create a render target that we will render the scene into.
            RenderTexture sceneMetaRenderTexture = new RenderTexture(sceneResolution.x, sceneResolution.y, 32, sceneRenderTextureFormat);
            sceneMetaRenderTexture.Create();

            //calculate the view matrix of the camera that we are using to render the scene with.
            Matrix4x4 lookMatrix = Matrix4x4.LookAt(camera.transform.position, camera.transform.position + camera.transform.forward, camera.transform.up);
            Matrix4x4 scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
            Matrix4x4 viewMatrix = scaleMatrix * lookMatrix.inverse;

            //make the render target active, and setup projection
            sceneAlbedoCommandBuffer.SetRenderTarget(sceneMetaRenderTexture);
            sceneAlbedoCommandBuffer.SetViewProjectionMatrices(viewMatrix, camera.projectionMatrix);
            sceneAlbedoCommandBuffer.SetViewport(new Rect(0, 0, sceneMetaRenderTexture.width, sceneMetaRenderTexture.height));
            sceneAlbedoCommandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));

            //create a custom material with a custom shader that will only show the buffers we feed it.
            Material objectMaterial = new Material(Shader.Find("Hidden/VoxelBufferMeta"));

            MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
            materialPropertyBlock.SetVector("unity_LightmapST", new Vector4(1, 1, 0, 0)); //cancel out any lightmap UV scaling/offsets.

            //iterate through each object we collected
            for (int i = 0; i < objectsMetaData.Count; i++)
            {
                ObjectMetaData objectMetaData = objectsMetaData[i];

                //if our object has materials
                if(objectMetaData.materials != null)
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
                            sceneAlbedoCommandBuffer.DrawMesh(objectMetaData.mesh, objectMetaData.transformMatrix, objectMaterial, submeshIndex, 0, materialPropertyBlock);
                        }
                    }
                }    
            }

            //actually renders the scene.
            Graphics.ExecuteCommandBuffer(sceneAlbedoCommandBuffer);

            //convert the render target to a texture so we can save it to the disk.
            Texture2D sceneMetaTexture = RenderTextureConverter.ConvertFromRenderTexture2D(sceneMetaRenderTexture, savedSceneFormat);

            //save it!
            AssetDatabase.CreateAsset(sceneMetaTexture, "Assets/SceneAlbedo.asset");

            //release the render texture, we are done with it.
            sceneMetaRenderTexture.Release();
        }

        //|||||||||||||||||||||||||||||||||||||| SCENE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SCENE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SCENE EMISSIVE BUFFER ||||||||||||||||||||||||||||||||||||||
        //In here we will render the scene almost like normal... except we will render with a custom shader that displays the raw emissive colors.

        /*
        using (CommandBuffer sceneEmissiveCommandBuffer = new CommandBuffer())
        {
            //create a render target that we will render the scene into.
            RenderTexture sceneMetaRenderTexture = new RenderTexture(sceneResolution.x, sceneResolution.y, 32, sceneRenderTextureFormat);
            sceneMetaRenderTexture.Create();

            //calculate the view matrix of the camera that we are using to render the scene with.
            Matrix4x4 lookMatrix = Matrix4x4.LookAt(camera.transform.position, camera.transform.position + camera.transform.forward, camera.transform.up);
            Matrix4x4 scaleMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1, 1, -1));
            Matrix4x4 viewMatrix = scaleMatrix * lookMatrix.inverse;

            //make the render target active, and setup projection
            sceneEmissiveCommandBuffer.SetRenderTarget(sceneMetaRenderTexture);
            sceneEmissiveCommandBuffer.SetViewProjectionMatrices(viewMatrix, camera.projectionMatrix);
            sceneEmissiveCommandBuffer.SetViewport(new Rect(0, 0, sceneMetaRenderTexture.width, sceneMetaRenderTexture.height));
            sceneEmissiveCommandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));

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

                            //feed it our emission buffer
                            materialPropertyBlock.SetTexture("_MainTex", materialMetaData.emission);

                            //draw the mesh in the scene, rendering only its raw emissive colors.
                            sceneEmissiveCommandBuffer.DrawMesh(objectMetaData.mesh, objectMetaData.transformMatrix, objectMaterial, submeshIndex, 0, materialPropertyBlock);
                        }
                    }
                }
            }

            //actually renders the scene.
            Graphics.ExecuteCommandBuffer(sceneEmissiveCommandBuffer);

            //convert the render target to a texture so we can save it to the disk.
            Texture2D sceneMetaTexture = RenderTextureConverter.ConvertFromRenderTexture2D(sceneMetaRenderTexture, savedSceneFormat);

            //save it!
            AssetDatabase.CreateAsset(sceneMetaTexture, "Assets/SceneEmissive.asset");

            //release the render texture, we are done with it.
            sceneMetaRenderTexture.Release();
        }
        */

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
                    if (objectMetaData.materials[j].isEmpty() == false)
                    {
                        memorySize += Profiler.GetRuntimeMemorySizeLong(objectMetaData.materials[j].albedo);
                        memorySize += Profiler.GetRuntimeMemorySizeLong(objectMetaData.materials[j].emission);

                        textures += 2;

                        //Debug.Log(string.Format("Meta Texture {0} Resolution: {1}", i + j, objectMetaData.materials[j].albedo.width));
                    }

                    objectMetaData.materials[j].ReleaseTextures();
                }
            }

            objectMetaData.materials = null;
        }

        //Debug.Log(string.Format("(FINAL) Meta Textures Amount: {0}", textures));
        //Debug.Log(string.Format("(FINAL) Meta Textures Runtime Memory: {0} MB [{1} B]", Mathf.RoundToInt(memorySize / (1024.0f * 1024.0f)), memorySize));
        Debug.Log(string.Format("Finished At: {0} seconds", Time.realtimeSinceStartupAsDouble - timeBeforeFunction));
    }
}
