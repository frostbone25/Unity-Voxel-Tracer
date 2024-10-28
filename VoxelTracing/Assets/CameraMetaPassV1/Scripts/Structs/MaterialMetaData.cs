using UnityEngine;
using UnityEngine.Profiling;

namespace CameraMetaPass1
{
    /// <summary>
    /// Contains two render textures that represent the raw albedo/emissive colors of an object.
    /// <para>These render textures are UV1 (Lightmap UVs) unwrapped. </para>
    /// </summary>
    public struct MaterialMetaData
    {
        public RenderTexture albedoBuffer;
        public RenderTexture emissiveBuffer;
        public RenderTexture normalBuffer;

        public void ReleaseTextures()
        {
            if (albedoBuffer != null)
                albedoBuffer.Release();

            if (emissiveBuffer != null)
                emissiveBuffer.Release();

            if (normalBuffer != null)
                normalBuffer.Release();
        }

        public bool isEmpty() => albedoBuffer == null || emissiveBuffer == null || normalBuffer == null;

        public long GetDebugMemorySize() => Profiler.GetRuntimeMemorySizeLong(albedoBuffer) + Profiler.GetRuntimeMemorySizeLong(emissiveBuffer) + Profiler.GetRuntimeMemorySizeLong(normalBuffer);
    }
}