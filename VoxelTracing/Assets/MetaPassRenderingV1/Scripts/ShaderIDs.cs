using UnityEngine;

namespace MetaPassRenderingV1
{
    public static class ShaderIDs
    {
        public static int Write => Shader.PropertyToID("Write");
        public static int _MainTex => Shader.PropertyToID("_MainTex");
        public static int KernelSize => Shader.PropertyToID("KernelSize");
        public static int unity_LightmapST => Shader.PropertyToID("unity_LightmapST");
        public static int unity_MetaVertexControl => Shader.PropertyToID("unity_MetaVertexControl");
        public static int unity_MetaFragmentControl => Shader.PropertyToID("unity_MetaFragmentControl");
        public static int unity_OneOverOutputBoost => Shader.PropertyToID("unity_OneOverOutputBoost");
        public static int unity_MaxOutputValue => Shader.PropertyToID("unity_MaxOutputValue");
        public static int unity_UseLinearSpace => Shader.PropertyToID("unity_UseLinearSpace");
    }
}