using System;
using UnityEditor;
using UnityEngine;


namespace Unity.Networking.Editor
{
    [Obsolete("RoslynAnalyzerFix does not serve a purpose anymore and will be removed in a future version.")]
    public class RoslynAnalyzerFix : AssetPostprocessor
    {
        public static string OnGeneratedCSProject(string path, string content)
        {
            return content;
        }
    }
}
