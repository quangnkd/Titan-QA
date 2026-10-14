#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TrackingChecker.Core;
using TrackingChecker.Core.Ai;
using TrackingChecker.Core.Rules;
using UnityEditor;
using UnityEngine;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Nhờ Claude viết luật tự động cho case Kiểm tay: mở 1 cửa sổ Claude Code (phiên thường, bạn duyệt từng lần sửa file / chạy lệnh / push)
    /// trong repo lõi TrackingChecker, giao sẵn yêu cầu. Hỏi sau Check all (khi bật Claude) hoặc bấm trong Kho chung → Kho case.
    /// </summary>
    static class RuleRequest
    {
        const string PrefRepo = "Titan.TrackingQA.CoreRepo";
        static string StatePath => Path.Combine(QaRunner.ProjectRoot, "UserSettings", "TrackingQA", "rule-requests.json");
        static string PromptDir => Path.Combine(QaRunner.WorkDir, "rule-requests");

        /// <summary>Repo lõi đã chọn (để trống = tự tìm thư mục TrackingChecker cạnh project game).</summary>
        public static string ConfiguredRepo
        {
            get => EditorPrefs.GetString(PrefRepo, "");
            set => EditorPrefs.SetString(PrefRepo, value.Trim());
        }

        public static string? CoreRepo => RuleWriter.FindCoreRepo(QaRunner.ProjectRoot, ConfiguredRepo);

        /// <summary>Case Kiểm tay đang có hiệu lực của game.</summary>
        public static List<TrackingCase> Manual(string? game)
        {
            using (Knowledge.ForGame(game)) return CaseStore.Active(game).Where(RuleWriter.IsManual).ToList();
        }

        /// <summary>Lần gần nhất đã nhờ Claude viết luật cho case (null = chưa).</summary>
        public static DateTime? RequestedAt(string caseId) => Load().TryGetValue(caseId, out var t) ? t : (DateTime?)null;

        /// <summary>Sau Check all (đang bật Claude): có case Kiểm tay chưa nhờ viết luật → hỏi có mở Claude Code không.</summary>
        public static void OfferAfterCheck()
        {
            if (Application.isBatchMode || !QaRunner.ClaudeReady) return;
            var game = QaRunner.GameId;
            var asked = Load();
            var cases = Manual(game).Where(c => !asked.ContainsKey(c.Id)).ToList();
            if (cases.Count == 0) return;
            var repo = CoreRepo;
            if (repo == null)
            {
                Debug.Log($"[Tracking QA] Có {cases.Count} case Kiểm tay chưa có luật tự động ({string.Join(", ", cases.Select(c => c.Id))}). " +
                          "Để Claude viết luật: chọn repo lõi TrackingChecker ở cửa sổ Kho chung → Kho case.");
                return;
            }
            Ask(cases, repo, game);
        }

        /// <summary>Bấm trong Kho case: nhờ viết luật cho mọi case Kiểm tay (kể cả case đã nhờ trước đó).</summary>
        public static void StartManual()
        {
            var game = QaRunner.GameId;
            var repo = CoreRepo;
            if (repo == null) { EditorUtility.DisplayDialog("Claude viết luật", "Chưa thấy repo lõi TrackingChecker trên máy này — chọn thư mục repo trong Kho case.", "OK"); return; }
            var cases = Manual(game);
            if (cases.Count == 0) { EditorUtility.DisplayDialog("Claude viết luật", "Không có case Kiểm tay nào cần viết luật.", "OK"); return; }
            Ask(cases, repo, game);
        }

        static void Ask(List<TrackingCase> cases, string repo, string? game)
        {
            var list = string.Join("\n", cases.Take(8).Select(c => $"• {c.Id}: {Short(c.Title, 70)}")) + (cases.Count > 8 ? $"\n… và {cases.Count - 8} case khác" : "");
            if (!EditorUtility.DisplayDialog("Claude viết luật",
                    $"Có {cases.Count} case Kiểm tay chưa có luật tự động:\n{list}\n\n" +
                    $"Mở Claude Code trong repo lõi:\n{repo}\n\n" +
                    "Yêu cầu đã giao sẵn: viết luật + test, chạy bộ game chuẩn, commit vào nhánh mới. " +
                    "Mỗi lần sửa file / chạy lệnh Claude đều hỏi bạn; push và tạo PR chỉ khi bạn đồng ý.",
                    "Mở Claude Code", "Để sau")) return;
            Open(cases, repo, game);
        }

        static void Open(List<TrackingCase> cases, string repo, string? game)
        {
            var exe = QaRunner.ClaudeStatus?.Path ?? ClaudeCodeRunner.Find();
            if (exe == null) { EditorUtility.DisplayDialog("Claude viết luật", "Không tìm thấy Claude Code trên máy.", "OK"); return; }
            try
            {
                Directory.CreateDirectory(PromptDir);
                var file = Path.Combine(PromptDir, $"viet-luat-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                File.WriteAllText(file, RuleWriter.BuildPrompt(cases, game), new UTF8Encoding(false));
                ClaudeCodeRunner.OpenInteractive(exe, repo, file, "Claude - viet luat Tracking QA");
                var asked = Load();
                foreach (var c in cases) asked[c.Id] = DateTime.Now;
                Save(asked);
                Debug.Log($"[Tracking QA] Đã mở Claude Code trong {repo} để viết luật cho {string.Join(", ", cases.Select(c => c.Id))}.");
            }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không mở được Claude Code: " + e.Message); }
        }

        static Dictionary<string, DateTime> Load()
        {
            try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(StatePath)) ?? new Dictionary<string, DateTime>() : new Dictionary<string, DateTime>(); }
            catch { return new Dictionary<string, DateTime>(); }
        }

        static void Save(Dictionary<string, DateTime> d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
