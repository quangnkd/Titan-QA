#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TrackingChecker.Core;
using TrackingChecker.Core.Analysis;
using TrackingChecker.Core.Report;
using TrackingChecker.Core.Rules;
using UnityEditor;
using UnityEngine;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Chạy Check all không mở giao diện (máy build / CI, hoặc thử package trên game khác):
    ///   Unity.exe -batchmode -projectPath &lt;game&gt; -executeMethod Titan.TrackingQA.Batch.CheckAll -trackingDoc &lt;plan.xlsx&gt; [-trackingPlatform iOS] [-trackingOut &lt;thư mục&gt;] -logFile -
    /// Kết quả: report.json + report.html + summary.txt (mặc định Library/TrackingQA/batch). Mã thoát: 0 = không có Lỗi, 2 = có Lỗi, 1 = không chạy được.
    /// Kèm kiểm tra Record gắn được vào Titan của game này không (TrackingManager có đủ hàm đăng ký dịch vụ).
    /// </summary>
    public static class Batch
    {
        public static void CheckAll()
        {
            var args = Environment.GetCommandLineArgs();
            string? Arg(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var doc = Arg("-trackingDoc") ?? QaRunner.DocPath;
            var outDir = Arg("-trackingOut") ?? Path.Combine(QaRunner.WorkDir, "batch");
            var platform = string.Equals(Arg("-trackingPlatform"), "iOS", StringComparison.OrdinalIgnoreCase) ? BuildPlatform.iOS : BuildPlatform.Android;
            int code;
            try
            {
                if (string.IsNullOrEmpty(doc) || !File.Exists(doc)) throw new FileNotFoundException("Không thấy file tracking (-trackingDoc): " + doc);
                Directory.CreateDirectory(outDir);
                Debug.Log($"[Tracking QA] Check all (batch): {QaRunner.ProjectRoot} · {doc} · {platform}");
                // Chạy trong Task.Run: luồng chính của Unity đang chờ → không để phần tiếp theo của await quay về luồng chính (treo)
                var session = Task.Run(() => CheckRunner.RunSessionAsync(new CheckRequest
                {
                    RepoPath = QaRunner.ProjectRoot, SpecPath = doc, Platform = platform, UseAi = false,
                    UnityInstallPath = Path.GetDirectoryName(EditorApplication.applicationContentsPath),
                }, null, s => Console.WriteLine("[Tracking QA] " + s))).GetAwaiter().GetResult();
                var r = session.Report;
                File.WriteAllText(Path.Combine(outDir, "report.json"), JsonSerializer.Serialize(r), Encoding.UTF8);
                File.WriteAllText(Path.Combine(outDir, "report.html"), HtmlReport.Render(r), Encoding.UTF8);
                var hook = RecordHook();
                var sb = new StringBuilder()
                    .AppendLine($"Game: {r.GameId} · doc: {Path.GetFileName(doc)} · {r.Platform} · Unity {r.UnityVersion}")
                    .AppendLine(QaRunner.Summary(r))
                    .AppendLine($"Mục (gồm đã ẩn / chấp nhận): {r.Findings.Count} · luồng bắn: {r.EmissionCount} · file: {r.FilesAnalyzed} · {r.AnalysisSeconds:F0}s")
                    .AppendLine($"Theo case: {string.Join(" · ", r.Cases.GroupBy(c => CaseEngine.StatusLabel(c.Status)).Select(g => $"{g.Key} {g.Count()}"))}")
                    .AppendLine($"Kịch bản: {r.Scenarios.Count} · độ phủ: {r.Coverage.Verdict}")
                    .AppendLine("Record: " + hook)
                    .AppendLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                foreach (var f in r.Findings.Where(f => f.IsOpen && f.Category == FindingCategory.CodeError))
                    sb.AppendLine($"  {f.Id} {string.Join(",", f.CaseIds)} {f.Title}");
                File.WriteAllText(Path.Combine(outDir, "summary.txt"), sb.ToString(), Encoding.UTF8);
                Console.WriteLine(sb.ToString());
                code = r.Findings.Any(f => f.IsOpen && f.Category == FindingCategory.CodeError) ? 2 : 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("[Tracking QA] LỖI: " + e);
                try { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "summary.txt"), "LỖI: " + e, Encoding.UTF8); } catch { }
                code = 1;
            }
            EditorApplication.Exit(code);
        }

        /// <summary>Record gắn vào Titan được không: TrackingManager có đủ 3 hàm đăng ký dịch vụ tracking.</summary>
        static string RecordHook()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Titan.Tracking.TrackingManager", false)).FirstOrDefault(t => t != null);
            if (type == null) return "KHÔNG có Titan.Tracking.TrackingManager — Record không nghe được event";
            var need = new[] { "RegisterTrackEventService", "RegisterTrackPropertyService", "RegisterTrackItemsService" };
            var miss = need.Where(m => type.GetMethod(m, BindingFlags.Public | BindingFlags.Instance) == null).ToList();
            return miss.Count == 0 ? "gắn được (TrackingManager có đủ RegisterTrackEvent / Property / ItemsService)" : "thiếu " + string.Join(", ", miss);
        }
    }
}
