# Unity Voxel Tracer

Work in progress voxel tracer. This is intended to be an offline solution for generating lighting/volumetrics.

### Features
- Direct Lighting *(Directional, Spot, Point, Area)*
- Multi-Bounce Lighting
- Emissive Lighting
- Environment Lighting
- Volumetric Lighting

### TODO / Notes / Ideas:
- Geometry thickening to solve problems with light leakage *(conservative rasterization perhaps?)*
- Improving scene albedo/emissive buffer capture by using the "Meta" pass rather than a replacement shader to support custom shaders.
- Optimizing Scene Voxelization even further.
- Would like to look into methods for potentially blurring and averaging results to improve quality. Something like a bilaterial blur that is "voxel aware" perhaps?

# Screenshots

![1](GithubContent/1.png)
*Voxel Trace: Final Lighting*

![4](GithubContent/4.png)
*Voxel Trace: Direct Lighting*

![5](GithubContent/5.png)
*Voxel Trace: Single Bounce Environment Lighting.*

![2](GithubContent/2.png)
*Voxel Trace: Scene Albedo Buffer*

![3](GithubContent/3.png)
*Voxel Trace: Scene Normals Buffer*

![7](GithubContent/7.png)
*Voxel Trace: Emissive Lighting*

![8](GithubContent/8.png)
*Voxel Trace: Scene Emissive Buffer*

![9](GithubContent/9.png)
*Voxel Trace: Volumetric Emissive Lighting.*

![10](GithubContent/10.png)
*Voxel Trace: Final Volumetric Lighting.*

![12](GithubContent/12.png)
*Voxel Trace: Volumetric Direct Lighting Only.*

![13](GithubContent/13.png)
*Voxel Trace: Volumetric Bounced Lighting Only.*

![11](GithubContent/11.png)
*Voxel Trace: Volumetric Environment Lighting Only.*

### Why?

This tool came about with the need to make the [Baked Volumetrics](https://github.com/frostbone25/Unity-Baked-Volumetrics) effect no longer dependent on sampling from light probes in a scene. So building a voxel based raytracer was necessary, and here we are. It's worth noting that this tool serves as a foundation for potentially more things to come in the future *(Real-time Voxel Based GI, Voxel Based Specular Reflections, Voxel Lightmaps, Scene To Voxel Mesh, etc.)*

# Sources / References / Credits
- **[pema99](https://gist.github.com/pema99)**: Helped fix a big issue regarding TDR and GPU Readbacks for better baking stability, as well as additional advice and tips. *(Thank you!)*
- **[Morgan McGuire](https://casual-effects.com/data/)**:  Sample Scenes.
- **[Light Trees and The Many Lights Problem](https://psychopath.io/post/2020_04_20_light_trees)**
- **[Unity SRP Core Sampling.hlsl](https://github.com/needle-mirror/com.unity.render-pipelines.core/blob/master/ShaderLibrary/Sampling/Sampling.hlsl)**
- **[Unity SRP Core Random.hlsl](https://github.com/needle-mirror/com.unity.render-pipelines.core/blob/master/ShaderLibrary/Random.hlsl)**