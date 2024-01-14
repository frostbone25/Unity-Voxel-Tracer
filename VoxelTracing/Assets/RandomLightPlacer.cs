using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;

public class RandomLightPlacer : MonoBehaviour
{
    public Vector3 volumeSize;
    public int lights;
    public LightmapBakeType lightmapBakeType;
    public LightShadows lightShadows;
    public float lightIntensity;

    [ContextMenu("Place Lights")]
    public void PlaceLights()
    {
        GameObject randomLightsParent = new GameObject("RandomLightPlacement");
        randomLightsParent.transform.position = transform.position;

        for (int i = 0; i < lights; i++) 
        {
            GameObject newLightGameObject = new GameObject("RandomLight");
            Light newLight = newLightGameObject.AddComponent<Light>();
            newLight.intensity = lightIntensity;
            newLight.lightmapBakeType = lightmapBakeType;
            newLight.shadows = lightShadows;
            newLight.type = UnityEngine.LightType.Point;
            newLight.color = new Color(Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f));

            newLight.transform.position = transform.position + new Vector3(Random.Range(-volumeSize.x / 2.0f, volumeSize.x / 2.0f), Random.Range(-volumeSize.y / 2.0f, volumeSize.y / 2.0f), Random.Range(-volumeSize.z / 2.0f, volumeSize.z / 2.0f));

            newLightGameObject.transform.SetParent(randomLightsParent.transform);
        }
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.white;
        Gizmos.DrawWireCube(transform.position, volumeSize);
    }
}
