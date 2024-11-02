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
using CameraMetaPass1;
using UnityEngine.Experimental.Rendering;
using UnityEditor.SceneManagement;

namespace CameraMetaPass2
{
    [ExecuteInEditMode]
    public class CameraMetaPassV2 : MonoBehaviour
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

        private static string localAssetFolder = "Assets/CameraMetaPassV2";
        private static string localAssetDataFolder = "Assets/CameraMetaPassV2/Data";
        private UnityEngine.SceneManagement.Scene activeScene => EditorSceneManager.GetActiveScene();
        private string localAssetSceneDataFolder => localAssetDataFolder + "/" + activeScene.name;

        private RenderTextureConverterV2 renderTextureConverter => new RenderTextureConverterV2();

        private Camera camera => GetComponent<Camera>();

        private MetaPassRenderingV2.MetaPassRenderingV2 metaPassRenderer;

        //|||||||||||||||||||||||||||||||||||||| PACKAGE PREPPING ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PACKAGE PREPPING ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| PACKAGE PREPPING ||||||||||||||||||||||||||||||||||||||

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

        //|||||||||||||||||||||||||||||||||||||| SCENE RENDERING ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SCENE RENDERING ||||||||||||||||||||||||||||||||||||||
        //|||||||||||||||||||||||||||||||||||||| SCENE RENDERING ||||||||||||||||||||||||||||||||||||||

        [ContextMenu("RenderSceneBuffers")]
        public void RenderSceneBuffers()
        {
            metaPassRenderer = new MetaPassRenderingV2.MetaPassRenderingV2();
            metaPassRenderer.dilationPixelSize = dilationPixelSize;
            metaPassRenderer.minimumBufferResolution = minimumBufferResolution;
            metaPassRenderer.objectLayerMask = objectLayerMask;
            metaPassRenderer.onlyUseGIContributors = onlyUseGIContributors;
            metaPassRenderer.onlyUseMeshesWithinBounds = false;
            metaPassRenderer.onlyUseShadowCasters = onlyUseShadowCasters;
            metaPassRenderer.performDilation = performDilation;
            metaPassRenderer.texelDensityPerUnit = texelDensityPerUnit;
            metaPassRenderer.useBoundingBoxCullingForRendering = useBoundingBoxCullingForRendering;
            metaPassRenderer.doubleSidedGeometry = doubleSidedGeometry;

            SetupAssetFolders(); //Setup a local "scene" folder in our local asset directory if it doesn't already exist.

            List<MetaPassRenderingV2.ObjectMetaData> objectsMetaData = metaPassRenderer.ExtractSceneObjectMetaBuffers();

            //create a render target that we will render the scene into.
            RenderTexture scenePackedBuffer = new RenderTexture(resolution.x, resolution.y, 32, RenderTextureFormat.ARGB64);
            scenePackedBuffer.filterMode = FilterMode.Point;
            scenePackedBuffer.enableRandomWrite = true;
            scenePackedBuffer.Create();

            metaPassRenderer.RenderScene(objectsMetaData, camera, scenePackedBuffer);

            //|||||||||||||||||||||||||||||||||||||| UNPACKING SCENE BUFFER ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| UNPACKING SCENE BUFFER ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| UNPACKING SCENE BUFFER ||||||||||||||||||||||||||||||||||||||

            RenderTexture sceneAlbedo = new RenderTexture(resolution.x, resolution.y, 32, RenderTextureFormat.ARGB32);
            sceneAlbedo.filterMode = FilterMode.Point;
            sceneAlbedo.enableRandomWrite = true;
            sceneAlbedo.Create();

            RenderTexture sceneEmissive = new RenderTexture(resolution.x, resolution.y, 32, RenderTextureFormat.ARGBHalf);
            sceneEmissive.filterMode = FilterMode.Point;
            sceneEmissive.enableRandomWrite = true;
            sceneEmissive.Create();

            RenderTexture sceneNormal = new RenderTexture(resolution.x, resolution.y, 32, RenderTextureFormat.ARGB32);
            sceneNormal.filterMode = FilterMode.Point;
            sceneNormal.enableRandomWrite = true;
            sceneNormal.Create();

            metaPassRenderer.UnpackSceneRender(scenePackedBuffer, sceneAlbedo, sceneEmissive, sceneNormal);

            renderTextureConverter.SaveAsyncRenderTexture2DAsTexture2D(scenePackedBuffer, string.Format("{0}/ScenePacked.asset", localAssetSceneDataFolder));
            renderTextureConverter.SaveAsyncRenderTexture2DAsTexture2D(sceneAlbedo, string.Format("{0}/SceneAlbedo.asset", localAssetSceneDataFolder));
            renderTextureConverter.SaveAsyncRenderTexture2DAsTexture2D(sceneEmissive, string.Format("{0}/SceneEmissive.asset", localAssetSceneDataFolder));
            renderTextureConverter.SaveAsyncRenderTexture2DAsTexture2D(sceneNormal, string.Format("{0}/SceneNormal.asset", localAssetSceneDataFolder));

            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||
            //|||||||||||||||||||||||||||||||||||||| CLEAN UP ||||||||||||||||||||||||||||||||||||||

            metaPassRenderer.CleanUpSceneObjectMetaBuffers(objectsMetaData);
        }
    }
}