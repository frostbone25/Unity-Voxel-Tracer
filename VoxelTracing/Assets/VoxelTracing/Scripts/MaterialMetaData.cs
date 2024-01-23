using UnityEngine;

namespace UnityVoxelTracer
{
    /// <summary>
    /// Contains two render textures that represent the raw albedo/emissive colors of an object.
    /// <para>These render textures are UV1 (Lightmap UVs) unwrapped. </para>
    /// </summary>
    public struct MaterialMetaData
    {
        public RenderTexture albedo;
        public RenderTexture emission;

        public void ReleaseTextures()
        {
            if (albedo != null)
                albedo.Release();

            if (emission != null)
                emission.Release();
        }

        public bool isEmpty() => albedo == null || emission == null;
    }
}