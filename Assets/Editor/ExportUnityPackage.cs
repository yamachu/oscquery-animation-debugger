using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dev.Yamachu.OscQueryAnimationDebugger.CI
{
    public static class ExportUnityPackage
    {
        private static string GetCommandLineArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static readonly string ExportRoot =
            GetCommandLineArg("-packagePath") ??
            Environment.GetEnvironmentVariable("UNITY_PACKAGE_PATH") ??
            "Assets/dev.yamachu.oscquery-animation-debugger";
        private static readonly string[] AdditionalSearchRoots =
        {
            "Assets/Packages/OscCore.1.0.5",
            "Assets/Packages/VRChat.OSCQuery.0.0.7",
            "Assets/Packages/MeaMod.DNS.1.0.70",
            "Assets/Packages/Microsoft.Extensions.Logging.Abstractions.6.0.2"
        };

        private const string ArtifactDirectory = "Artifacts";
        private static readonly string OutputPath =
            GetCommandLineArg("-exportPath") ??
            Environment.GetEnvironmentVariable("UNITY_EXPORT_PATH") ??
            "Artifacts/oscquery-animation-debugger.unitypackage";

        [MenuItem("Tools/ExportPackage")]
        public static void Export()
        {
            if (!AssetDatabase.IsValidFolder(ExportRoot))
            {
                Debug.LogError($"Export root not found: {ExportRoot}");
                return;
            }

            Directory.CreateDirectory(ArtifactDirectory);
            AssetDatabase.Refresh();

            var searchRoots = new List<string>();
            if (AssetDatabase.IsValidFolder(ExportRoot))
            {
                searchRoots.Add(ExportRoot);
            }

            foreach (var root in AdditionalSearchRoots)
            {
                if (AssetDatabase.IsValidFolder(root))
                {
                    searchRoots.Add(root);
                }
            }

            var exportPathSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in searchRoots)
            {
                foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { root }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        exportPathSet.Add(path);
                    }
                }
            }

            var exportPaths = new List<string>(exportPathSet);

            if (exportPaths.Count == 0)
            {
                Debug.LogError("No export assets found from configured search roots.");
                return;
            }

            AssetDatabase.ExportPackage(
                exportPaths.ToArray(),
                OutputPath,
                ExportPackageOptions.Recurse);

            Debug.Log($"Exported unitypackage: {OutputPath} (paths={exportPaths.Count})");
        }
    }
}
