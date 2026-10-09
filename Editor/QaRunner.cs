#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrackingChecker.Core;
using TrackingChecker.Core.Ai;
using TrackingChecker.Core.Analysis;
using TrackingChecker.Core.Live;
using TrackingChecker.Core.Report;
using TrackingChecker.Core.Rules;
using UnityEditor;
using UnityEngine;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Chạy Check all / chạy tiếp điểm mù ngầm (không khoá Editor) và giữ kết quả.
    /// Báo cáo gần nhất lưu ở Library/TrackingQA/last-report.json → đóng mở Unity vẫn xem lại được.
    /// Phiên phân tích (để chạy tiếp điểm mù) chỉ giữ trong bộ nhớ: mất khi Unity nạp lại script.
    /// </summary>
    static class QaRunner
    {
        const string PrefDoc = "Titan.TrackingQA.DocPath.";
        const string PrefPlatform = "Titan.TrackingQA.Platform";
        const string PrefClaude = "Titan.TrackingQA.UseClaude";

        /// <summary>Check all có gửi cho Claude review không (Claude Code trên máy, gói Claude đang đăng nhập) — tắt mặc định, lưu theo máy.</summary>
        public static bool UseClaude
        {
            get => EditorPrefs.GetBool(PrefClaude, false);
            set => EditorPrefs.SetBool(PrefClaude, value);
        }

        /// <summary>Tình trạng Claude Code trên máy (null = đang kiểm).</summary>
        public static ClaudeCodeRunner.Status? ClaudeStatus { get; private set; }
        public static bool ClaudeReady => ClaudeStatus is { Found: true, LoggedIn: true };

        public static void RefreshClaudeStatus()
        {
            Task.Run(async () =>
            {
                ClaudeCodeRunner.Status s;
                try { s = await ClaudeCodeRunner.GetStatusAsync(); }
                catch (Exception e) { s = new ClaudeCodeRunner.Status(false, null, null, false, null, e.Message); }
                Post(() => ClaudeStatus = s);
            });
        }

        static AiSettings Ai() => new AiSettings { Provider = "claude-code", CacheDir = Path.Combine(WorkDir, "ai-cache") };

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath)!;
        public static string WorkDir => Path.Combine(ProjectRoot, "Library", "TrackingQA");
        static string LastReportFile => Path.Combine(WorkDir, "last-report.json");
        static string ProjectKey => ProjectRoot.ToLowerInvariant().GetHashCode().ToString("x");

        /// <summary>Mỗi project nhớ file tracking của nó (lưu theo máy).</summary>
        public static string DocPath
        {
            get => EditorPrefs.GetString(PrefDoc + ProjectKey, "");
            set => EditorPrefs.SetString(PrefDoc + ProjectKey, value);
        }

        public static BuildPlatform Platform
        {
            get => EditorPrefs.GetInt(PrefPlatform, 0) == 1 ? BuildPlatform.iOS : BuildPlatform.Android;
            set => EditorPrefs.SetInt(PrefPlatform, value == BuildPlatform.iOS ? 1 : 0);
        }

        public static CheckReport? Report { get; private set; }
        /// <summary>Tăng mỗi khi báo cáo đổi (check mới, đúng thiết kế…) — cửa sổ chỉ vẽ lại danh sách khi số này đổi.</summary>
        public static int Version { get; private set; }
        public static CheckSession? Session { get; private set; }
        public static bool Running { get; private set; }
        public static string Status { get; private set; } = "";
        public static readonly List<string> LogLines = new List<string>();

        /// <summary>Báo cho cửa sổ vẽ lại (luôn gọi trên main thread).</summary>
        public static event Action? Changed;

        static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        static CancellationTokenSource? _cts;
        static bool _loaded;

        [InitializeOnLoadMethod]
        static void Init()
        {
            EditorApplication.update += Pump;
            SetupKnowledge();
            RefreshClaudeStatus();
        }

        static void Pump()
        {
            bool any = false;
            while (MainThread.TryDequeue(out var a)) { a(); any = true; }
            if (any) Changed?.Invoke();
        }

        /// <summary>Chạy việc trên luồng chính của Unity (từ luồng nền).</summary>
        public static void Post(Action a) => MainThread.Enqueue(a);

        static void Log(string s) => Post(() =>
        {
            LogLines.Add(DateTime.Now.ToString("HH:mm:ss  ") + s);
            if (LogLines.Count > 500) LogLines.RemoveRange(0, LogLines.Count - 500);
            Status = s;
        });

        /// <summary>Kho kiến thức: Knowledge/ đi kèm package + chỉnh sửa trên máy (UserSettings, không lên git của game).</summary>
        static void SetupKnowledge()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(QaRunner).Assembly);
            var dir = info != null ? Path.Combine(info.resolvedPath, "Knowledge") : null;
            if (dir != null && Directory.Exists(Path.Combine(dir, "common"))) Knowledge.SharedDir = dir;
            Knowledge.LocalDir = Path.Combine(ProjectRoot, "UserSettings", "TrackingQA", "knowledge-local");
        }

        public static string? GameId => UnityProject.ReadGameId(ProjectRoot);

        /// <summary>Đọc báo cáo gần nhất (lần đầu mở cửa sổ sau khi mở Unity).</summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            // Package đã cập nhật (PR kho chung đã merge) → dọn bản trên máy đã có đủ trong kho chung
            try { KnowledgeShare.CleanLocal(GameId); } catch (Exception e) { Debug.LogWarning("[Tracking QA] Không dọn được bản trên máy: " + e.Message); }
            if (Report != null || !File.Exists(LastReportFile)) return;
            try
            {
                Report = JsonSerializer.Deserialize<CheckReport>(File.ReadAllText(LastReportFile));
                if (Report != null)
                {
                    RecordFeedback.Apply(Report, RecordFeedback.Load(RecordController.FeedbackPath));
                    RecordController.Issues.MergeInto(Report);
                    GameData.ApplyExceptions(Report); // ngoại lệ có thể đã đổi từ lần trước
                    CheckCodeChanges(notify: false);
                }
                Version++;
            }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không đọc được báo cáo cũ: " + e.Message); }
        }

        static void Save()
        {
            if (Report == null) return;
            try
            {
                Directory.CreateDirectory(WorkDir);
                File.WriteAllText(LastReportFile, JsonSerializer.Serialize(Report), Encoding.UTF8);
            }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không lưu được báo cáo: " + e.Message); }
        }

        public static void CheckAll(string docPath, BuildPlatform platform)
        {
            // Đọc cài đặt / API Unity trên luồng chính (EditorPrefs, đường dẫn…) — phần chạy ngầm không được gọi API Unity
            var useAi = UseClaude && ClaudeReady;
            var req = new CheckRequest
            {
                RepoPath = ProjectRoot, SpecPath = docPath, Platform = platform, UseAi = useAi,
                UnityInstallPath = Path.GetDirectoryName(EditorApplication.applicationContentsPath),
            };
            var ai = useAi ? Ai() : null;
            Run("Check all", ct => CheckRunner.RunSessionAsync(req, ai, Log, ct), s => { Session = s; return s.Report; });
        }

        /// <summary>Có thể gửi báo cáo đang xem cho Claude review (còn phiên phân tích trong bộ nhớ, chưa nạp lại script).</summary>
        public static bool CanReview => Session != null && Session.Report == Report && ClaudeReady && !Running;

        /// <summary>Claude review báo cáo đang xem (không phân tích lại code): xác nhận / bác bỏ mục nghi ngờ, tìm thêm lỗi ngữ nghĩa.</summary>
        public static void ReviewWithClaude()
        {
            var s = Session;
            if (s == null) return;
            var ai = Ai(); // trên luồng chính
            Run("Claude review", async ct =>
            {
                using var _ = Knowledge.ForGame(s.Report.GameId);
                await new AiReviewer(ai, Log).ReviewAsync(s.Report, s.Analysis, s.Spec, ct);
                Explainer.ApplyToAiFindings(s.Report, s.Analysis);
                CaseEngine.Apply(s.Report, s.Spec);
                return s.Report;
            }, r => r);
        }

        public static bool CanContinue => Session != null && Session.Report == Report && Report?.Coverage.Truncated > 0;

        public static void ContinueBlindSpots()
        {
            var s = Session;
            if (s == null) return;
            Run("Chạy tiếp điểm mù", ct => CheckRunner.ContinueAsync(s, Log, ct), r => r);
        }

        static void Run<T>(string what, Func<CancellationToken, Task<T>> work, Func<T, CheckReport> result)
        {
            if (Running) return;
            Running = true;
            LogLines.Clear();
            Status = what + "…";
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var started = DateTime.Now;
            // Không cho Unity nạp lại script khi đang chạy (nạp lại sẽ huỷ luồng phân tích giữa chừng)
            EditorApplication.LockReloadAssemblies();
            Changed?.Invoke();

            Task.Run(async () =>
            {
                try
                {
                    var r = result(await work(ct));
                    Post(() =>
                    {
                        // So với lần check trước (mục mới / đã sửa), băm file code có mục báo (để báo "code đã đổi")
                        try { if (!ReferenceEquals(Report, r)) { ReportDiff.Compare(Report, r); ReportDiff.HashFiles(r); } }
                        catch (Exception e) { Debug.LogWarning("[Tracking QA] Không so được với lần check trước: " + e.Message); }
                        // Ghép kết quả Record (xác nhận / bác bỏ / không tái hiện) — lưu theo mã ổn định nên giữ qua các lần Check all;
                        // và các lỗi Record còn mở (bảng Lỗi Record) để xem chung
                        try
                        {
                            RecordFeedback.Apply(r, RecordFeedback.Load(RecordController.FeedbackPath));
                            RecordController.Issues.MergeInto(r);
                            GameData.ApplyExceptions(r);
                        }
                        catch (Exception e) { Debug.LogWarning("[Tracking QA] Không ghép được kết quả Record: " + e.Message); }
                        Report = r;
                        CheckCodeChanges(notify: false);
                        Version++;
                        Save();
                        Status = Summary(r) + $" · {(DateTime.Now - started).TotalSeconds:F0}s";
                    });
                }
                catch (OperationCanceledException) { Post(() => Status = "Đã huỷ."); }
                catch (Exception e)
                {
                    Post(() => { Status = "Lỗi: " + e.Message; LogLines.Add("LỖI: " + e); });
                    Debug.LogException(e);
                }
                finally
                {
                    Post(() =>
                    {
                        Running = false;
                        EditorApplication.UnlockReloadAssemblies();
                    });
                }
            });
        }

        public static void Cancel() => _cts?.Cancel();

        public static string Summary(CheckReport r)
        {
            int N(FindingCategory c) => r.Findings.Count(f => f.Category == c && f.IsOpen);
            return $"{N(FindingCategory.CodeError)} lỗi · {N(FindingCategory.Suspect)} nghi ngờ · {N(FindingCategory.Missing)} thiếu · {N(FindingCategory.DocIssue)} lỗi doc";
        }

        /// <summary>Record có kết quả đối chiếu mới → cập nhật báo cáo đang xem (cửa sổ CheckAll vẽ lại).</summary>
        public static void ApplyRecordFeedback(RecordFeedback fb)
        {
            if (Report == null) return;
            RecordFeedback.Apply(Report, fb);
            Version++;
            Save();
            Changed?.Invoke();
        }

        /// <summary>Kiến thức trên máy đổi (dọn sau khi kho chung đã có, đổi mã case…) → gắn lại case + ngoại lệ cho báo cáo đang xem.</summary>
        public static void ReapplyKnowledge()
        {
            Knowledge.ClearCache();
            if (Report == null) return;
            CaseEngine.Retag(Report);
            GameData.ApplyExceptions(Report);
            Version++;
            Save();
            Changed?.Invoke();
        }

        /// <summary>Bảng Lỗi Record đổi → đưa lại các lỗi còn mở vào báo cáo đang xem.</summary>
        public static void MergeRecordIssues()
        {
            if (Report == null) return;
            RecordController.Issues.MergeInto(Report);
            GameData.ApplyExceptions(Report);
            CheckCodeChanges(notify: false);
            Version++;
            Save();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ code đã đổi → có thể đã sửa

        static readonly Dictionary<string, (DateTime Time, string? Hash)> Hashes = new Dictionary<string, (DateTime, string?)>();

        /// <summary>Nội dung file code (đường dẫn tương đối project) — để biết hàm trên đường gọi còn không.</summary>
        static string? ReadText(string rel)
        {
            try { var p = Path.Combine(ProjectRoot, rel); return File.Exists(p) ? File.ReadAllText(p) : null; }
            catch { return null; }
        }

        /// <summary>Mã băm file code (đường dẫn tương đối project) — nhớ theo thời điểm sửa file để không băm lại mỗi event.</summary>
        public static string? FileHash(string rel)
        {
            var full = Path.Combine(ProjectRoot, rel);
            var t = File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTime.MinValue;
            if (Hashes.TryGetValue(rel, out var h) && h.Time == t) return h.Hash;
            var hash = ReportDiff.Hash(ProjectRoot, rel);
            Hashes[rel] = (t, hash);
            return hash;
        }

        /// <summary>Đánh dấu mục (Check all + lỗi Record) có file code đã đổi từ lúc check / lúc gặp — gọi khi mở / quay lại cửa sổ.</summary>
        public static void CheckCodeChanges(bool notify = true)
        {
            bool changed;
            try
            {
                // Lỗi Record: so mọi file trên đường gọi với lúc gặp → vẫn còn / code đã đổi / có thể đã sửa (không cần chơi lại)
                changed = RecordController.Issues.MarkCodeChanged(FileHash, ReadText);
                if (changed) RecordController.Touch();
                if (Report == null) return;
                changed |= ReportDiff.MarkChanged(Report);
                foreach (var f in Report.Findings.Where(f => f.RecordIssueId != null))
                {
                    if (RecordController.Issues.Items.FirstOrDefault(x => x.Id == f.RecordIssueId) is not { } it) continue;
                    var status = RecordIssueLog.StatusLabel(it.Status) + (it.CodeStateLabel != null ? " · " + it.CodeStateLabel : "");
                    if (f.CodeChanged != it.CodeChanged || f.RecordStatus != status) { f.CodeChanged = it.CodeChanged; f.RecordStatus = status; changed = true; }
                }
            }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không kiểm được thay đổi code: " + e.Message); return; }
            if (!changed) return;
            Version++;
            if (notify) Changed?.Invoke();
        }

        // ------------------------------------------------------------------ bảng Lỗi Record

        public static void SetRecordStatus(string id, RecordIssueStatus status, string? note = null)
        {
            RecordController.Issues.SetStatus(id, status, Environment.UserName, note);
            RecordController.SaveIssues();
            MergeRecordIssues();
            RecordController.Touch();
        }

        // ------------------------------------------------------------------ case riêng game (G-xxx)

        /// <summary>Lưu mục thành case riêng của game (G-xxx, trên máy) và gắn lại mã case. Trả về mã case.</summary>
        public static string? SaveCase(Finding f, string title, string? why)
        {
            var game = Report?.GameId ?? GameId;
            if (game == null) return null;
            string id;
            using (Knowledge.ForGame(game)) id = GameData.SaveCase(game, f, title, why, Environment.UserName);
            if (f.RecordIssueId != null && RecordController.Issues.Items.FirstOrDefault(x => x.Id == f.RecordIssueId) is { } it)
            {
                it.SavedCaseId = id;
                RecordController.SaveIssues();
                RecordController.Touch();
            }
            if (Report != null)
            {
                CaseEngine.Retag(Report);
                GameData.ApplyExceptions(Report);
                Version++;
                Save();
                Changed?.Invoke();
            }
            return id;
        }

        // ------------------------------------------------------------------ Đúng thiết kế / Check all báo nhầm

        /// <summary>Lưu ngoại lệ của game. kind = "false_positive" khi Check all báo nhầm (cause = vì sao, để học); null = đúng thiết kế.</summary>
        public static void Accept(Finding f, string reason, string? kind = null, string? cause = null)
        {
            var game = Report?.GameId ?? GameId;
            if (game == null) return;
            GameData.Accept(game, f, reason, Environment.UserName, kind, cause);
            RecordController.Touch();
            if (Report == null) return;
            GameData.ApplyExceptions(Report);
            Version++;
            Save();
            Changed?.Invoke();
        }

        public static void Unaccept(Finding f)
        {
            var game = Report?.GameId ?? GameId;
            if (game == null) return;
            GameData.Unaccept(game, f.StableKey());
            RecordController.Touch();
            if (Report == null) return;
            GameData.ApplyExceptions(Report);
            Version++;
            Save();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ xuất

        public static string? WriteHtml()
        {
            if (Report == null) return null;
            Directory.CreateDirectory(WorkDir);
            var p = Path.Combine(WorkDir, "report.html");
            File.WriteAllText(p, HtmlReport.Render(Report), Encoding.UTF8);
            return p;
        }

    }

    /// <summary>
    /// Kích hoạt Check all không cần bấm (thử nghiệm / tự động hoá): tạo file Library/TrackingQA/run.request
    /// chứa đường dẫn file tracking (dòng 1) và nền tảng (dòng 2, tuỳ chọn). Kết quả: last-report.json + spike-result.txt.
    /// </summary>
    [InitializeOnLoad]
    static class RunRequestWatcher
    {
        static double _next;
        static DateTime _startedAt;
        static bool _waiting;

        static RunRequestWatcher() => EditorApplication.update += Poll;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 2;

            if (_waiting && !QaRunner.Running)
            {
                _waiting = false;
                var r = QaRunner.Report;
                var text = new StringBuilder()
                    .AppendLine(r != null && r.CreatedAt >= _startedAt ? "OK" : "ERROR")
                    .AppendLine(QaRunner.Status)
                    .AppendLine(r != null ? $"Findings: {r.Findings.Count}, emissions: {r.EmissionCount}, files: {r.FilesAnalyzed}" : "")
                    .AppendLine($"Thời gian: {(DateTime.Now - _startedAt).TotalSeconds:F1}s")
                    .AppendLine("Xuất: " + TryExport())
                    .AppendLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                try { File.WriteAllText(Path.Combine(QaRunner.WorkDir, "spike-result.txt"), text.ToString(), Encoding.UTF8); } catch { }
            }

            var f = Path.Combine(QaRunner.WorkDir, "run.request");
            if (_waiting) return;
            if (QaRunner.Running || !File.Exists(f)) return;
            string[] lines;
            try { lines = File.ReadAllLines(f); File.Delete(f); } catch { return; }
            if (lines.Length == 0 || !File.Exists(lines[0].Trim())) return;
            var platform = lines.Length > 1 && lines[1].Trim().Equals("iOS", StringComparison.OrdinalIgnoreCase) ? BuildPlatform.iOS : BuildPlatform.Android;
            Debug.Log("[Tracking QA] Chạy Check all theo yêu cầu: " + lines[0]);
            _startedAt = DateTime.Now;
            _waiting = true;
            QaRunner.CheckAll(lines[0].Trim(), platform);
        }

        /// <summary>Xuất thử HTML (kiểm tra ghi file chạy được trong Unity).</summary>
        static string TryExport()
        {
            try
            {
                if (QaRunner.Report == null) return "không có báo cáo";
                QaRunner.WriteHtml();
                return "report.html OK";
            }
            catch (Exception e) { return "LỖI " + e; }
        }
    }
}
