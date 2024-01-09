#if UNITY_EDITOR
using UnityEngine;

namespace UnityVoxelTracer
{
    /// <summary>
    /// Spot Light.
    /// 
    /// <para>Gets the necessary data from a Unity Spot Light, to be used by the voxel tracer. </para>
    /// </summary>
    public struct VoxelLightSpot
    {
        public Vector3 lightPosition;
        public Vector3 lightDirection;
        public Vector3 lightColor;
        public float lightRange;
        public float lightAngle;

        //returns the total size, in bytes, occupied by an instance of this struct in memory.
        public static int GetByteSize()
        {
            int size = 0;

            size += 3 * 4; //lightPosition (12 bytes)
            size += 3 * 4; //lightDirection (12 bytes)
            size += 3 * 4; //lightColor (12 bytes)

            size += 4; //lightRange (4 bytes)
            size += 4; //lightAngle (4 bytes)

            return size;
        }

        //constructor that initializes the VoxelLightSpot instance using a Unity Light component.
        public VoxelLightSpot(Light spotLight)
        {
            lightColor = new Vector3(spotLight.color.r, spotLight.color.g, spotLight.color.b) * spotLight.intensity;
            lightPosition = spotLight.transform.position;
            lightDirection = spotLight.transform.forward;
            lightRange = spotLight.range;
            lightAngle = spotLight.spotAngle;
        }
    }
}
#endif