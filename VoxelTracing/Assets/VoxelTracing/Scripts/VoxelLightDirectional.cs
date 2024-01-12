#if UNITY_EDITOR
using System.Runtime.InteropServices;
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
        public static int GetByteSize() => Marshal.SizeOf(typeof(VoxelLightDirectional));

        //constructor that initializes the VoxelLightDirectional instance using a Unity Light component.
        public VoxelLightDirectional(Light directionalLight)
        {
            lightColor = new Vector3(directionalLight.color.r, directionalLight.color.g, directionalLight.color.b);

            //Multiply color by light intensity, we are working in HDR anyway so this saves a bit of extra data that we don't have to pass to the compute shader.
            lightColor *= directionalLight.intensity;

            //Do a color space conversion on the CPU side, saves a bit of extra unecessary computation in the compute shader.
            //[Gamma -> Linear] 2.2
            //[Linear -> Gamma] 0.454545
            lightColor.x = Mathf.Pow(lightColor.x, 2.2f);
            lightColor.y = Mathf.Pow(lightColor.y, 2.2f);
            lightColor.z = Mathf.Pow(lightColor.z, 2.2f);

            lightDirection = directionalLight.transform.forward;
        }
    }
}
#endif