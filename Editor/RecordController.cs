#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TrackingChecker.Core;
using TrackingChecker.Core.Analysis;
using TrackingChecker.Core.Live;
using TrackingChecker.Core.Rules;
using TrackingChecker.Core.Spec;
using UnityEditor;
using UnityEngine;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Record: bấm Play là bắt đầu 1 phiên, nghe mọi event / property / items game bắn qua Titan và các bước chơi (bấm nút, vào scene)
    /// qua RecordBus; kiểm ngay theo doc (LiveChecker) và theo kho case (LiveSequence); bỏ qua lỗi Check all đã báo;
    /// đối chiếu với Check all (xác nhận / bác bỏ / không tái hiện — RecordFeedback, cập nhật vào cửa sổ CheckAll);
    /// tự lưu phiên vào UserSettings/TrackingQA/record (ghi dần trong lúc chơi). Thoát Play thì kết thúc phiên. Không dùng AI.
    /// </summary>
    [InitializeOnLoad]
    static class RecordController
    {
        const string PrefAuto = "Titan.TrackingQA.AutoRecord";
        const int KeepSessions = 50;

        public static RecordSession? Current { get; private set; }
        public static bool Live => Current != null && EditorApplication.isPlaying && Current.EndedAt == null;
        /// <summary>Tăng mỗi khi phiên đang xem đổi — cửa sổ vẽ lại theo số này.</summary>
        public static int Version { get; private set; }
        /// <summary>Nhắc khi kiểm không đầy đủ (chưa chọn doc, chưa có Check all…).</summary>
        public static List<string> Notices { get; } = new List<string>();

        public static string Dir => Path.Combine(QaRunner.ProjectRoot, "UserSettings", "TrackingQA", "record");
        public static string FeedbackPath => Path.Combine(QaRunner.ProjectRoot, "UserSettings", "TrackingQA", "record-feedback.json");
        public static string IssuesPath => Path.Combine(QaRunner.ProjectRoot, "UserSettings", "TrackingQA", "record-issues.json");

        static RecordIssueLog? _issues;
        /// <summary>Bảng Lỗi Record: lỗi mới (Check all chưa báo) cộng dồn qua mọi phiên, có số lần và trạng thái.</summary>
        public static RecordIssueLog Issues => _issues ??= RecordIssueLog.Load(IssuesPath);
        static bool _issuesDirty;

        public static void SaveIssues()
        {
            try { Issues.Save(IssuesPath); _issuesDirty = false; }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không lưu được bảng Lỗi Record: " + e.Message); }
        }

        /// <summary>Báo cửa sổ Record vẽ lại (bảng Lỗi Record / ngoại lệ đổi).</summary>
        public static void Touch() => Version++;

        public static bool AutoRecord
        {
            get => EditorPrefs.GetBool(PrefAuto, true);
            set { EditorPrefs.SetBool(PrefAuto, value); RecordBus.Enabled = value; }
        }

        static LiveChecker? _checker;
        static LiveSequence? _sequence;
        static CheckReport? _known;
        static RecordFeedback? _feedback;
        static string? _gameId;
        static bool _dirty, _feedbackDirty;
        static double _nextSave;
        static int _steps;

        /// <summary>Lời gọi / bước chơi của frame đang chạy — xử lý khi frame xong để cú bấm đứng trước các event nó gây ra
        /// (người nghe của QA được gắn sau nên chạy sau code game).</summary>
        static readonly List<object> Pending = new List<object>();

        static RecordController()
        {
            // Chạy sau mỗi lần nạp lại script (kể cả lúc vào Play) — trước khi scene đầu chạy, nên đặt cờ kịp cho TitanHook
            RecordBus.Enabled = AutoRecord;
            RecordBus.PlayStarted += Start;
            RecordBus.Received += c => Queue(c, c.Frame);
            RecordBus.StepReceived += s => Queue(s, s.Frame);
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode || s == PlayModeStateChange.EnteredEditMode) End(); };
            EditorApplication.update += Tick;
            // Nạp lại script giữa phiên (Enter Play Mode không reload) → các lời gọi đã nghe vẫn còn trong bus
            if (EditorApplication.isPlaying && RecordBus.Enabled && RecordBus.Calls.Count > 0 && Current == null)
            {
                Start();
                foreach (var c in RecordBus.Calls.ToList()) Queue(c, c.Frame);
                Flush();
            }
        }

        static void Start()
        {
            QaRunner.EnsureLoaded();
            var now = DateTime.Now;
            _gameId = QaRunner.GameId;
            Notices.Clear();
            Pending.Clear();
            _steps = 0;
            Current = new RecordSession
            {
                Id = RecordStore.NewId(now), StartedAt = now, GameId = _gameId,
                Game = Application.productName, PackageVersion = PackageVersion(),
            };
            _checker = null;
            _sequence = null;
            _known = null;
            _feedback = RecordFeedback.Load(FeedbackPath);
            var doc = QaRunner.DocPath;
            if (string.IsNullOrEmpty(doc) || !File.Exists(doc))
                Notices.Add("Chưa chọn file tracking (Tracking QA CheckAll → Chọn file tracking…) — chỉ ghi event, chưa kiểm theo doc.");
            else
            {
                try
                {
                    using var _ = Knowledge.ForGame(_gameId);
                    var spec = ExcelSpecReader.Read(doc);
                    if (_gameId != null && GameData.LoadOverlay(_gameId, doc) is { IsEmpty: false } ov) ov.Apply(spec);
                    _known = QaRunner.Report is { } r && SamePath(r.SpecFile, doc) ? r : null;
                    if (_known == null) Notices.Add("Chưa có Check all cho doc này — lỗi lúc chơi có thể trùng lỗi Check all sẽ báo, và chưa đối chiếu được với Check all.");
                    else Current.KnownReportAt = _known.CreatedAt;
                    _checker = new LiveChecker(spec, _known);
                    _sequence = new LiveSequence(spec);
                    Current.DocFile = Path.GetFileName(doc);
                }
                catch (Exception e) { Notices.Add("Không đọc được file tracking: " + e.Message); }
            }
            _dirty = true;
            Version++;
        }

        static int _pendingFrame = -1;

        static void Queue(object item, int frame)
        {
            if (Current == null || Current.EndedAt != null) Start();
            if (frame != _pendingFrame) Flush();
            _pendingFrame = frame;
            Pending.Add(item);
        }

        /// <summary>Xử lý các mục của 1 frame: cú bấm đặt trước lời gọi đầu tiên sinh ra từ cú bấm đó.</summary>
        static void Flush()
        {
            if (Pending.Count == 0) return;
            var items = Pending.ToList();
            Pending.Clear();
            foreach (var click in items.OfType<RawStep>().Where(s => s.Kind == "click").ToList())
            {
                var first = items.FindIndex(o => o is RawTrackingCall c && c.FromClick);
                var at = items.IndexOf(click);
                if (first >= 0 && first < at)
                {
                    items.RemoveAt(at);
                    items.Insert(first, click);
                }
            }
            foreach (var o in items)
            {
                if (o is RawStep s) AddStep(s);
                else if (o is RawTrackingCall c) AddCall(c);
            }
        }

        static void AddStep(RawStep st)
        {
            var e = new RecordedEvent
            {
                Seq = -(++_steps), T = Math.Round(st.RealTime, 2), Time = st.Time, Frame = st.Frame, Kind = st.Kind,
                Name = st.Name, Context = st.Context, Path = st.Path,
            };
            _sequence?.Step(e);
            Current!.Events.Add(e);
            _dirty = true;
            Version++;
        }

        static void AddCall(RawTrackingCall c)
        {
            var s = Current!;
            // Ghi đúng như Firebase nhận: Titan gửi items của resource_update lên Firebase dưới tên view_item (FirebaseAnalytic.TrackItems)
            var name = c.Kind == "items" && c.Name == "resource_update" ? "view_item" : c.Name;
            var e = new RecordedEvent
            {
                Seq = s.Events.Count(x => !x.IsStep) + 1, T = Math.Round(c.RealTime, 2), Time = c.Time, Frame = c.Frame, Kind = c.Kind, Name = name,
                FromClick = c.FromClick,
                Params = c.Params.Select(Param).ToList(),
                Items = c.Items.Select(i => i.Select(Param).ToList()).ToList(),
                Stack = c.Stack.Select(Frame).Where(f => f != null).Select(f => f!).Take(10).ToList(),
            };
            if (_checker != null)
            {
                try
                {
                    using var _ = Knowledge.ForGame(_gameId);
                    _checker.Check(e, s.Events);
                    _sequence?.Check(e);
                    _checker.MarkKnown(e);
                }
                catch (Exception ex) { Debug.LogWarning("[Tracking QA] Không kiểm được " + e.Name + ": " + ex.Message); }
            }
            if (c.Kind == "items" && name != "view_item")
                e.Issues.Add(new LiveIssue { Level = "warn", Rule = "items_not_sent", Text = $"Firebase của Titan chỉ nhận items cho view_item — lời gọi items “{c.Name}” bị bỏ, không lên Firebase" });
            s.Events.Add(e);
            // Đối chiếu với Check all: xác nhận / bác bỏ / không tái hiện (property: sau khi kiểm thứ tự với event kế tiếp)
            if (_known != null && _feedback != null)
            {
                try
                {
                    if (_feedback.Observe(_known, e, s.Id)) _feedbackDirty = true;
                    // Lỗi thứ tự property được gắn vào dòng property phía trước → đối chiếu lại các property vừa có lỗi mới
                    foreach (var p in s.Events.Where(x => x.Kind == "property" && x.Issues.Any(i => i.Rule == "flow_property_before_event" && i.Text.Contains($"(#{e.Seq})"))))
                        if (_feedback.Observe(_known, new RecordedEvent { Seq = p.Seq, Kind = p.Kind, Name = p.Name, Time = p.Time, Issues = p.Issues.Where(i => i.Text.Contains($"(#{e.Seq})")).ToList() }, s.Id))
                            _feedbackDirty = true;
                }
                catch (Exception ex) { Debug.LogWarning("[Tracking QA] Không đối chiếu được với Check all: " + ex.Message); }
            }
            // Bảng Lỗi Record: cộng lỗi mới (Check all chưa báo), đếm lần đi qua đúng chỗ mà không lỗi
            try
            {
                var before = s.Events.Take(s.Events.Count - 1).ToList();
                if (Issues.Observe(e, s.Id, before, QaRunner.FileHash)) _issuesDirty = true;
                // Lỗi thứ tự property gắn vào dòng property phía trước
                foreach (var p in s.Events.Where(x => x.Kind == "property" && x.Issues.Any(i => i.KnownFindingId == null && i.Rule == "flow_property_before_event" && i.Text.Contains($"(#{e.Seq})"))).ToList())
                {
                    var sub = new RecordedEvent
                    {
                        Seq = p.Seq, T = p.T, Kind = p.Kind, Name = p.Name, Time = p.Time, Params = p.Params, Stack = p.Stack,
                        Issues = p.Issues.Where(i => i.Text.Contains($"(#{e.Seq})")).ToList(),
                    };
                    if (Issues.Observe(sub, s.Id, s.Events.TakeWhile(x => x != p).ToList(), QaRunner.FileHash)) _issuesDirty = true;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Tracking QA] Không ghi được bảng Lỗi Record: " + ex.Message); }
            _dirty = true;
            Version++;
        }

        static RecordedParam Param(KeyValuePair<string, object?> p) => new RecordedParam
        {
            Key = p.Key,
            Value = p.Value switch
            {
                null => null,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var v => v.ToString(),
            },
            Type = p.Value?.GetType().Name ?? "",
        };

        /// <summary>Khung gọi → vị trí trong project (Assets/… hoặc Library/PackageCache/…) dạng giống Check all; bỏ Titan.Tracking, uGUI / Unity và chính package QA.</summary>
        static CodeLocation? Frame((string File, int Line, string Type, string Method) f)
        {
            string rel;
            try { rel = Path.GetRelativePath(QaRunner.ProjectRoot, f.File).Replace('\\', '/'); }
            catch { return null; }
            if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;
            if (rel.Contains("/com.titan.tracking@") || rel.Contains("com.titan.tracking-qa") || rel.Contains("/com.unity.")) return null;
            // "Ns.PausePopup+<>c" + "<Awake>b__3_0" → "PausePopup.Awake" (giống cách Check all ghi tên hàm chứa lambda)
            var parts = f.Type.Split('+');
            var real = parts.LastOrDefault(p => !p.StartsWith("<")) ?? parts[0];
            var cls = real.Substring(real.LastIndexOf('.') + 1);
            var method = f.Method;
            var gen = parts.FirstOrDefault(p => p.StartsWith("<") && p.IndexOf('>') > 1);
            if (method.StartsWith("<") && method.IndexOf('>') > 1) method = method.Substring(1, method.IndexOf('>') - 1);
            else if (method == "MoveNext" && gen != null) method = gen.Substring(1, gen.IndexOf('>') - 1);
            return new CodeLocation(rel, f.Line, $"{cls}.{method}");
        }

        static void Tick()
        {
            if (Pending.Count > 0) Flush(); // frame xong → xử lý các mục của frame đó
            if (Current == null || EditorApplication.timeSinceStartup < _nextSave) return;
            if (!_dirty && !_feedbackDirty && !_issuesDirty) return;
            _nextSave = EditorApplication.timeSinceStartup + 2; // ghi dần mỗi 2 giây — Unity treo / tắt đột ngột vẫn còn phần đã chơi
            Save();
            SaveFeedback();
            FlushIssues();
        }

        static void End()
        {
            Flush();
            if (Current == null || Current.EndedAt != null) return;
            Current.EndedAt = DateTime.Now;
            Current.Notes.AddRange(Notices.Where(n => !Current.Notes.Contains(n)));
            if (!string.IsNullOrEmpty(RecordBus.Status) && RecordBus.Status != "Đang ghi") Current.Notes.Add(RecordBus.Status);
            Save();
            SaveFeedback();
            if (Issues.CommitPending(Current.Id)) _issuesDirty = true;
            FlushIssues();
            try { RecordStore.Prune(Dir, KeepSessions); } catch { }
            Version++;
        }

        static void Save()
        {
            if (Current == null || (Current.Events.Count == 0 && Current.EndedAt == null)) return;
            try { RecordStore.Save(Current, Dir); _dirty = false; }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không lưu được phiên Record: " + e.Message); }
        }

        /// <summary>Lưu kết quả đối chiếu và cập nhật cửa sổ CheckAll (Nghi ngờ được xác nhận → Lỗi, mục bị bác bỏ không tính).</summary>
        static void SaveFeedback()
        {
            if (!_feedbackDirty || _feedback == null) return;
            try
            {
                _feedback.Save(FeedbackPath);
                _feedbackDirty = false;
                QaRunner.ApplyRecordFeedback(_feedback);
            }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không lưu được kết quả đối chiếu Record: " + e.Message); }
        }

        /// <summary>Lưu bảng Lỗi Record và đưa lỗi còn mở vào cửa sổ CheckAll.</summary>
        static void FlushIssues()
        {
            if (!_issuesDirty) return;
            SaveIssues();
            try { QaRunner.MergeRecordIssues(); }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không cập nhật được cửa sổ CheckAll: " + e.Message); }
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static string PackageVersion() =>
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RecordController).Assembly)?.version ?? "";
    }
}
