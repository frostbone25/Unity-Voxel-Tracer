# Unity Voxel Tracer

Work in progress voxel tracer. This is intended to be an offline solution for generating lighting/volumetrics.

### Features
- Direct Lighting *(Directional, Spot, Point, Area)*
- Multi-Bounce Lighting
- Emissive Lighting
- Environment Lighting
- Volumetric Lighting
- 3D Gaussian Blur Filter for Volumetric Lighting

### TODO / Notes / Ideas:
- Soft shadow support for Directional/Spot/Point lights.
- Geometry thickening to solve problems with light leakage *(conservative rasterization perhaps?)*
- Optimizing Scene Voxelization even further.
- Would like to look into methods for potentially blurring and averaging results to improve quality. Something like a bilaterial blur that is "voxel aware" perhaps?

# Screenshots

![7](GithubContent/7.png)
*Voxel Trace: Bounce Lighting*

![8](GithubContent/8.png)
*Voxel Trace: Direct Lighting*

![8](GithubContent/4.png)
*Voxel Trace: Direct Lighting with Combined Bounce and Emissive Lighting.*

![8](GithubContent/3.png)
*Ground Truth*

![8](GithubContent/5.png)
*Voxel Trace: Direct Lighting with Area Lights with soft shadows.*

![8](GithubContent/6.png)
*Ground Truth*

![10](GithubContent/10.png)
*Voxel Trace: Volumetric Bounce Lighting with emissives. (Lots of noise due to low sample count)*

![11](GithubContent/11.png)
*Voxel Trace: Volumetric Direct Lighting.*

![12](GithubContent/12.png)
*Voxel Trace: Volumetric Direct + Bounce Lighting. (Lots of noise due to low sample count)*

![2](GithubContent/2.png)
*Voxel Trace: Early test with direct lighting*

![1](GithubContent/1.png)
*Ground Truth*

# Sources / References / Credits
- **[pema99](https://gist.github.com/pema99)**: Fixed a big issue regarding TDR and GPU Readbacks for better baking stability, as well as additional advice and tips. *(Thank you!)*
- **[Morgan McGuire](https://casual-effects.com/data/)**:  Sample Scenes.
- **[Light Trees and The Many Lights Problem](https://psychopath.io/post/2020_04_20_light_trees)**
- **[Unity SRP Core Sampling.hlsl](https://github.com/needle-mirror/com.unity.render-pipelines.core/blob/master/ShaderLibrary/Sampling/Sampling.hlsl)**
- **[Unity SRP Core Random.hlsl](https://github.com/needle-mirror/com.unity.render-pipelines.core/blob/master/ShaderLibrary/Random.hlsl)**