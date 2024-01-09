#if UNITY_EDITOR
using UnityEngine;

namespace UnityVoxelTracer
{
    /// <summary>
    /// Area Light.
    /// 
    /// <para>Gets the necessary data from a Unity Area Light, to be used by the voxel tracer. </para>
    /// </summary>
    public struct VoxelLightArea
    {
        public Vector3 lightPosition;
        public Vector3 lightForwardDirection;
        public Vector3 lightRightDirection;
        public Vector3 lightUpwardDirection;
        public Vector2 lightSize;
        public Vector3 lightColor;
        public float lightRange;

        //returns the total size, in bytes, occupied by an instance of this struct in memory.
        public static int GetByteSize()
        {
            int size = 0;

            size += 3 * 4; //lightPosition (12 bytes)
            size += 3 * 4; //lightForwardDirection (12 bytes)
            size += 3 * 4; //lightRightDirection (12 bytes)
            size += 3 * 4; //lightUpwardDirection (12 bytes)
            size += 3 * 4; //lightColor (12 bytes)

            size += 2 * 4; //lightSize (8 bytes)

            size += 4; //lightRange (4 bytes)

            return size;
        }

        //constructor that initializes the VoxelLightDirectional instance using a Unity Light component.
        public VoxelLightArea(Light areaLight)
        {
            lightColor = new Vector3(areaLight.color.r, areaLight.color.g, areaLight.color.b) * areaLight.intensity;
            lightForwardDirection = areaLight.transform.forward;
            lightRightDirection = areaLight.transform.right;
            lightUpwardDirection = areaLight.transform.up;
            lightPosition = areaLight.transform.position;
            lightRange = areaLight.range;
            lightSize = areaLight.areaSize;
        }
    }
}
#endif