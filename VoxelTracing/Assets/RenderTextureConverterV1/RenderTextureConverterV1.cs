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
using UnityEngine.Profiling;
using System.Runtime.InteropServices;

namespace UnityVoxelTracer
{
    public static class RenderTextureConverterV1
    {
        /// <summary>
        /// Captures a single slice of the volume we are capturing.
        /// </summary>
        /// <returns></returns>
        private static RenderTexture GetRenderTextureSlice(
            RenderTexture renderTextureSource, 
            int indexZ, 
            ComputeShader computeShaderSlicer)
        {
            //create a SLICE of the render texture
            RenderTexture renderTextureSlice = new RenderTexture(renderTextureSource.width, renderTextureSource.height, 0, renderTextureSource.format);

            //set our options for the render texture SLICE
            renderTextureSlice.dimension = TextureDimension.Tex2D;
            renderTextureSlice.wrapMode = renderTextureSource.wrapMode;
            renderTextureSlice.anisoLevel = renderTextureSource.anisoLevel;
            renderTextureSlice.filterMode = renderTextureSource.filterMode;
            renderTextureSlice.enableRandomWrite = true;
            renderTextureSlice.Create();

            //find the main function in the slicer shader and start displaying each slice
            int ComputeShader_GetSlice = computeShaderSlicer.FindKernel("ComputeShader_GetSlice");

            computeShaderSlicer.SetInt(ShaderIDs.SourceIndexZ, indexZ);
            computeShaderSlicer.SetTexture(ComputeShader_GetSlice, ShaderIDs.Source3D, renderTextureSource);
            computeShaderSlicer.SetTexture(ComputeShader_GetSlice, ShaderIDs.Destination2D, renderTextureSlice);

            computeShaderSlicer.Dispatch(ComputeShader_GetSlice, renderTextureSource.width, renderTextureSource.height, 1);

            return renderTextureSlice;
        }

        /// <summary>
        /// Converts a 2D render texture to a Texture2D object.
        /// </summary>
        /// <returns></returns>
        public static Texture2D ConvertFromRenderTexture2D(
            RenderTexture renderTextureSource, 
            TextureFormat textureFormat, 
            bool alphaIsTransparency = false)
        {
            //create our texture2D object to store the slice
            Texture2D output = new Texture2D(renderTextureSource.width, renderTextureSource.height, textureFormat, renderTextureSource.useMipMap);
            output.filterMode = renderTextureSource.filterMode;
            output.wrapMode = renderTextureSource.wrapMode;
            output.anisoLevel = renderTextureSource.anisoLevel;
            output.alphaIsTransparency = alphaIsTransparency;

            //make sure the render texture slice is active so we can read from it
            RenderTexture.active = renderTextureSource;

            //read the texture and store the data in the texture2D object
            output.ReadPixels(new Rect(0, 0, renderTextureSource.width, renderTextureSource.height), 0, 0);
            output.Apply();

            renderTextureSource.DiscardContents(true, true);
            renderTextureSource.Release();

            return output;
        }

        public static Texture3D ConvertFromRenderTexture3D(
            ComputeShader computeShaderSlicer, 
            RenderTexture renderTextureSource, 
            TextureFormat textureFormat)
        {
            RenderTexture[] slices = new RenderTexture[renderTextureSource.volumeDepth]; //create an array that matches in length the "depth" of the volume
            Texture2D[] finalSlices = new Texture2D[renderTextureSource.volumeDepth]; //create another array to store the texture2D versions of the layers array

            for (int i = 0; i < renderTextureSource.volumeDepth; i++)
                slices[i] = GetRenderTextureSlice(renderTextureSource, i, computeShaderSlicer);

            for (int i = 0; i < renderTextureSource.volumeDepth; i++)
                finalSlices[i] = ConvertFromRenderTexture2D(slices[i], textureFormat);

            Texture3D output = new Texture3D(renderTextureSource.width, renderTextureSource.height, renderTextureSource.volumeDepth, textureFormat, renderTextureSource.useMipMap);
            Color[] outputColors = new Color[renderTextureSource.width * renderTextureSource.height * renderTextureSource.volumeDepth];

            for (int z = 0; z < renderTextureSource.volumeDepth; z++)
            {
                Color[] sliceColors = finalSlices[z].GetPixels();

                int startIndex = z * renderTextureSource.width * renderTextureSource.height;
                Array.Copy(sliceColors, 0, outputColors, startIndex, renderTextureSource.width * renderTextureSource.height);
            }

            output.SetPixels(outputColors);
            output.Apply();

            output.filterMode = renderTextureSource.filterMode;
            output.wrapMode = renderTextureSource.wrapMode;
            output.anisoLevel = renderTextureSource.anisoLevel;

            return output;
        }

        /// <summary>
        /// Saves a 3D Render Texture to the disk
        /// </summary>
        public static void Save3D(
            ComputeShader computeShaderSlicer, 
            RenderTexture renderTextureSource, 
            TextureFormat textureFormat, 
            string assetRealtivePath)
        {
            Texture3D output = ConvertFromRenderTexture3D(computeShaderSlicer, renderTextureSource, textureFormat);
            output.wrapMode = renderTextureSource.wrapMode;
            output.filterMode = renderTextureSource.filterMode;
            output.anisoLevel = renderTextureSource.anisoLevel;

            //AssetDatabase.DeleteAsset(assetRealtivePath);
            AssetDatabase.CreateAsset(output, assetRealtivePath);
        }

        public static Texture3D Duplicate3DTexture(Texture3D source)
        {
            Texture3D duplicate = new Texture3D(source.width, source.height, source.depth, source.format, source.mipmapCount);
            duplicate.wrapMode = source.wrapMode;
            duplicate.anisoLevel = source.anisoLevel;
            duplicate.filterMode = source.filterMode;

            duplicate.SetPixels(source.GetPixels());

            return duplicate;
        }










        public static void SaveRenderTexture3DAsTexture3D(RenderTexture renderTexture3D, string assetRealtivePath)
        {
            int width = renderTexture3D.width;
            int height = renderTexture3D.height;
            int depth = renderTexture3D.volumeDepth;
            int renderTextureMemorySize = (int)Profiler.GetRuntimeMemorySizeLong(renderTexture3D);

            NativeArray<byte> nativeArray = new NativeArray<byte>(renderTextureMemorySize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref nativeArray, renderTexture3D, 0, (request) =>
            {
                Texture3D output = new Texture3D(width, height, depth, renderTexture3D.graphicsFormat, TextureCreationFlags.None);
                output.filterMode = renderTexture3D.filterMode;
                output.SetPixelData(nativeArray, 0);
                output.Apply(false, true);

                AssetDatabase.CreateAsset(output, assetRealtivePath);
                AssetDatabase.SaveAssetIfDirty(output);

                nativeArray.Dispose();
                renderTexture3D.Release();
            });

            request.WaitForCompletion();
        }
    }
}

#endif