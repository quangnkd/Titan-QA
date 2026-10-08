#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TrackingChecker.Core;
using TrackingChecker.Core.Analysis;
using TrackingChecker.Core.Live;
using TrackingChecker.Core.Spec;
using UnityEditor;
using UnityEngine;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Record: bấm Play là bắt đầu 1 phiên, nghe mọi event / property / items game bắn qua Titan (RecordBus), kiểm ngay theo doc
    /// (LiveChecker), bỏ qua lỗi Check all đã báo, tự lưu phiên vào UserSettings/TrackingQA/record (ghi dần trong lúc chơi).
    /// Thoát Play thì kết thúc phiên. Không dùng AI.
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

        public static bool AutoRecord
        {
            get => EditorPrefs.GetBool(PrefAuto, true);
            set { EditorPrefs.SetBool(PrefAuto, value); RecordBus.Enabled = value; }
        }

        static LiveChecker? _checker;
        static string? _gameId;
        static bool _dirty;
        static double _nextSave;

        static RecordController()
        {
            // Chạy sau mỗi lần nạp lại script (kể cả lúc vào Play) — trước khi scene đầu chạy, nên đặt cờ kịp cho TitanHook
            RecordBus.Enabled = AutoRecord;
            RecordBus.PlayStarted += Start;
            RecordBus.Received += OnCall;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode || s == PlayModeStateChange.EnteredEditMode) End(); };
            EditorApplication.update += Tick;
            // Nạp lại script giữa phiên (Enter Play Mode không reload) → các lời gọi đã nghe vẫn còn trong bus
            if (EditorApplication.isPlaying && RecordBus.Enabled && RecordBus.Calls.Count > 0 && Current == null)
            {
                Start();
                foreach (var c in RecordBus.Calls.ToList()) Add(c);
            }
        }

        static void Start()
        {
            QaRunner.EnsureLoaded();
            var now = DateTime.Now;
            _gameId = QaRunner.GameId;
            Notices.Clear();
            Current = new RecordSession
            {
                Id = RecordStore.NewId(now), StartedAt = now, GameId = _gameId,
                Game = Application.productName, PackageVersion = PackageVersion(),
            };
            _checker = null;
            var doc = QaRunner.DocPath;
            if (string.IsNullOrEmpty(doc) || !File.Exists(doc))
                Notices.Add("Chưa chọn file tracking (Tracking QA → Chọn file tracking…) — chỉ ghi event, chưa kiểm theo doc.");
            else
            {
                try
                {
                    using var _ = Knowledge.ForGame(_gameId);
                    var spec = ExcelSpecReader.Read(doc);
                    if (_gameId != null && GameData.LoadOverlay(_gameId, doc) is { IsEmpty: false } ov) ov.Apply(spec);
                    var known = QaRunner.Report is { } r && SamePath(r.SpecFile, doc) ? r : null;
                    if (known == null) Notices.Add("Chưa có Check all cho doc này — lỗi lúc chơi có thể trùng lỗi Check all sẽ báo.");
                    else Current.KnownReportAt = known.CreatedAt;
                    _checker = new LiveChecker(spec, known);
                    Current.DocFile = Path.GetFileName(doc);
                }
                catch (Exception e) { Notices.Add("Không đọc được file tracking: " + e.Message); }
            }
            _dirty = true;
            Version++;
        }

        static void OnCall(RawTrackingCall c)
        {
            if (Current == null || Current.EndedAt != null) Start();
            Add(c);
        }

        static void Add(RawTrackingCall c)
        {
            var s = Current!;
            // Ghi đúng như Firebase nhận: Titan gửi items của resource_update lên Firebase dưới tên view_item (FirebaseAnalytic.TrackItems)
            var name = c.Kind == "items" && c.Name == "resource_update" ? "view_item" : c.Name;
            var e = new RecordedEvent
            {
                Seq = s.Events.Count + 1, T = Math.Round(c.RealTime, 2), Time = c.Time, Frame = c.Frame, Kind = c.Kind, Name = name,
                Params = c.Params.Select(Param).ToList(),
                Items = c.Items.Select(i => i.Select(Param).ToList()).ToList(),
                Stack = c.Stack.Select(Frame).Where(f => f != null).Select(f => f!).Take(10).ToList(),
            };
            if (_checker != null)
            {
                try { using var _ = Knowledge.ForGame(_gameId); _checker.Check(e, s.Events); }
                catch (Exception ex) { Debug.LogWarning("[Tracking QA] Không kiểm được " + e.Name + ": " + ex.Message); }
            }
            if (c.Kind == "items" && name != "view_item")
                e.Issues.Add(new LiveIssue { Level = "warn", Rule = "items_not_sent", Text = $"Firebase của Titan chỉ nhận items cho view_item — lời gọi items “{c.Name}” bị bỏ, không lên Firebase" });
            s.Events.Add(e);
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

        /// <summary>Khung gọi → vị trí trong project (Assets/… hoặc Library/PackageCache/…) dạng giống Check all; bỏ Titan.Tracking và chính package QA.</summary>
        static CodeLocation? Frame((string File, int Line, string Type, string Method) f)
        {
            string rel;
            try { rel = Path.GetRelativePath(QaRunner.ProjectRoot, f.File).Replace('\\', '/'); }
            catch { return null; }
            if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;
            if (rel.Contains("/com.titan.tracking@") || rel.Contains("com.titan.tracking-qa") || rel.StartsWith("Packages/com.titan.tracking-qa")) return null;
            // "Ns.PausePopup+<>c" + "<Awake>b__3_0" → "PausePopup.Awake" (giống cách Check all ghi tên hàm chứa lambda)
            var parts = f.Type.Split('+');
            var real = parts.LastOrDefault(p => !p.StartsWith("<")) ?? parts[0];
            var cls = real.Substring(real.LastIndexOf('.') + 1);
            var method = f.Method;
            var gen = parts.Select(p => p).FirstOrDefault(p => p.StartsWith("<") && p.IndexOf('>') > 1);
            if (method.StartsWith("<") && method.IndexOf('>') > 1) method = method.Substring(1, method.IndexOf('>') - 1);
            else if (method == "MoveNext" && gen != null) method = gen.Substring(1, gen.IndexOf('>') - 1);
            return new CodeLocation(rel, f.Line, $"{cls}.{method}");
        }

        static void Tick()
        {
            if (!_dirty || Current == null || EditorApplication.timeSinceStartup < _nextSave) return;
            _nextSave = EditorApplication.timeSinceStartup + 2; // ghi dần mỗi 2 giây — Unity treo / tắt đột ngột vẫn còn phần đã chơi
            Save();
        }

        static void End()
        {
            if (Current == null || Current.EndedAt != null) return;
            Current.EndedAt = DateTime.Now;
            Current.Notes.AddRange(Notices.Where(n => !Current.Notes.Contains(n)));
            if (!string.IsNullOrEmpty(RecordBus.Status) && RecordBus.Status != "Đang ghi") Current.Notes.Add(RecordBus.Status);
            Save();
            try { RecordStore.Prune(Dir, KeepSessions); } catch { }
            Version++;
        }

        static void Save()
        {
            if (Current == null || (Current.Events.Count == 0 && Current.EndedAt == null)) return;
            try { RecordStore.Save(Current, Dir); _dirty = false; }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không lưu được phiên Record: " + e.Message); }
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
