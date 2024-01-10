#if UNITY_EDITOR
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;

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
        public static int GetByteSize() => Marshal.SizeOf(typeof(VoxelLightPoint));

        //constructor that initializes the VoxelLightPoint instance using a Unity Light component.
        public VoxelLightPoint(Light pointLight)
        {
            lightColor = new Vector3(pointLight.color.r, pointLight.color.g, pointLight.color.b);

            //Multiply color by light intensity, we are working in HDR anyway so this saves a bit of extra data that we don't have to pass to the compute shader.
            lightColor *= pointLight.intensity;

            //Do a color space conversion on the CPU side, saves a bit of extra unecessary computation in the compute shader.
            //[Gamma -> Linear] 2.2
            //[Linear -> Gamma] 0.454545
            lightColor.x = Mathf.Pow(lightColor.x, 2.2f);
            lightColor.y = Mathf.Pow(lightColor.y, 2.2f);
            lightColor.z = Mathf.Pow(lightColor.z, 2.2f);

            lightPosition = pointLight.transform.position;
            lightRange = pointLight.range;
        }
    }
}
#endif