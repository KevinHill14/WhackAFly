using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FlyAI.Editor
{
    // The game looks some shaders up by name at runtime, and Unity strips shaders that no asset references from a
    // build. This makes sure they are in Project Settings > Graphics > Always Included Shaders before every build.
    // It can also be run by hand from the FlyAI menu.
    public class FlyAIBuildShaders : IPreprocessBuildWithReport
    {
        static readonly string[] Names =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Unlit",
            "Sprites/Default",
        };

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => Include();

        [MenuItem("FlyAI/Include game shaders in builds")]
        static void Include()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[FlyAI] Could not open the Graphics settings to add shaders.");
                return;
            }
            var settings = new SerializedObject(assets[0]);
            var list = settings.FindProperty("m_AlwaysIncludedShaders");
            var added = 0;
            foreach (var name in Names)
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;
                var present = false;
                for (var i = 0; i < list.arraySize && !present; i++) present = list.GetArrayElementAtIndex(i).objectReferenceValue == shader;
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                added++;
            }
            if (added == 0) return;
            settings.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log($"[FlyAI] Added {added} shader(s) to Always Included Shaders.");
        }
    }
}
