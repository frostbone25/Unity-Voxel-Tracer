#if UNITY_EDITOR
using System.Runtime.InteropServices;
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
        public static int GetByteSize() => Marshal.SizeOf(typeof(VoxelLightArea));

        //constructor that initializes the VoxelLightDirectional instance using a Unity Light component.
        public VoxelLightArea(Light areaLight)
        {
            lightColor = new Vector3(areaLight.color.r, areaLight.color.g, areaLight.color.b);

            //Multiply color by light intensity, we are working in HDR anyway so this saves a bit of extra data that we don't have to pass to the compute shader.
            lightColor *= areaLight.intensity;

            //Do a color space conversion on the CPU side, saves a bit of extra unecessary computation in the compute shader.
            //[Gamma -> Linear] 2.2
            //[Linear -> Gamma] 0.454545
            lightColor.x = Mathf.Pow(lightColor.x, 2.2f);
            lightColor.y = Mathf.Pow(lightColor.y, 2.2f);
            lightColor.z = Mathf.Pow(lightColor.z, 2.2f);

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