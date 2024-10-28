using UnityEngine;

namespace CameraMetaPass2
{
    /// <summary>
    /// Object that holds the raw albedo/emissive colors of a given mesh and it's materials.
    /// </summary>
    public struct ObjectMetaData
    {
        public Mesh mesh;
        public Matrix4x4 transformMatrix;
        public Bounds bounds;
        public MaterialMetaData[] materials;

        public void ReleaseMaterials()
        {
            for (int i = 0; i < materials.Length; i++)
                materials[i].ReleaseTextures();
        }
    }
}