#if UNITY_EDITOR

using UnityEngine;

public struct VoxelLightPoint
{
    public Vector3 lightPosition;
    public Vector3 lightColor;
    public float lightIntensity;
    public float lightRange;

    public static int GetByteSize()
    {
        int size = 0;

        size += 3 * 4; //lightPosition (12 bytes)
        size += 3 * 4; //lightColor (12 bytes)

        size += 4; //lightIntensity (4 bytes)
        size += 4; //lightRange (4 bytes)

        return size;
    }

    public VoxelLightPoint(Light pointLight)
    {
        lightColor = new Vector3(pointLight.color.r, pointLight.color.g, pointLight.color.b);
        lightIntensity = pointLight.intensity;
        lightPosition = pointLight.transform.position;
        lightRange = pointLight.range;
    }
}

#endif