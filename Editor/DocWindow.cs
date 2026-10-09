#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrackingChecker.Core;
using TrackingChecker.Core.Rules;
using TrackingChecker.Core.Spec;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Cửa sổ Doc: Check all hiểu file tracking (Excel) thế nào — từng event / param / user property, kiểu, giá trị hợp lệ đọc được,
    /// chỗ đọc chưa chắc. QA sửa ngay tại đây (bỏ event / param không dùng ở game này, sửa kiểu, viết lại giá trị) rồi xác nhận —
    /// lưu theo game + tên file doc (games/&lt;id&gt;/docs/, trên máy; gửi cho team qua Kho chung). Không sửa file Excel.
    /// </summary>
    public sealed class DocWindow : EditorWindow
    {
        const string UserProps = "user_properties";
        static readonly List<string> TypeChoices = new List<string> { "(theo doc)", "int", "double", "string" };

        TrackingSpec? _raw;           // doc đọc từ Excel (chưa áp chỉnh sửa)
        SpecOverlay _ov = new SpecOverlay();
        List<SpecReview.Flag> _flags = new List<SpecReview.Flag>();
        string? _doc, _game, _error;
        bool _dirty, _onlyFlagged;
        string _search = "";
        string? _selected;
        readonly List<string> _items = new List<string>();

        Label _head = null!, _info = null!;
        ListView _list = null!;
        VisualElement _detail = null!;
        ScrollView _detailScroll = null!;
        Button _save = null!;

        [MenuItem("Titan/QAUTO/Tracking QA Doc", false, 4)]
        public static void Open() => GetWindow<DocWindow>("Tracking QA · Doc").minSize = new Vector2(640, 360);

        void OnFocus()
        {
            if (_list != null && !_dirty && _doc != QaRunner.DocPath) Load();
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            var css = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.titan.tracking-qa/Editor/TrackingQAWindow.uss");
            if (css != null) root.styleSheets.Add(css);
            root.AddToClassList("tqa");
            _head = new Label();
            _head.AddToClassList("game");
            root.Add(_head);
            _info = new Label();
            _info.AddToClassList("status");
            root.Add(_info);

            var top = Row("filter");
            var only = new Toggle("Chỉ hiện chỗ cần xem") { value = _onlyFlagged };
            only.RegisterValueChangedCallback(e => { _onlyFlagged = e.newValue; Rebuild(); });
            top.Add(only);
            var search = new ToolbarSearchField();
            search.AddToClassList("search");
            search.RegisterValueChangedCallback(e => { _search = e.newValue ?? ""; Rebuild(); });
            top.Add(search);
            top.Add(new VisualElement { style = { flexGrow = 1 } });
            _save = new Button(Save) { text = "Lưu & xác nhận doc", tooltip = "Lưu chỉnh sửa cho game này (trên máy) — áp dụng ở lần Check all / Record sau" };
            _save.AddToClassList("primary");
            top.Add(_save);
            top.Add(new Button(() => { if (!_dirty || EditorUtility.DisplayDialog("Tracking QA", "Bỏ các chỉnh sửa chưa lưu?", "Bỏ", "Huỷ")) Load(); }) { text = "Đọc lại" });
            root.Add(top);

            var split = new TwoPaneSplitView(0, 260, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("split");
            _list = new ListView
            {
                itemsSource = _items, fixedItemHeight = 40, selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight, makeItem = MakeRow, bindItem = BindRow,
            };
            _list.AddToClassList("list");
            _list.selectionChanged += sel => { if (sel.FirstOrDefault() is string n) { _selected = n; ShowEntry(n); } };
            split.Add(_list);
            _detailScroll = new ScrollView();
            _detailScroll.AddToClassList("detail-scroll");
            _detail = new VisualElement();
            _detail.AddToClassList("detail");
            _detailScroll.Add(_detail);
            split.Add(_detailScroll);
            root.Add(split);
            Load();
        }

        void Load()
        {
            _doc = QaRunner.DocPath;
            _game = QaRunner.GameId;
            _raw = null;
            _error = null;
            _dirty = false;
            _ov = new SpecOverlay();
            try
            {
                if (string.IsNullOrEmpty(_doc) || !File.Exists(_doc)) _error = "Chưa chọn file tracking (cửa sổ CheckAll → Chọn file tracking…).";
                else
                {
                    using var _ = Knowledge.ForGame(_game);
                    _raw = ExcelSpecReader.Read(_doc);
                    if (_game != null && GameData.LoadOverlay(_game, _doc) is { } ov) _ov = ov;
                    _flags = SpecReview.Check(_raw);
                }
            }
            catch (Exception e) { _error = "Không đọc được file tracking: " + e.Message; }
            Rebuild();
        }

        // ------------------------------------------------------------------ danh sách

        int FlagCount(string name) => _flags.Count(f => f.Event == name && f.Level == "warn");

        int EditCount(string name) =>
            (_ov.IgnoredEvents.Contains(name) ? 1 : 0)
            + _ov.IgnoredParams.Count(k => k.StartsWith(name + ".", StringComparison.Ordinal))
            + _ov.Types.Keys.Count(k => k.StartsWith(name + ".", StringComparison.Ordinal))
            + _ov.Values.Keys.Count(k => k.StartsWith(name + ".", StringComparison.Ordinal));

        void Rebuild()
        {
            if (_list == null) return;
            _head.text = $"Game: {_game ?? "?"}   ·   doc: {(_doc != null ? Path.GetFileName(_doc) : "?")}";
            _items.Clear();
            if (_raw != null)
            {
                var q = _search.Trim();
                var names = _raw.Events.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (_raw.UserProperties.Count > 0) names.Add(UserProps);
                _items.AddRange(names
                    .Where(n => !_onlyFlagged || FlagCount(n) > 0 || EditCount(n) > 0)
                    .Where(n => q.Length == 0 || n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                || (n != UserProps && _raw.Events[n].Params.Keys.Any(p => p.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)))
                    .OrderByDescending(n => FlagCount(n) > 0).ThenBy(n => n == UserProps ? 1 : 0));
            }
            var warn = _flags.Count(f => f.Level == "warn");
            _info.text = _error ?? $"{_raw!.Events.Count} event · {_raw.UserProperties.Count} user property · {warn} chỗ đọc chưa chắc · {_ov.Count} chỉnh sửa"
                + (_ov.ConfirmedAt is { } at ? $" · đã xác nhận {at:dd/MM/yyyy HH:mm}" : " · chưa xác nhận")
                + (_dirty ? " · CHƯA LƯU" : "");
            _save.SetEnabled(_raw != null && _game != null);
            _list.RefreshItems();
            if (_items.Count == 0) { _detail.Clear(); _detail.Add(Muted(_error ?? "Không có mục nào khớp bộ lọc.")); return; }
            var idx = _selected == null ? 0 : Math.Max(0, _items.IndexOf(_selected));
            _list.SetSelectionWithoutNotify(new[] { idx });
            _list.ScrollToItem(idx);
            _selected = _items[idx];
            ShowEntry(_selected);
        }

        static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var l1 = Row("row-1");
            var pill = new Label();
            pill.AddToClassList("pill");
            l1.Add(pill);
            var subj = new Label();
            subj.AddToClassList("subj");
            l1.Add(subj);
            row.Add(l1);
            var t = new Label();
            t.AddToClassList("row-title");
            row.Add(t);
            return row;
        }

        void BindRow(VisualElement row, int i)
        {
            var n = _items[i];
            var pill = row.Q<Label>(className: "pill");
            foreach (var c in new[] { "warn", "pass", "na", "acc" }) pill.RemoveFromClassList(c);
            var flags = FlagCount(n);
            var ignored = _ov.IgnoredEvents.Contains(n);
            var edits = EditCount(n);
            pill.text = ignored ? "Bỏ" : flags > 0 ? $"{flags} cần xem" : edits > 0 ? "Đã sửa" : "OK";
            pill.AddToClassList(ignored ? "na" : flags > 0 ? "warn" : edits > 0 ? "acc" : "pass");
            row.Q<Label>(className: "subj").text = n == UserProps ? "User property" : n;
            row.Q<Label>(className: "row-title").text = n == UserProps
                ? string.Join(", ", _raw!.UserProperties.Keys)
                : string.Join(", ", _raw!.Events[n].Params.Keys);
        }

        // ------------------------------------------------------------------ chi tiết

        void ShowEntry(string name)
        {
            _detail.Clear();
            if (_raw == null) return;
            var title = new Label(name == UserProps ? "User property" : name);
            title.AddToClassList("d-title");
            _detail.Add(title);
            foreach (var f in _flags.Where(f => f.Event == name && f.Param == null)) _detail.Add(Flag(f));

            if (name == UserProps)
            {
                foreach (var (pn, up) in _raw.UserProperties) ParamBox(UserProps, pn, up.Type, up.Description + " " + up.Notes);
                _detailScroll.scrollOffset = Vector2.zero;
                return;
            }
            var es = _raw.Events[name];
            if (!string.IsNullOrWhiteSpace(es.Trigger)) _detail.Add(Para("<b>Bắn khi:</b> " + es.Trigger.Trim()));
            if (!string.IsNullOrWhiteSpace(es.Notes)) _detail.Add(Para("<b>Ghi chú:</b> " + es.Notes.Trim()));
            _detail.Add(Muted($"Sheet {es.SourceSheet}, dòng {es.SourceRow}"));
            var ignore = new Toggle("Bỏ event này — game này không dùng (không báo Thiếu)") { value = _ov.IgnoredEvents.Contains(name) };
            ignore.RegisterValueChangedCallback(e =>
            {
                _ov.IgnoredEvents.Remove(name);
                if (e.newValue) _ov.IgnoredEvents.Add(name);
                Changed();
            });
            _detail.Add(ignore);
            _detail.Add(Section($"Param ({es.Params.Count})"));
            foreach (var (pn, ps) in es.Params) ParamBox(name, pn, ps.Type, ps.Description + " " + ps.Notes);
            _detailScroll.scrollOffset = Vector2.zero;
        }

        void ParamBox(string ev, string pn, ParamType type, string text)
        {
            var key = $"{ev}.{pn}";
            var box = new VisualElement();
            box.AddToClassList("trace");
            var head = Row("d-head");
            var nm = new Label($"<b>{pn}</b>");
            head.Add(nm);
            head.Add(Tag(_ov.Types.TryGetValue(key, out var t) ? t + " (đã sửa)" : type == ParamType.Unknown ? "kiểu ?" : type.ToString().ToLowerInvariant()));
            if (_ov.IgnoredParams.Contains(key)) head.Add(Tag("bỏ", "ai"));
            box.Add(head);
            if (!string.IsNullOrWhiteSpace(text)) box.Add(Para("<b>Doc:</b> " + text.Trim()));
            var shown = _ov.Values.TryGetValue(key, out var v) ? v : text;
            var rule = ValueRule.FromDoc(shown);
            box.Add(Para(rule == null ? "<b>Check all hiểu:</b> không có danh sách giá trị cố định"
                : "<b>Check all hiểu:</b> " + (rule.Codes.Count > 0 ? string.Join(" · ", rule.Codes.Select(c => $"{c.Code} = {c.Meaning}"))
                    : rule.Min != null || rule.Max != null ? $"trong khoảng {rule.Min?.ToString() ?? "…"} – {rule.Max?.ToString() ?? "…"}"
                    : string.Join(", ", rule.Tokens.Select(x => x.Token + (x.Prefix ? "*" : ""))))));
            foreach (var f in _flags.Where(f => f.Event == ev && f.Param == pn)) box.Add(Flag(f));

            var edit = Row("ev");
            edit.style.flexWrap = Wrap.Wrap;
            var ign = new Toggle("Bỏ param") { value = _ov.IgnoredParams.Contains(key), tooltip = "Game này không gửi param này (không báo thiếu)" };
            ign.RegisterValueChangedCallback(e => { _ov.IgnoredParams.Remove(key); if (e.newValue) _ov.IgnoredParams.Add(key); Changed(); });
            edit.Add(ign);
            var tp = new PopupField<string>("Kiểu", TypeChoices, _ov.Types.TryGetValue(key, out var cur) && TypeChoices.Contains(cur) ? cur : TypeChoices[0]);
            tp.RegisterValueChangedCallback(e => { _ov.Types.Remove(key); if (e.newValue != TypeChoices[0]) _ov.Types[key] = e.newValue; Changed(); });
            edit.Add(tp);
            box.Add(edit);
            if (ev != UserProps)
            {
                box.Add(Muted("Viết lại giá trị hợp lệ (để trống = theo doc), dạng “0-thua; 1-thắng; 3-ấn replay”:"));
                var tf = new TextField { value = v ?? "" };
                tf.RegisterValueChangedCallback(e =>
                {
                    _ov.Values.Remove(key);
                    if (!string.IsNullOrWhiteSpace(e.newValue)) _ov.Values[key] = e.newValue.Trim();
                    _dirty = true;
                    _info.text = _info.text.Contains("CHƯA LƯU") ? _info.text : _info.text + " · CHƯA LƯU";
                });
                tf.RegisterCallback<FocusOutEvent>(_ => { if (_dirty) Rebuild(); });
                box.Add(tf);
            }
            _detail.Add(box);
        }

        void Changed()
        {
            _dirty = true;
            Rebuild();
        }

        void Save()
        {
            if (_raw == null || _game == null || _doc == null) return;
            _ov.ConfirmedAt = DateTime.Now;
            try
            {
                using (Knowledge.ForGame(_game)) GameData.SaveOverlay(_game, _doc, _ov);
                _dirty = false;
                Rebuild();
                if (EditorUtility.DisplayDialog("Tracking QA", $"Đã lưu {_ov.Count} chỉnh sửa doc cho game này (trên máy). Áp dụng ở lần Check all sau.\nGửi cho cả team: Kho chung.", "Check all ngay", "Để sau")
                    && !QaRunner.Running && File.Exists(_doc))
                {
                    QaRunner.CheckAll(_doc, QaRunner.Platform);
                    TrackingQAWindow.Open();
                }
            }
            catch (Exception e) { EditorUtility.DisplayDialog("Tracking QA", "Không lưu được: " + e.Message, "OK"); }
        }

        // ------------------------------------------------------------------ phần tử nhỏ

        static Label Flag(SpecReview.Flag f)
        {
            var p = Para((f.Level == "warn" ? "⚠ " : "· ") + f.Message, "sc-check");
            p.AddToClassList(f.Level == "warn" ? "warn" : "known");
            return p;
        }

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

        static Label Tag(string text, string cls = "")
        {
            var l = new Label(text);
            l.AddToClassList("tag");
            if (cls.Length > 0) l.AddToClassList(cls);
            return l;
        }
    }
}
