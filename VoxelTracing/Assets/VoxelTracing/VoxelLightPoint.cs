#if UNITY_EDITOR
using UnityEngine;

namespace UnityVoxelTracer
{
    /// <summary>
    /// Point Light.
    /// 
    /// <para>Gets the necessary data from a Unity Point Light, to be used by the voxel tracer. </para>
    /// </summary>
    public struct VoxelLightPoint
    {
        public Vector3 lightPosition;
        public Vector3 lightColor;
        public float lightRange;

        //returns the total size, in bytes, occupied by an instance of this struct in memory.
        public static int GetByteSize()
        {
            int size = 0;

            size += 3 * 4; //lightPosition (12 bytes)
            size += 3 * 4; //lightColor (12 bytes)

            size += 4; //lightRange (4 bytes)

            return size;
        }

        //constructor that initializes the VoxelLightPoint instance using a Unity Light component.
        public VoxelLightPoint(Light pointLight)
        {
            lightColor = new Vector3(pointLight.color.r, pointLight.color.g, pointLight.color.b) * pointLight.intensity;
            lightPosition = pointLight.transform.position;
            lightRange = pointLight.range;
        }
    }
}
#endif