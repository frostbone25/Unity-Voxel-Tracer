using UnityEngine;

namespace SceneVoxelizer2
{
    public static class ShaderIDs
    {
        public static int Write => Shader.PropertyToID("Write");
        public static int VolumeResolution => Shader.PropertyToID("VolumeResolution");
        public static int CameraVoxelRender => Shader.PropertyToID("CameraVoxelRender");
        public static int AxisIndex => Shader.PropertyToID("AxisIndex");
    }
}