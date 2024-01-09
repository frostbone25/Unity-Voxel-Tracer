#if UNITY_EDITOR
using UnityEngine;

namespace UnityVoxelTracer
{
    /// <summary>
    /// Directional Light.
    /// 
    /// <para>Gets the necessary data from a Unity Directional Light, to be used by the voxel tracer. </para>
    /// </summary>
    public struct VoxelLightDirectional
    {
        public Vector3 lightDirection;
        public Vector3 lightColor;

        //returns the total size, in bytes, occupied by an instance of this struct in memory.
        public static int GetByteSize()
        {
            int size = 0;

            size += 3 * 4; //lightDirection (12 bytes)
            size += 3 * 4; //lightColor (12 bytes)

            return size;
        }

        //constructor that initializes the VoxelLightDirectional instance using a Unity Light component.
        public VoxelLightDirectional(Light directionalLight)
        {
            lightColor = new Vector3(directionalLight.color.r, directionalLight.color.g, directionalLight.color.b) * directionalLight.intensity;
            lightDirection = directionalLight.transform.forward;
        }
    }
}
#endif