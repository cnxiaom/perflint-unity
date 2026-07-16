namespace PerfLint.Core
{
    /// <summary>
    /// Marker for scanners whose findings only reflect the CURRENTLY LOADED scene(s) — they walk
    /// SceneManager's loaded scenes rather than the whole project (e.g. Static Batching / GPU Instancing
    /// overlap / Skinned Instancing / Mesh LOD). Every other scanner is project-wide (AssetDatabase).
    ///
    /// The scan panel enumerates the discovered <see cref="ISceneScoped"/> scanners to build its
    /// "these checks only see the open scene" notice, so the notice stays in sync automatically when
    /// scene-scoped rules are added/removed — no hardcoded rule list to drift out of date.
    /// (See CLAUDE.md: cross-references must be gated or read live state, never hardcoded.)
    ///
    /// Deliberately memberless: the display label comes from the scanner's own <see cref="IScanner.Name"/>.
    /// </summary>
    public interface ISceneScoped
    {
    }
}
