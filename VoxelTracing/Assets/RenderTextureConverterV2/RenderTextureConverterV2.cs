#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine;
using UnityEditor;
using System.IO;
using System;
using UnityEngine.Experimental.Rendering;
using Unity.Collections;
//using UnityEngine.Profiling;
//using System.Runtime.InteropServices;

namespace RenderTextureConverting
{
    public class RenderTextureConverterV2
    {
        private Texture2D convertedTexture2D;
        private Texture3D convertedTexture3D;

        public Texture2D ConvertRenderTexture2DToTexture2D(RenderTexture renderTexture2D)
        {
            int width = renderTexture2D.width;
            int height = renderTexture2D.height;
            //int renderTextureMemorySize = (int)Profiler.GetRuntimeMemorySizeLong(renderTexture2D);
            int renderTextureMemorySize = (int)RenderTextureSize.GetRenderTextureMemorySize(renderTexture2D);

            NativeArray<byte> nativeArray = new NativeArray<byte>(renderTextureMemorySize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref nativeArray, renderTexture2D, 0, (request) =>
            {
                convertedTexture2D = new Texture2D(width, height, renderTexture2D.graphicsFormat, TextureCreationFlags.None);
                convertedTexture2D.filterMode = convertedTexture2D.filterMode;
                convertedTexture2D.SetPixelData(nativeArray, 0);
                convertedTexture2D.Apply(false, true);

                nativeArray.Dispose();
                renderTexture2D.Release();
            });

            request.WaitForCompletion();

            return convertedTexture2D;
        }

        public void SaveRenderTexture2DAsTexture2D(RenderTexture renderTexture2D, string assetRealtivePath)
        {
            Texture2D converted = ConvertRenderTexture2DToTexture2D(renderTexture2D);
            AssetDatabase.CreateAsset(converted, assetRealtivePath);
            AssetDatabase.SaveAssetIfDirty(converted);
        }

        public Texture3D ConvertRenderTexture3DToTexture3D(RenderTexture renderTexture3D)
        {
            int width = renderTexture3D.width;
            int height = renderTexture3D.height;
            int depth = renderTexture3D.volumeDepth;
            //int renderTextureMemorySize = (int)Profiler.GetRuntimeMemorySizeLong(renderTexture3D);
            int renderTextureMemorySize = (int)RenderTextureSize.GetRenderTextureMemorySize(renderTexture3D);

            NativeArray<byte> nativeArray = new NativeArray<byte>(renderTextureMemorySize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref nativeArray, renderTexture3D, 0, (request) =>
            {
                convertedTexture3D = new Texture3D(width, height, depth, renderTexture3D.graphicsFormat, TextureCreationFlags.None);
                convertedTexture3D.filterMode = renderTexture3D.filterMode;
                convertedTexture3D.SetPixelData(nativeArray, 0);
                convertedTexture3D.Apply(false, true);

                nativeArray.Dispose();
                renderTexture3D.Release();
            });

            request.WaitForCompletion();

            return convertedTexture3D;
        }

        public void SaveRenderTexture3DAsTexture3D(RenderTexture renderTexture3D, string assetRealtivePath)
        {
            Texture3D converted = ConvertRenderTexture3DToTexture3D(renderTexture3D);
            AssetDatabase.CreateAsset(converted, assetRealtivePath);
            AssetDatabase.SaveAssetIfDirty(converted);
        }
    }
}

#endif