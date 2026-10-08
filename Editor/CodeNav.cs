#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Titan.TrackingQA
{
    /// <summary>Mở vị trí trong báo cáo (đường dẫn tương đối với project): .cs → IDE đúng dòng; prefab / scene / anim → chọn trong Project.</summary>
    static class CodeNav
    {
        static readonly Regex PackageCache = new Regex(@"^Library/PackageCache/([^/@]+)@[^/]+/(.*)$");

        /// <summary>"Library/PackageCache/com.titan.ads@98472be8ad8a/Runtime/X.cs" → "Packages/com.titan.ads/Runtime/X.cs".</summary>
        public static string? AssetPath(string file)
        {
            file = file.Replace('\\', '/');
            if (file.StartsWith("Assets/", StringComparison.Ordinal) || file.StartsWith("Packages/", StringComparison.Ordinal)) return file;
            var m = PackageCache.Match(file);
            return m.Success ? $"Packages/{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
        }

        public static string FullPath(string file) => Path.Combine(QaRunner.ProjectRoot, file.Replace('/', Path.DirectorySeparatorChar));

        public static void Open(string file, int line)
        {
            var asset = AssetPath(file);
            var obj = asset != null ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(asset) : null;
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (obj != null && AssetDatabase.OpenAsset(obj, Math.Max(1, line))) return;
                InternalEditorUtility.OpenFileAtLineExternal(FullPath(file), Math.Max(1, line));
                return;
            }
            if (obj != null)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
            else EditorUtility.RevealInFinder(FullPath(file));
        }

        /// <summary>Vài dòng code quanh vị trí (dòng trúng có dấu ▶).</summary>
        public static string? Snippet(string file, int line, int around = 3)
        {
            try
            {
                var p = FullPath(file);
                if (line <= 0 || !File.Exists(p)) return null;
                var lines = File.ReadAllLines(p);
                int s = Math.Max(1, line - around), e = Math.Min(lines.Length, line + around);
                var sb = new System.Text.StringBuilder();
                for (int i = s; i <= e; i++)
                    sb.Append(i == line ? "▶ " : "   ").Append(i.ToString().PadLeft(4)).Append("  ").AppendLine(lines[i - 1].TrimEnd().Replace("\t", "    "));
                return sb.ToString().TrimEnd();
            }
            catch { return null; }
        }
    }
}
