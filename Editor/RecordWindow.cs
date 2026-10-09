#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrackingChecker.Core;
using TrackingChecker.Core.Live;
using TrackingChecker.Core.Rules;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Cửa sổ Record: dòng thời gian event / property game bắn khi chơi trong Editor (giống Firebase DebugView), kiểm ngay theo doc.
    /// Lỗi mới tô đỏ; lỗi Check all đã báo hiện xám "đã biết". Xem lại phiên cũ (UserSettings/TrackingQA/record).
    /// Tab "Lỗi tổng hợp": bảng Lỗi Record cộng dồn qua mọi phiên (số lần, trạng thái Mới / Đã sửa? / Gặp lại / Đã sửa / Bỏ qua).
    /// </summary>
    public sealed class RecordWindow : EditorWindow
    {
        RecordSession? _viewing;      // phiên cũ đang chọn xem; null = phiên đang ghi (hoặc phiên gần nhất đã lưu)
        RecordSession? _latest;       // phiên gần nhất đã lưu — chỉ dùng khi chưa có phiên nào trong lần mở Unity này
        int _mode;                    // 0 = Dòng thời gian, 1 = Lỗi tổng hợp
        bool _onlyIssues, _showClosed;
        string _search = "";
        int _rendered = -1;
        int? _selectedSeq;
        string? _selectedIssue;
        string? _formIssue, _form;    // biểu mẫu đang mở trong chi tiết lỗi: "accept" / "case"
        readonly List<object> _items = new List<object>();
        /// <summary>Bước bấm đại diện cho 1 chuỗi bấm liên tiếp cùng kiểu (vd Tile_38, Tile_40… → "×12") — Seq → các bước trong chuỗi.</summary>
        readonly Dictionary<int, List<RecordedEvent>> _runs = new Dictionary<int, List<RecordedEvent>>();
        Dictionary<string, (GameData.AcceptedEntry E, bool Pending)> _accepted = new Dictionary<string, (GameData.AcceptedEntry, bool)>();
        /// <summary>R4: kết quả so cú bấm với log giả lập — Seq của bước bấm → kết quả.</summary>
        readonly Dictionary<int, ScenarioMatch> _sim = new Dictionary<int, ScenarioMatch>();

        Label _head = null!, _status = null!, _notice = null!, _counts = null!, _editorOnly = null!;
        Toggle _auto = null!, _onlyToggle = null!, _closedToggle = null!;
        Button _modeTimeline = null!, _modeIssues = null!;
        ListView _list = null!;
        VisualElement _detail = null!;
        ScrollView _detailScroll = null!;
        PopupField<string> _sessions = null!;
        List<RecordStore.Entry> _entries = new List<RecordStore.Entry>();

        [MenuItem("Titan/QAUTO/Tracking QA Record", false, 2)]
        public static void Open() => GetWindow<RecordWindow>("Tracking QA · Record").minSize = new Vector2(640, 360);

        /// <summary>Mở cửa sổ ở tab Lỗi tổng hợp, chọn sẵn 1 lỗi (từ cửa sổ CheckAll).</summary>
        public static void ShowIssue(string id)
        {
            var w = GetWindow<RecordWindow>("Tracking QA · Record");
            w._selectedIssue = id;
            w.SetMode(1);
        }

        void OnEnable() => EditorApplication.update += Poll;
        void OnDisable() => EditorApplication.update -= Poll;
        void OnFocus() => QaRunner.CheckCodeChanges();

        RecordSession? Session => _viewing ?? RecordController.Current ?? (_latest ??= LoadLatest());

        static RecordSession? LoadLatest()
        {
            var e = RecordStore.List(RecordController.Dir).FirstOrDefault();
            return e == null ? null : RecordStore.Load(e.Path);
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            var css = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.titan.tracking-qa/Editor/TrackingQAWindow.uss");
            if (css != null) root.styleSheets.Add(css);
            root.AddToClassList("tqa");
            _rendered = -1;

            _head = new Label();
            _head.AddToClassList("game");
            root.Add(_head);

            var top = Row("top");
            _auto = new Toggle("Tự Record khi Play") { value = RecordController.AutoRecord, tooltip = "Bấm Play là bắt đầu ghi (nghe event qua Titan). Tắt nếu chỉ muốn chơi thử." };
            _auto.RegisterValueChangedCallback(e => RecordController.AutoRecord = e.newValue);
            top.Add(_auto);
            _status = new Label();
            _status.AddToClassList("rec-status");
            top.Add(_status);
            top.Add(new VisualElement { style = { flexGrow = 1 } });
            _sessions = new PopupField<string>(new List<string> { "Phiên hiện tại" }, 0) { tooltip = "Xem lại phiên chơi cũ" };
            _sessions.AddToClassList("rec-sessions");
            _sessions.RegisterValueChangedCallback(_ => PickSession());
            top.Add(_sessions);
            top.Add(new Button(() => EditorUtility.RevealInFinder(Directory.Exists(RecordController.Dir) ? RecordController.Dir : QaRunner.ProjectRoot)) { text = "Mở thư mục", tooltip = RecordController.Dir });
            root.Add(top);

            _notice = new Label();
            _notice.AddToClassList("status");
            root.Add(_notice);

            var filter = Row("filter");
            var modeSeg = Row("seg");
            modeSeg.AddToClassList("mode");
            _modeTimeline = new Button(() => SetMode(0)) { text = "Dòng thời gian", tooltip = "Event / property / bước bấm của 1 phiên chơi" };
            _modeIssues = new Button(() => SetMode(1)) { text = "Lỗi tổng hợp", tooltip = "Bảng Lỗi Record: lỗi mới (Check all chưa báo) cộng dồn qua mọi phiên, có số lần và trạng thái" };
            modeSeg.Add(_modeTimeline);
            modeSeg.Add(_modeIssues);
            filter.Add(modeSeg);
            _onlyToggle = new Toggle("Chỉ hiện event có lỗi / cần xem") { value = _onlyIssues };
            _onlyToggle.RegisterValueChangedCallback(e => { _onlyIssues = e.newValue; Rebuild(); });
            filter.Add(_onlyToggle);
            _closedToggle = new Toggle("Hiện lỗi đã đóng (Đã sửa / Bỏ qua)") { value = _showClosed };
            _closedToggle.RegisterValueChangedCallback(e => { _showClosed = e.newValue; Rebuild(); });
            filter.Add(_closedToggle);
            var search = new ToolbarSearchField();
            search.AddToClassList("search");
            search.RegisterValueChangedCallback(e => { _search = e.newValue ?? ""; Rebuild(); });
            filter.Add(search);
            _counts = new Label();
            _counts.AddToClassList("meta");
            filter.Add(_counts);
            root.Add(filter);
            _editorOnly = Muted("Trong Editor không có (chỉ thấy trên máy thật / Firebase DebugView): quảng cáo thật (ad_impression, ad_request, ad_impression_banner), screen_view tự động, param Firebase tự thêm (firebase_*, ga_session_*, fps), user property Group / first_open_time.");
            _editorOnly.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_editorOnly);

            var split = new TwoPaneSplitView(0, 360, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("split");
            _list = new ListView
            {
                itemsSource = _items, fixedItemHeight = 40, selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight, makeItem = MakeRow, bindItem = BindRow,
            };
            _list.AddToClassList("list");
            _list.selectionChanged += sel =>
            {
                switch (sel.FirstOrDefault())
                {
                    case RecordedEvent e: _selectedSeq = e.Seq; ShowEvent(e); break;
                    case RecordIssue it: _selectedIssue = it.Id; ShowIssueDetail(it); break;
                }
            };
            split.Add(_list);
            _detailScroll = new ScrollView();
            _detailScroll.AddToClassList("detail-scroll");
            _detail = new VisualElement();
            _detail.AddToClassList("detail");
            _detailScroll.Add(_detail);
            split.Add(_detailScroll);
            root.Add(split);

            RefreshSessions();
            SetMode(_mode);
        }

        void SetMode(int mode)
        {
            _mode = mode;
            if (_list == null) return;
            _modeTimeline.EnableInClassList("on", mode == 0);
            _modeIssues.EnableInClassList("on", mode == 1);
            _onlyToggle.style.display = mode == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _closedToggle.style.display = mode == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            _editorOnly.style.display = mode == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _sessions.SetEnabled(mode == 0);
            Rebuild();
        }

        // ------------------------------------------------------------------ cập nhật

        double _nextPoll;
        void Poll()
        {
            if (_list == null || EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 0.25; // vẽ lại tối đa 4 lần / giây khi game bắn dồn dập
            if ((_viewing == null || _mode == 1) && _rendered != RecordController.Version) { Rebuild(); }
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var s = Session;
            _head.text = $"Game: {QaRunner.GameId ?? "?"}" + (s?.DocFile != null ? $"   ·   doc: {s.DocFile}" : "");
            _status.text = _viewing != null && _mode == 0 ? $"Đang xem phiên {s?.StartedAt:dd/MM HH:mm:ss}"
                : RecordController.Live ? "● Đang ghi"
                : EditorApplication.isPlaying ? (RecordController.AutoRecord ? RecordBus.Status : "Không ghi (đã tắt Tự Record)")
                : s != null ? $"Phiên gần nhất {s.StartedAt:dd/MM HH:mm:ss} (đã dừng)" : "Bấm Play để bắt đầu ghi";
            _status.EnableInClassList("live", RecordController.Live && (_viewing == null || _mode == 1));
            var notes = _viewing != null ? _viewing.Notes : RecordController.Notices.Concat(RecordBus.Status.StartsWith("Đang ghi") || RecordBus.Status.Length == 0 ? Array.Empty<string>() : new[] { RecordBus.Status }).ToList();
            if (_mode == 1) notes = new List<string>();
            _notice.text = string.Join("\n", notes);
            _notice.style.display = notes.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void RefreshSessions()
        {
            _entries = RecordStore.List(RecordController.Dir);
            var choices = new List<string> { "Phiên hiện tại" };
            choices.AddRange(_entries.Select(e => $"{e.StartedAt:dd/MM HH:mm:ss} · {e.Events} event · {e.Errors} lỗi"));
            _sessions.choices = choices;
            if (_viewing == null) _sessions.SetValueWithoutNotify(choices[0]);
        }

        void PickSession()
        {
            var i = _sessions.index;
            _viewing = i <= 0 ? null : RecordStore.Load(_entries[i - 1].Path);
            _latest = null;
            _selectedSeq = null;
            Rebuild();
        }

        /// <summary>Mở 1 phiên ở dòng thời gian và chọn sẵn 1 event (từ bảng Lỗi tổng hợp).</summary>
        void OpenSession(string id, int seq)
        {
            if (RecordController.Current?.Id == id) _viewing = null;
            else
            {
                RefreshSessions();
                var i = _entries.FindIndex(e => e.Id == id);
                if (i < 0)
                {
                    EditorUtility.DisplayDialog("Tracking QA", $"Phiên {id} không còn (chỉ giữ {50} phiên gần nhất).", "OK");
                    return;
                }
                _viewing = RecordStore.Load(_entries[i].Path);
                _sessions.SetValueWithoutNotify(_sessions.choices[i + 1]);
            }
            _latest = null;
            _selectedSeq = seq;
            _onlyIssues = false;
            _onlyToggle.SetValueWithoutNotify(false);
            SetMode(0);
        }

        void Rebuild()
        {
            if (_list == null) return;
            _rendered = RecordController.Version;
            if (_mode == 1) { RebuildIssues(); return; }
            var s = Session;
            var follow = _viewing == null && RecordController.Live && (_selectedSeq == null || _items.Count == 0 || _selectedSeq == (_items.LastOrDefault() as RecordedEvent)?.Seq);
            _items.Clear();
            if (s != null)
            {
                var q = _search.Trim();
                Simulate(s);
                var shown = s.Events.Where(e => !_onlyIssues || (!e.IsStep && e.Issues.Any(i => i.IsNew)) || (e.Kind == "click" && _sim.TryGetValue(e.Seq, out var sm) && sm.Differs))
                    .Where(e => q.Length == 0 || Has(e.Name, q) || Has(e.Context, q) || e.Params.Any(p => Has(p.Key, q) || Has(p.Value, q)) || e.Issues.Any(i => Has(i.Text, q)));
                // Gộp các lần bấm liên tiếp cùng kiểu, cùng màn (tên chỉ khác số) thành 1 dòng
                _runs.Clear();
                RecordedEvent? head = null;
                foreach (var e in shown)
                {
                    if (head != null && e.Kind == "click" && head.Kind == "click" && e.Context == head.Context && Pattern(e.Name) == Pattern(head.Name))
                    {
                        if (!_runs.TryGetValue(head.Seq, out var run)) _runs[head.Seq] = run = new List<RecordedEvent> { head };
                        run.Add(e);
                        continue;
                    }
                    _items.Add(e);
                    head = e.Kind == "click" ? e : null;
                }
                int known = s.Events.Sum(e => e.Issues.Count(i => i.KnownFindingId != null));
                int cheat = s.Events.Sum(e => e.Issues.Count(i => i.KnownFindingId == null && i.Cheat != null));
                int diff = _sim.Values.Count(m => m.Differs);
                _counts.text = $"{s.Events.Count(e => !e.IsStep)} event · {s.Events.Count(e => e.Kind == "click")} lần bấm · {s.ErrorCount} lỗi mới · {s.WarnCount} cần xem"
                    + (known > 0 ? $" · {known} đã biết (Check all)" : "") + (cheat > 0 ? $" · {cheat} do cheat (ẩn)" : "")
                    + (_sim.Count > 0 ? $" · {diff}/{_sim.Count} lần bấm lệch giả lập" : "");
            }
            else _counts.text = "";
            _list.RefreshItems();
            UpdateStatus();
            if (_items.Count == 0) { _detail.Clear(); _detail.Add(Muted(s == null ? "Chưa có phiên nào. Bấm Play để bắt đầu ghi." : "Không có event nào khớp bộ lọc.")); return; }
            var idx = follow ? _items.Count - 1 : _selectedSeq is { } seq ? Math.Max(0, _items.FindIndex(o => o is RecordedEvent e && e.Seq == seq)) : _items.Count - 1;
            _list.SetSelectionWithoutNotify(new[] { idx });
            _list.ScrollToItem(idx);
            var sel = (RecordedEvent)_items[idx];
            _selectedSeq = sel.Seq;
            ShowEvent(sel);
            if (!RecordController.Live && _viewing == null) RefreshSessions();
        }

        static int StatusRank(RecordIssueStatus s) => s switch
        {
            RecordIssueStatus.Reopened => 0, RecordIssueStatus.New => 1, RecordIssueStatus.MaybeFixed => 2, RecordIssueStatus.Fixed => 3, _ => 4,
        };

        bool IsAccepted(RecordIssue it) => _accepted.ContainsKey(RecordIssueLog.ToFinding(it).StableKey());

        void RebuildIssues()
        {
            var log = RecordController.Issues;
            try { _accepted = QaRunner.GameId is { } g ? GameData.Exceptions(g) : new Dictionary<string, (GameData.AcceptedEntry, bool)>(); }
            catch { _accepted = new Dictionary<string, (GameData.AcceptedEntry, bool)>(); }
            var q = _search.Trim();
            _items.Clear();
            _items.AddRange(log.Items
                .Where(x => _showClosed || (!x.IsClosed && !IsAccepted(x)))
                .Where(x => q.Length == 0 || Has(x.Id, q) || Has(x.Subject, q) || Has(x.Text, q) || Has(x.CaseId, q) || Has(x.Param, q))
                .OrderBy(x => IsAccepted(x) ? 5 : StatusRank(x.Status)).ThenByDescending(x => x.LastSeen));
            int open = log.Items.Count(x => !x.IsClosed && !IsAccepted(x));
            _counts.text = $"{open} đang mở · {log.Items.Count(x => x.Status == RecordIssueStatus.MaybeFixed && !IsAccepted(x))} Đã sửa? · {log.Items.Count(x => x.Status == RecordIssueStatus.Reopened && !IsAccepted(x))} Gặp lại · {log.Items.Count - open} đã đóng";
            _list.RefreshItems();
            UpdateStatus();
            if (_items.Count == 0)
            {
                _detail.Clear();
                _detail.Add(Muted(log.Items.Count == 0
                    ? "Chưa có lỗi nào. Lỗi mới (Check all chưa báo) gặp khi chơi sẽ được cộng dồn ở đây qua mọi phiên."
                    : "Không có lỗi nào khớp bộ lọc."));
                return;
            }
            var idx = _selectedIssue == null ? 0 : Math.Max(0, _items.FindIndex(o => o is RecordIssue x && x.Id == _selectedIssue));
            _list.SetSelectionWithoutNotify(new[] { idx });
            _list.ScrollToItem(idx);
            var it = (RecordIssue)_items[idx];
            _selectedIssue = it.Id;
            ShowIssueDetail(it);
        }

        static bool Has(string? s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        static string Pattern(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"\d+", "#");

        // ------------------------------------------------------------------ danh sách

        static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var l1 = Row("row-1");
            var time = new Label();
            time.AddToClassList("fid");
            l1.Add(time);
            var pill = new Label();
            pill.AddToClassList("pill");
            l1.Add(pill);
            var name = new Label();
            name.AddToClassList("subj");
            l1.Add(name);
            row.Add(l1);
            var sub = new Label();
            sub.AddToClassList("row-title");
            row.Add(sub);
            return row;
        }

        static readonly string[] Pills = { "vio", "warn", "na", "pass", "info", "acc" };

        static string IssuePill(RecordIssueStatus s) => s switch
        {
            RecordIssueStatus.New or RecordIssueStatus.Reopened => "vio",
            RecordIssueStatus.MaybeFixed => "warn",
            RecordIssueStatus.Fixed => "pass",
            _ => "na",
        };

        void BindRow(VisualElement row, int i)
        {
            var pl = row.Q<Label>(className: "pill");
            foreach (var c in Pills) pl.RemoveFromClassList(c);
            row.RemoveFromClassList("step-row");
            if (_items[i] is RecordIssue it)
            {
                var acc = IsAccepted(it);
                pl.text = acc ? "Đúng thiết kế" : RecordIssueLog.StatusLabel(it.Status);
                pl.AddToClassList(acc ? "acc" : IssuePill(it.Status));
                row.Q<Label>(className: "fid").text = $"{it.Id} · ×{it.Count} · {it.SessionCount} phiên" + (it.Stack.Count > 0 ? " · " + it.Stack[0].Member : "") + (it.CodeChanged ? " · code đã đổi" : "");
                row.Q<Label>(className: "subj").text = it.Subject;
                row.Q<Label>(className: "row-title").text = it.Text;
                row.tooltip = it.Text;
                return;
            }
            var e = (RecordedEvent)_items[i];
            if (e.IsStep)
            {
                var sim = _sim.TryGetValue(e.Seq, out var sm) ? sm : null;
                pl.text = e.Kind == "click" ? (sim?.Differs == true ? "▶ Bấm ≠" : "▶ Bấm") : e.Kind == "focus" ? (e.Name.StartsWith("Rời") ? "⏸ Rời game" : "▶ Quay lại") : "▶ Scene";
                pl.AddToClassList(sim?.Differs == true ? "warn" : "info");
                row.Q<Label>(className: "fid").text = $"{e.Time:HH:mm:ss}";
                row.Q<Label>(className: "subj").text = _runs.TryGetValue(e.Seq, out var run) ? $"{e.Name} … ×{run.Count}" : e.Name;
                row.Q<Label>(className: "row-title").text = (sim != null ? sim.Verdict + " · " : "") + (e.Context ?? e.Path ?? "");
                row.tooltip = e.Path ?? e.Name;
                row.AddToClassList("step-row");
                return;
            }
            var newErr = e.Issues.Any(x => x.Level == "error" && x.IsNew);
            var newWarn = e.Issues.Any(x => x.Level == "warn" && x.IsNew);
            var known = e.Issues.Count > 0 && e.Issues.All(x => !x.IsNew);
            pl.text = newErr ? "Lỗi" : newWarn ? "Cần xem" : known ? (e.Issues.All(x => x.KnownFindingId == null) ? "Cheat" : "Đã biết") : e.Kind == "property" ? "property" : "OK";
            pl.AddToClassList(newErr ? "vio" : newWarn ? "warn" : known ? "na" : e.Kind == "property" ? "info" : "pass");
            row.Q<Label>(className: "fid").text = $"{e.Time:HH:mm:ss}  #{e.Seq}";
            row.Q<Label>(className: "subj").text = e.Name;
            row.Q<Label>(className: "row-title").text = (e.Note != null ? "(rời focus) " : "") + string.Join(" · ", e.Params.Take(6).Select(p => e.Kind == "property" ? p.Value : $"{p.Key}={p.Value}"));
            row.tooltip = string.Join("\n", e.Issues.Select(x => x.Text));
        }

        // ------------------------------------------------------------------ chi tiết event

        void ShowEvent(RecordedEvent e)
        {
            _detail.Clear();
            if (e.IsStep)
            {
                var t = new Label(e.Kind == "click" ? "Bấm " + e.Name : e.Kind == "focus" ? e.Name : "Vào scene " + e.Name);
                t.AddToClassList("d-title");
                _detail.Add(t);
                _detail.Add(Para($"{e.Time:HH:mm:ss.fff} · {e.T:F2}s · frame {e.Frame}"));
                if (e.Kind == "focus") _detail.Add(Para("Trong Editor: bấm ra ngoài cửa sổ Game (sang cửa sổ khác, cửa sổ Record…) hoặc bấm Pause. Trên máy thật tương ứng người chơi bấm Home / chuyển app. Game thường bắn level_exit… ở đây — đúng, không phải lỗi.", "detail-text"));
                else if (e.Context != null) _detail.Add(Para("<b>Trong:</b> " + e.Context));
                if (e.Path != null) _detail.Add(Para("<b>Đường dẫn:</b> " + e.Path));
                if (_runs.TryGetValue(e.Seq, out var steps))
                    _detail.Add(Para($"<b>Bấm liên tiếp {steps.Count} lần:</b> " + string.Join(", ", steps.Select(x => x.Name))));
                var last = _runs.TryGetValue(e.Seq, out var rs) ? rs.Last() : e;
                var after = Session?.Events.SkipWhile(x => x != last).Skip(1).TakeWhile(x => !x.IsStep).ToList() ?? new List<RecordedEvent>();
                _detail.Add(Section(after.Count > 0 ? $"Ngay sau đó ({after.Count})" : "Không có tracking nào ngay sau thao tác này"));
                foreach (var x in after) _detail.Add(Para($"#{x.Seq}  <b>{x.Name}</b>  " + string.Join(" · ", x.Params.Take(5).Select(p => $"{p.Key}={p.Value}"))));
                SimulationSection(e);
                _detailScroll.scrollOffset = Vector2.zero;
                return;
            }
            var head = Row("d-head");
            head.Add(Tag(e.Kind));
            head.Add(Tag($"#{e.Seq}"));
            head.Add(Tag($"{e.Time:HH:mm:ss.fff} · {e.T:F2}s · frame {e.Frame}"));
            _detail.Add(head);
            var title = new Label(e.Name);
            title.AddToClassList("d-title");
            title.selection.isSelectable = true;
            _detail.Add(title);

            var log = RecordController.Issues;
            foreach (var i in e.Issues.OrderBy(i => i.KnownFindingId != null).ThenBy(i => i.Level == "error" ? 0 : 1))
            {
                var rec = i.IsNew ? log.Items.FirstOrDefault(x => x.Key == RecordIssueLog.KeyOf(e, i)) : null;
                var refs = string.Join(", ", new[] { i.CaseId, i.KnownFindingId != null ? "đã biết · " + i.KnownFindingId + " (Check all)" : null,
                    i.KnownFindingId == null && i.Cheat != null ? "ẩn — do cheat: " + i.Cheat : null, rec?.Id }.Where(x => x != null));
                var p = Para((!i.IsNew ? "• " : i.Level == "error" ? "✘ " : "⚠ ") + i.Text + (refs.Length > 0 ? $"  <i>({refs})</i>" : ""), "sc-check");
                p.AddToClassList(!i.IsNew ? "known" : i.Level);
                if (rec != null)
                {
                    p.tooltip = "Bấm để xem trong bảng Lỗi tổng hợp";
                    var id = rec.Id;
                    p.RegisterCallback<ClickEvent>(_ => { _selectedIssue = id; SetMode(1); });
                }
                _detail.Add(p);
            }
            if (e.Note != null) _detail.Add(Para("ⓘ " + e.Note, "detail-text"));
            if (e.Issues.Count == 0) _detail.Add(Para("✔ Đúng doc", "sc-check"));

            _detail.Add(Section(e.Kind == "property" ? "Giá trị" : $"Param ({e.Params.Count})"));
            foreach (var p in e.Params)
                _detail.Add(Para($"<b>{p.Key}</b> = {p.Value ?? "null"}  <i>{p.Type}</i>"));
            for (int k = 0; k < e.Items.Count; k++)
            {
                _detail.Add(Section($"Item {k + 1}"));
                foreach (var p in e.Items[k]) _detail.Add(Para($"<b>{p.Key}</b> = {p.Value ?? "null"}  <i>{p.Type}</i>"));
            }
            StackSection(e.Stack, "Đường gọi trong code (gần chỗ bắn nhất trước)");
            _detailScroll.scrollOffset = Vector2.zero;
        }

        /// <summary>R4: so cú bấm với log giả lập của Check all (chỉ tính khi đang xem — log giả lập lấy từ báo cáo Check all đang mở).</summary>
        void Simulate(RecordSession s)
        {
            _sim.Clear();
            var logs = QaRunner.Report?.Scenarios;
            if (logs == null || logs.Count == 0) return;
            try
            {
                using var _ = Knowledge.ForGame(QaRunner.GameId);
                foreach (var m in ScenarioMatcher.Compare(s, logs)) _sim[m.StepSeq] = m;
            }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không so được với log giả lập: " + e.Message); }
        }

        void SimulationSection(RecordedEvent click)
        {
            if (click.Kind != "click") return;
            if (_runs.TryGetValue(click.Seq, out var rs)) click = rs.Last();
            if (!_sim.TryGetValue(click.Seq, out var m))
            {
                if (QaRunner.Report?.Scenarios.Count > 0) return;
                _detail.Add(Muted("Chưa so được với log giả lập: báo cáo Check all chưa có (hoặc bản cũ) — bấm Check all."));
                return;
            }
            _detail.Add(Section($"So với log giả lập: {m.Verdict}"));
            _detail.Add(Muted($"Giả lập [{string.Join(", ", m.Scenarios)}]: {string.Join(" · ", m.Triggers)}"));
            foreach (var l in m.Lines)
            {
                var p = Para((l.Level == "ok" ? "✔ " : l.Level == "info" ? "· " : "⚠ ") + l.Text, "sc-check");
                p.AddToClassList(l.Level == "info" ? "known" : l.Level);
                _detail.Add(p);
            }
            if (m.Differs)
                _detail.Add(Muted("Lệch không phải lỗi tracking: là chỗ Check all đoán khác thực tế (code rác, giá trị Remote Config, nhánh khác). Xem tab Kịch bản trong cửa sổ CheckAll."));
        }

        void StackSection(List<TrackingChecker.Core.Analysis.CodeLocation> stack, string title)
        {
            if (stack.Count == 0) return;
            _detail.Add(Section(title));
            foreach (var f in stack)
            {
                var r = Row("ev");
                var m = new Label(f.Member);
                m.AddToClassList("member");
                r.Add(m);
                var link = new Label(f.File + ":" + f.Line) { tooltip = "Mở trong IDE", enableRichText = false };
                link.AddToClassList("link");
                var loc = f;
                link.RegisterCallback<ClickEvent>(_ => CodeNav.Open(loc.File, loc.Line));
                r.Add(link);
                _detail.Add(r);
            }
        }

        // ------------------------------------------------------------------ chi tiết lỗi tổng hợp

        void ShowIssueDetail(RecordIssue it)
        {
            _detail.Clear();
            var accepted = _accepted.TryGetValue(RecordIssueLog.ToFinding(it).StableKey(), out var acc) ? acc : ((GameData.AcceptedEntry E, bool Pending)?)null;

            var head = Row("d-head");
            var pill = new Label(accepted != null ? "Đúng thiết kế" : RecordIssueLog.StatusLabel(it.Status));
            pill.AddToClassList("pill");
            pill.AddToClassList(accepted != null ? "acc" : IssuePill(it.Status));
            head.Add(pill);
            head.Add(Tag(it.Id, "subj-tag"));
            if (it.CaseId != null) head.Add(Tag(it.CaseId, "case"));
            if (it.SavedCaseId != null) head.Add(Tag("đã lưu case " + it.SavedCaseId, "pkg", "Case riêng game này (trên máy, chưa gửi lên kho chung)"));
            head.Add(Tag((it.Kind == "property" ? "property " : "") + it.Subject));
            head.Add(Tag(it.Level == "error" ? "lỗi" : "cần xem"));
            head.Add(Tag($"gặp {it.Count} lần · {it.SessionCount} phiên"));
            if (it.CodeChanged) head.Add(Tag("code đã đổi — có thể đã sửa", "rec-ok", $"File {it.CodeFile} đã đổi từ lần gặp gần nhất"));
            _detail.Add(head);

            var title = new Label(it.Text);
            title.AddToClassList("d-title");
            title.selection.isSelectable = true;
            _detail.Add(title);

            if (it.StatusNote != null || it.StatusBy != null)
                _detail.Add(Para($"<b>{RecordIssueLog.StatusLabel(it.Status)}</b>" + (it.StatusNote != null ? ": " + it.StatusNote : "")
                                 + (it.StatusBy != null ? $" · {it.StatusBy}" : "") + (it.StatusAt is { } at ? $" · {at:dd/MM HH:mm}" : ""), "detail-text"));
            if (it.Status is RecordIssueStatus.New or RecordIssueStatus.Reopened && it.CleanPasses > 0)
                _detail.Add(Muted($"Đã đi lại đúng chỗ đó {it.CleanPasses} lần không lỗi (gần nhất {it.LastCleanPass}) — {RecordIssueLog.CleanPassesToMaybeFixed} lần thì chuyển Đã sửa?"));
            if (it.CodeChanged)
                _detail.Add(Para($"File code chỗ bắn ({it.CodeFile}) đã đổi từ lần gặp gần nhất — có thể đã sửa. Chơi lại các bước bên dưới để kiểm.", "detail-text"));

            // Thao tác của QA
            if (accepted is { } a)
            {
                var box = new VisualElement();
                box.AddToClassList("accepted-box");
                box.Add(new Label($"<b>Đúng thiết kế của game này</b> — lý do: {a.E.Reason}" + (a.E.By != null ? $" · {a.E.By}" : "") + (a.Pending ? " · <i>mới lưu trên máy</i>" : "")));
                box.Add(new Button(() => QaRunner.Unaccept(RecordIssueLog.ToFinding(it))) { text = "Bỏ chấp nhận" });
                _detail.Add(box);
            }
            else if (it.IsClosed)
            {
                var btns = Row("accept-btns");
                btns.Add(new Button(() => QaRunner.SetRecordStatus(it.Id, RecordIssueStatus.Reopened, "QA mở lại")) { text = "Mở lại" });
                _detail.Add(btns);
            }
            else if (_formIssue == it.Id && _form != null) IssueForm(it);
            else
            {
                var btns = Row("accept-btns");
                btns.style.flexWrap = Wrap.Wrap;
                var fixedBtn = new Button(() => QaRunner.SetRecordStatus(it.Id, RecordIssueStatus.Fixed))
                {
                    text = it.Status == RecordIssueStatus.MaybeFixed ? "Xác nhận đã sửa" : "Đã sửa",
                    tooltip = "Đóng lỗi. Gặp lại sau này → tự mở lại (Gặp lại).",
                };
                if (it.Status == RecordIssueStatus.MaybeFixed) fixedBtn.AddToClassList("primary");
                btns.Add(fixedBtn);
                btns.Add(new Button(() => QaRunner.SetRecordStatus(it.Id, RecordIssueStatus.Ignored)) { text = "Bỏ qua", tooltip = "Đóng lỗi, gặp lại không mở lại (chỉ đếm số lần)." });
                btns.Add(new Button(() => { _formIssue = it.Id; _form = "accept"; ShowIssueDetail(it); }) { text = "Đúng thiết kế…", tooltip = "Lưu làm ngoại lệ của game này (kèm lý do)" });
                if (it.SavedCaseId == null)
                    btns.Add(new Button(() => { _formIssue = it.Id; _form = "case"; ShowIssueDetail(it); }) { text = "Lưu thành case G-xxx…", tooltip = "Lưu thành case riêng của game — các lần Check all / Record sau gắn mã case này; sau có thể nâng thành case chung TC" });
                if (QaRunner.Report?.Findings.Any(f => f.RecordIssueId == it.Id) == true)
                    btns.Add(new Button(() => TrackingQAWindow.RevealRecord(it.Id)) { text = "Xem trong CheckAll" });
                _detail.Add(btns);
            }

            _detail.Add(Section("Lần gặp"));
            var seen = Row("ev");
            seen.style.flexWrap = Wrap.Wrap;
            seen.Add(new Label($"Lần đầu {it.FirstSeen:dd/MM HH:mm:ss} · "));
            seen.Add(SessionLink($"phiên {it.FirstSession} #{it.FirstSeq}", it.FirstSession, it.FirstSeq));
            seen.Add(new Label($"   ·   Gần nhất {it.LastSeen:dd/MM HH:mm:ss} · "));
            seen.Add(SessionLink($"phiên {it.LastSession} #{it.LastSeq}", it.LastSession, it.LastSeq));
            _detail.Add(seen);
            if (it.LastText != it.Text && it.LastText.Length > 0) _detail.Add(Para("<b>Gần nhất:</b> " + it.LastText));

            _detail.Add(Section("Các bước trước khi gặp lỗi (lần đầu)"));
            if (it.Steps.Count == 0) _detail.Add(Muted("Không ghi được thao tác trước đó (lỗi xảy ra khi chưa bấm nút nào)."));
            for (int k = 0; k < it.Steps.Count; k++) _detail.Add(Para($"{k + 1}. {it.Steps[k]}"));
            _detail.Add(Para($"{it.Steps.Count + 1}. Xem dòng <b>{it.Subject}</b>"));

            StackSection(it.Stack, "Đường gọi trong code (lần gần nhất)");
            _detailScroll.scrollOffset = Vector2.zero;
        }

        Label SessionLink(string text, string session, int seq)
        {
            var l = new Label(text) { tooltip = "Mở phiên này ở dòng thời gian", enableRichText = false };
            l.AddToClassList("link");
            l.RegisterCallback<ClickEvent>(_ => OpenSession(session, seq));
            return l;
        }

        /// <summary>Biểu mẫu "Đúng thiết kế" / "Lưu thành case G-xxx" của 1 lỗi.</summary>
        void IssueForm(RecordIssue it)
        {
            var f = RecordIssueLog.ToFinding(it);
            var form = new VisualElement();
            form.AddToClassList("accept-form");
            TextField title = null!;
            if (_form == "case")
            {
                form.Add(new Label($"Lưu thành case riêng của game ({GameData.NextCaseId(QaRunner.GameId ?? "")}) — điều phải đúng:"));
                title = new TextField { multiline = true, value = it.Text };
                title.AddToClassList("reason");
                form.Add(title);
                form.Add(new Label("Vì sao quan trọng (không bắt buộc):"));
            }
            else form.Add(new Label("Vì sao đây là đúng thiết kế của game? (vd: game cố ý bắn level_start khi về Home để …)"));
            var reason = new TextField { multiline = true };
            reason.AddToClassList("reason");
            form.Add(reason);
            var btns = Row("accept-btns");
            var save = new Button(() =>
            {
                if (_form == "case")
                {
                    if (string.IsNullOrWhiteSpace(title.value)) return;
                    _form = null;
                    var id = QaRunner.SaveCase(f, title.value, reason.value);
                    if (id != null) Debug.Log($"[Tracking QA] Đã lưu case {id} cho game {QaRunner.GameId}");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(reason.value)) return;
                    _form = null;
                    QaRunner.Accept(f, reason.value);
                }
                Rebuild();
            }) { text = "Lưu" };
            save.AddToClassList("primary");
            btns.Add(save);
            btns.Add(new Button(() => { _form = null; ShowIssueDetail(it); }) { text = "Huỷ" });
            form.Add(btns);
            form.Add(Muted("Lưu trên máy này, chỉ cho game này (UserSettings/TrackingQA/knowledge-local). Gửi cho cả team: nút “Kho chung” trong cửa sổ CheckAll."));
            _detail.Add(form);
            (_form == "case" ? title : reason).schedule.Execute(() => (_form == "case" ? title : reason).Focus());
        }

        // ------------------------------------------------------------------ phần tử nhỏ

        static VisualElement Row(string cls)
        {
            var v = new VisualElement();
            v.AddToClassList("hrow");
            v.AddToClassList(cls);
            return v;
        }

        static Label Para(string text, string? cls = null)
        {
            var l = new Label(text);
            l.selection.isSelectable = true;
            l.AddToClassList("para");
            if (cls != null) l.AddToClassList(cls);
            return l;
        }

        static Label Muted(string text)
        {
            var l = new Label(text);
            l.AddToClassList("muted");
            return l;
        }

        static Label Section(string text)
        {
            var l = new Label(text);
            l.AddToClassList("section");
            return l;
        }

        static Label Tag(string text, string cls = "", string? tip = null)
        {
            var l = new Label(text) { tooltip = tip };
            l.AddToClassList("tag");
            if (cls.Length > 0) l.AddToClassList(cls);
            return l;
        }
    }
}
