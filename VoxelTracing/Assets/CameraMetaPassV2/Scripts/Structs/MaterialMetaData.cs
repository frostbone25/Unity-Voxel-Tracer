using UnityEngine;

namespace CameraMetaPass2
{
    /// <summary>
    /// Contains two render textures that represent the raw albedo/emissive colors of an object.
    /// <para>These render textures are UV1 (Lightmap UVs) unwrapped. </para>
    /// </summary>
    public struct MaterialMetaData
    {
        public RenderTexture packedMetaBuffer;

        public void ReleaseTextures()
        {
            if (packedMetaBuffer != null)
                packedMetaBuffer.Release();
        }

        public bool isEmpty() => packedMetaBuffer == null;
    }
}