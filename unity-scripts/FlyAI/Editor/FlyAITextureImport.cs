using UnityEditor;

namespace FlyAI.EditorTools
{
    // Keeps room textures imported correctly, including normal maps.
    public class FlyAITextureImport : AssetPostprocessor
    {
        const string Folder = "Assets/FlyAI/Art/Resources/FlyAITextures";

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = assetPath.Contains("_Normal") ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.wrapMode = UnityEngine.TextureWrapMode.Repeat;
            importer.anisoLevel = 8;
            importer.maxTextureSize = 1024;
            importer.mipmapEnabled = true;
        }

        [InitializeOnLoadMethod]
        static void RepairImports()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || !AssetDatabase.IsValidFolder(Folder)) return;
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Folder }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.Contains("_Normal") && AssetImporter.GetAtPath(path) is TextureImporter importer
                        && importer.textureType != TextureImporterType.NormalMap)
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
            };
        }
    }
}
