# Controlled Demolition - Unity Mesh Fragmentation Demo

Unity tech demo implementing runtime mesh fragmentation via iterative plane slicing. Uses local-space geometry calculations, Job System/Burst optimization, a deferred processing pipeline (`SliceIterationManager`, `MeshCreationManager`), reference counting for native memory (`SliceResultReference`), object pooling, and the `Mesh.MeshData` API. Features recursive destruction and physics integration.

**Video Showcase:** [Watch on YouTube](https://www.youtube.com/watch?v=Od4ipg_NaqY)

Built with Unity (URP), C#, Job System, Burst, `Unity.Mathematics`.

## How to Test

1.  Open `Assets/Scenes/SampleScene.unity`.
2.  Ensure scene has `DestructibleObject` (e.g., `Cube.prefab`) & Manager scripts.
3.  Assign `Fragment Prefab` to `FragmentPoolManager`.
4.  Run scene & left-click `DestructibleObject` or use `BasicExplosive` prefab.
