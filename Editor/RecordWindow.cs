#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrackingChecker.Core.Live;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Cửa sổ Record: dòng thời gian event / property game bắn khi chơi trong Editor (giống Firebase DebugView), kiểm ngay theo doc.
    /// Lỗi mới tô đỏ; lỗi Check all đã báo hiện xám "đã biết". Xem lại phiên cũ (UserSettings/TrackingQA/record).
    /// </summary>
    public sealed class RecordWindow : EditorWindow
    {
        RecordSession? _viewing;      // phiên cũ đang chọn xem; null = phiên đang ghi (hoặc phiên gần nhất đã lưu)
        RecordSession? _latest;       // phiên gần nhất đã lưu — chỉ dùng khi chưa có phiên nào trong lần mở Unity này
        bool _onlyIssues;
        string _search = "";
        int _rendered = -1;
        int? _selectedSeq;
        readonly List<RecordedEvent> _items = new List<RecordedEvent>();
        /// <summary>Bước bấm đại diện cho 1 chuỗi bấm liên tiếp cùng kiểu (vd Tile_38, Tile_40… → "×12") — Seq → các bước trong chuỗi.</summary>
        readonly Dictionary<int, List<RecordedEvent>> _runs = new Dictionary<int, List<RecordedEvent>>();

        Label _head = null!, _status = null!, _notice = null!, _counts = null!;
        Toggle _auto = null!;
        ListView _list = null!;
        VisualElement _detail = null!;
        ScrollView _detailScroll = null!;
        PopupField<string> _sessions = null!;
        List<RecordStore.Entry> _entries = new List<RecordStore.Entry>();

        [MenuItem("Titan/QAUTO/Tracking QA Record", false, 2)]
        public static void Open() => GetWindow<RecordWindow>("Tracking QA · Record").minSize = new Vector2(640, 360);

        void OnEnable() => EditorApplication.update += Poll;
        void OnDisable() => EditorApplication.update -= Poll;

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
            var only = new Toggle("Chỉ hiện event có lỗi / cần xem") { value = _onlyIssues };
            only.RegisterValueChangedCallback(e => { _onlyIssues = e.newValue; Rebuild(); });
            filter.Add(only);
            var search = new ToolbarSearchField();
            search.AddToClassList("search");
            search.RegisterValueChangedCallback(e => { _search = e.newValue ?? ""; Rebuild(); });
            filter.Add(search);
            _counts = new Label();
            _counts.AddToClassList("meta");
            filter.Add(_counts);
            root.Add(filter);
            var editorOnly = Muted("Trong Editor không có (chỉ thấy trên máy thật / Firebase DebugView): quảng cáo thật (ad_impression, ad_request, ad_impression_banner), screen_view tự động, param Firebase tự thêm (firebase_*, ga_session_*, fps), user property Group / first_open_time.");
            editorOnly.style.whiteSpace = WhiteSpace.Normal;
            root.Add(editorOnly);

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
                if (sel.FirstOrDefault() is RecordedEvent e) { _selectedSeq = e.Seq; ShowEvent(e); }
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
            Rebuild();
        }

        // ------------------------------------------------------------------ cập nhật

        double _nextPoll;
        void Poll()
        {
            if (_list == null || EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 0.25; // vẽ lại tối đa 4 lần / giây khi game bắn dồn dập
            if (_viewing == null && _rendered != RecordController.Version) { Rebuild(); }
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var s = Session;
            _head.text = $"Game: {QaRunner.GameId ?? "?"}" + (s?.DocFile != null ? $"   ·   doc: {s.DocFile}" : "");
            _status.text = _viewing != null ? $"Đang xem phiên {s?.StartedAt:dd/MM HH:mm:ss}"
                : RecordController.Live ? "● Đang ghi"
                : EditorApplication.isPlaying ? (RecordController.AutoRecord ? RecordBus.Status : "Không ghi (đã tắt Tự Record)")
                : s != null ? $"Phiên gần nhất {s.StartedAt:dd/MM HH:mm:ss} (đã dừng)" : "Bấm Play để bắt đầu ghi";
            _status.EnableInClassList("live", RecordController.Live && _viewing == null);
            var notes = _viewing != null ? _viewing.Notes : RecordController.Notices.Concat(RecordBus.Status.StartsWith("Đang ghi") || RecordBus.Status.Length == 0 ? Array.Empty<string>() : new[] { RecordBus.Status }).ToList();
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

        void Rebuild()
        {
            if (_list == null) return;
            _rendered = RecordController.Version;
            var s = Session;
            var follow = _viewing == null && RecordController.Live && (_selectedSeq == null || _items.Count == 0 || _selectedSeq == _items.Last().Seq);
            _items.Clear();
            if (s != null)
            {
                var q = _search.Trim();
                var shown = s.Events.Where(e => !_onlyIssues || (!e.IsStep && e.Issues.Any(i => i.KnownFindingId == null)))
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
                _counts.text = $"{s.Events.Count(e => !e.IsStep)} event · {s.Events.Count(e => e.Kind == "click")} lần bấm · {s.ErrorCount} lỗi mới · {s.WarnCount} cần xem" + (known > 0 ? $" · {known} đã biết (Check all)" : "");
            }
            else _counts.text = "";
            _list.RefreshItems();
            UpdateStatus();
            if (_items.Count == 0) { _detail.Clear(); _detail.Add(Muted(s == null ? "Chưa có phiên nào. Bấm Play để bắt đầu ghi." : "Không có event nào khớp bộ lọc.")); return; }
            var idx = follow ? _items.Count - 1 : _selectedSeq is { } seq ? Math.Max(0, _items.FindIndex(e => e.Seq == seq)) : _items.Count - 1;
            _list.SetSelectionWithoutNotify(new[] { idx });
            _list.ScrollToItem(idx);
            _selectedSeq = _items[idx].Seq;
            ShowEvent(_items[idx]);
            if (!RecordController.Live && _viewing == null) RefreshSessions();
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

        static readonly string[] Pills = { "vio", "warn", "na", "pass", "info" };

        void BindRow(VisualElement row, int i)
        {
            var e = _items[i];
            if (e.IsStep)
            {
                var sp = row.Q<Label>(className: "pill");
                foreach (var c in Pills) sp.RemoveFromClassList(c);
                sp.text = e.Kind == "click" ? "▶ Bấm" : "▶ Scene";
                sp.AddToClassList("info");
                row.Q<Label>(className: "fid").text = $"{e.Time:HH:mm:ss}";
                row.Q<Label>(className: "subj").text = _runs.TryGetValue(e.Seq, out var run) ? $"{e.Name} … ×{run.Count}" : e.Name;
                row.Q<Label>(className: "row-title").text = e.Context ?? e.Path ?? "";
                row.tooltip = e.Path ?? e.Name;
                row.AddToClassList("step-row");
                return;
            }
            row.RemoveFromClassList("step-row");
            var newErr = e.Issues.Any(x => x.Level == "error" && x.KnownFindingId == null);
            var newWarn = e.Issues.Any(x => x.Level == "warn" && x.KnownFindingId == null);
            var known = e.Issues.Count > 0 && e.Issues.All(x => x.KnownFindingId != null);
            var pill = row.Q<Label>(className: "pill");
            foreach (var c in Pills) pill.RemoveFromClassList(c);
            pill.text = newErr ? "Lỗi" : newWarn ? "Cần xem" : known ? "Đã biết" : e.Kind == "property" ? "property" : "OK";
            pill.AddToClassList(newErr ? "vio" : newWarn ? "warn" : known ? "na" : e.Kind == "property" ? "info" : "pass");
            row.Q<Label>(className: "fid").text = $"{e.Time:HH:mm:ss}  #{e.Seq}";
            row.Q<Label>(className: "subj").text = e.Name;
            row.Q<Label>(className: "row-title").text = string.Join(" · ", e.Params.Take(6).Select(p => e.Kind == "property" ? p.Value : $"{p.Key}={p.Value}"));
            row.tooltip = string.Join("\n", e.Issues.Select(x => x.Text));
        }

        // ------------------------------------------------------------------ chi tiết

        void ShowEvent(RecordedEvent e)
        {
            _detail.Clear();
            if (e.IsStep)
            {
                var t = new Label((e.Kind == "click" ? "Bấm " : "Vào scene ") + e.Name);
                t.AddToClassList("d-title");
                _detail.Add(t);
                _detail.Add(Para($"{e.Time:HH:mm:ss.fff} · {e.T:F2}s · frame {e.Frame}"));
                if (e.Context != null) _detail.Add(Para("<b>Trong:</b> " + e.Context));
                if (e.Path != null) _detail.Add(Para("<b>Đường dẫn:</b> " + e.Path));
                if (_runs.TryGetValue(e.Seq, out var steps))
                    _detail.Add(Para($"<b>Bấm liên tiếp {steps.Count} lần:</b> " + string.Join(", ", steps.Select(x => x.Name))));
                var last = _runs.TryGetValue(e.Seq, out var rs) ? rs.Last() : e;
                var after = Session?.Events.SkipWhile(x => x != last).Skip(1).TakeWhile(x => !x.IsStep).ToList() ?? new List<RecordedEvent>();
                _detail.Add(Section(after.Count > 0 ? $"Ngay sau đó ({after.Count})" : "Không có tracking nào ngay sau thao tác này"));
                foreach (var x in after) _detail.Add(Para($"#{x.Seq}  <b>{x.Name}</b>  " + string.Join(" · ", x.Params.Take(5).Select(p => $"{p.Key}={p.Value}"))));
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

            foreach (var i in e.Issues.OrderBy(i => i.KnownFindingId != null).ThenBy(i => i.Level == "error" ? 0 : 1))
            {
                var refs = string.Join(", ", new[] { i.CaseId, i.KnownFindingId != null ? "đã biết · " + i.KnownFindingId + " (Check all)" : null }.Where(x => x != null));
                var p = Para((i.KnownFindingId != null ? "• " : i.Level == "error" ? "✘ " : "⚠ ") + i.Text + (refs.Length > 0 ? $"  <i>({refs})</i>" : ""), "sc-check");
                p.AddToClassList(i.KnownFindingId != null ? "known" : i.Level);
                _detail.Add(p);
            }
            if (e.Issues.Count == 0) _detail.Add(Para("✔ Đúng doc", "sc-check"));

            _detail.Add(Section(e.Kind == "property" ? "Giá trị" : $"Param ({e.Params.Count})"));
            foreach (var p in e.Params)
                _detail.Add(Para($"<b>{p.Key}</b> = {p.Value ?? "null"}  <i>{p.Type}</i>"));
            for (int k = 0; k < e.Items.Count; k++)
            {
                _detail.Add(Section($"Item {k + 1}"));
                foreach (var p in e.Items[k]) _detail.Add(Para($"<b>{p.Key}</b> = {p.Value ?? "null"}  <i>{p.Type}</i>"));
            }

            if (e.Stack.Count > 0)
            {
                _detail.Add(Section("Đường gọi trong code (gần chỗ bắn nhất trước)"));
                foreach (var f in e.Stack)
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
            _detailScroll.scrollOffset = Vector2.zero;
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

        static Label Tag(string text)
        {
            var l = new Label(text);
            l.AddToClassList("tag");
            return l;
        }
    }
}
