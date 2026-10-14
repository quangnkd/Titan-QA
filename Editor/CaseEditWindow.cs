#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TrackingChecker.Core;
using TrackingChecker.Core.Rules;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Thêm / sửa 1 case: TC-xxx (chung mọi game) hoặc G-xxx (riêng game). Lưu trên máy, có hiệu lực ngay — kết quả đang xem được gắn lại mã case,
    /// không cần Check all lại. Case "tự động" dựng từ luật lõi có sẵn (không cần viết code); case "kiểm tay" là checklist khi chơi.
    /// Gửi cho cả team qua cửa sổ Kho chung (PR, người duyệt merge).
    /// </summary>
    public sealed class CaseEditWindow : EditorWindow
    {
        const string Manual = "Kiểm tay — checklist các bước khi chơi";
        const string Auto = "Tự động — theo luật có sẵn (Check all + Record tự gắn)";
        const string NoGroup = "(chưa xếp nhóm)";

        [SerializeField] string _id = ""; // "" = thêm mới

        public static void Add() => Open("");
        public static void Edit(string id) => Open(id);

        static void Open(string id)
        {
            var w = GetWindow<CaseEditWindow>("Tracking QA · Case");
            w.minSize = new Vector2(520, 560);
            w._id = id;
            w.Build();
        }

        void CreateGUI() => Build();

        void Build()
        {
            var root = rootVisualElement;
            root.Clear();
            var css = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.titan.tracking-qa/Editor/TrackingQAWindow.uss");
            if (css != null) root.styleSheets.Add(css);
            root.AddToClassList("tqa");
            var game = QaRunner.GameId;
            List<TrackingCase> all;
            using (Knowledge.ForGame(game)) all = CaseStore.Active(game);
            var old = _id.Length == 0 ? null : all.FirstOrDefault(c => c.Id.Equals(_id, StringComparison.OrdinalIgnoreCase));
            var scroll = new ScrollView();
            scroll.AddToClassList("detail-scroll");
            root.Add(scroll);
            var body = new VisualElement();
            body.AddToClassList("detail");
            scroll.Add(body);
            if (_id.Length > 0 && old == null)
            {
                body.Add(Muted($"Không tìm thấy case {_id} (đã bị bỏ?)."));
                return;
            }

            // --- Phạm vi + mã
            var head = new Label(old == null ? "Thêm case" : $"Sửa case {old.Id}");
            head.AddToClassList("section");
            body.Add(head);
            PopupField<string>? scope = null;
            var idLabel = Muted("");
            if (old == null)
            {
                var scopes = new List<string> { "Chung mọi game (TC-xxx)" };
                if (game != null) scopes.Add("Riêng game này (G-xxx)");
                scope = new PopupField<string>("Phạm vi", scopes, 0);
                body.Add(scope);
            }
            body.Add(idLabel);
            bool Common() => old != null ? CaseStore.IsCommon(old.Id) : scope!.index == 0;
            string NewId() { using (Knowledge.ForGame(game)) return CaseStore.NextId(Common() ? "TC" : "G", game); }
            void ShowId() => idLabel.text = old != null
                ? $"{old.Id} · {(CaseStore.IsCommon(old.Id) ? "chung mọi game" : "riêng game này")}{(old.Layer.StartsWith("máy") ? " · đã sửa trên máy, chưa gửi" : "")}"
                : $"Mã sẽ cấp: {NewId()}";
            ShowId();

            // --- Nội dung
            var title = Field("Điều phải đúng", old?.Title ?? "", true);
            body.Add(title);
            var why = Field("Vì sao quan trọng", old?.Why ?? "", true);
            body.Add(why);
            var category = new PopupField<string>("Loại khi sai", new List<string> { "Lỗi", "Nghi ngờ" }, old?.Category == "Nghi ngờ" ? 1 : 0)
            {
                tooltip = "Lỗi: bắn sai / đếm sai → số liệu sai. Nghi ngờ: cần xem hoặc chơi thật để xác nhận.",
            };
            body.Add(category);
            var groups = all.Select(c => c.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();
            groups.Insert(0, NoGroup);
            var group = new PopupField<string>("Nhóm", groups, old != null && groups.Contains(old.Group) ? groups.IndexOf(old.Group) : 0);
            body.Add(group);

            // --- Áp dụng khi (chỉ sửa được điều kiện "doc có …"; điều kiện khác giữ nguyên)
            var docOnly = old?.AppliesWhen == null || old.AppliesWhen.Keys.All(k => k == "doc");
            var applies = Field("Áp dụng khi doc có", docOnly ? string.Join(", ", DocNames(old?.AppliesWhen)) : "", false);
            applies.tooltip = "Tên event / user property / event.param, cách nhau dấu phẩy (có 1 cái là đủ). Để trống = áp dụng mọi game.";
            applies.SetEnabled(docOnly);
            body.Add(applies);
            if (!docOnly) body.Add(Muted("Điều kiện áp dụng của case này phức tạp hơn — giữ nguyên."));

            // --- Cách kiểm
            var keepLabel = old != null && !IsSimple(old) ? $"Giữ cách kiểm hiện tại ({old.Kind}{(old.FromRules.Count > 0 ? " · " + string.Join(", ", old.FromRules) : "")})" : null;
            var modes = new List<string>();
            if (keepLabel != null) modes.Add(keepLabel);
            modes.Add(Manual);
            modes.Add(Auto);
            var mode = new PopupField<string>("Cách kiểm", modes, keepLabel != null ? 0 : old != null && old.Kind == "rule" ? modes.IndexOf(Auto) : modes.IndexOf(Manual));
            body.Add(mode);

            var autoBox = new VisualElement();
            var rules = KnownRules(game);
            var oldRule = old != null && old.Kind == "rule" && old.FromRules.Count == 1 ? old.FromRules[0] : null;
            var oldMatch = old?.Match.FirstOrDefault();
            if (oldRule != null && !rules.Any(r => r.Rule == oldRule)) rules.Insert(0, (oldRule, ""));
            var rule = new PopupField<string>("Luật", rules.Select(r => r.Rule).ToList(), Math.Max(0, rules.FindIndex(r => r.Rule == oldRule)),
                r => r,
                r => { var ex = rules.FirstOrDefault(x => x.Rule == r).Example ?? ""; return ex.Length > 0 ? $"{r}   ({Short(ex, 60)})" : r; });
            autoBox.Add(rule);
            var ev = Field("Chỉ event / property", oldMatch?.Subject ?? "", false);
            var param = Field("Chỉ param", oldMatch?.Param ?? "", false);
            var value = Field("Chỉ giá trị", oldMatch?.Value ?? "", false);
            ev.tooltip = param.tooltip = value.tooltip = "Để trống = mọi mục của luật này.";
            autoBox.Add(ev);
            autoBox.Add(param);
            autoBox.Add(value);
            var hits = Muted("");
            autoBox.Add(hits);
            body.Add(autoBox);
            if (rules.Count == 0) autoBox.Add(Muted("Chưa có luật nào để chọn."));

            void Refresh()
            {
                autoBox.style.display = mode.value == Auto ? DisplayStyle.Flex : DisplayStyle.None;
                if (mode.value != Auto || rules.Count == 0) return;
                var r = QaRunner.Report;
                hits.text = r == null
                    ? "Chưa có kết quả Check all để thử."
                    : $"Khớp {r.Findings.Count(f => f.Rule == rule.value && Eq(ev.value, f.Subject) && Eq(param.value, f.Param) && Eq(value.value, f.Value))} mục trong kết quả đang xem — lưu là gắn ngay, không cần Check all lại.";
            }
            mode.RegisterValueChangedCallback(_ => Refresh());
            rule.RegisterValueChangedCallback(_ => Refresh());
            ev.RegisterValueChangedCallback(_ => Refresh());
            param.RegisterValueChangedCallback(_ => Refresh());
            value.RegisterValueChangedCallback(_ => Refresh());
            scope?.RegisterValueChangedCallback(_ => ShowId());
            Refresh();

            var repro = Field("Các bước kiểm khi chơi (mỗi dòng 1 bước)", string.Join("\n", old?.Repro ?? new List<string>()), true);
            body.Add(repro);
            var example = Field("Ví dụ", old?.Example ?? "", false);
            body.Add(example);

            // --- Lưu / Huỷ / Bỏ
            var error = Muted("");
            error.AddToClassList("error");
            var btns = new VisualElement();
            btns.AddToClassList("accept-btns");
            btns.style.flexDirection = FlexDirection.Row;
            var save = new Button(() =>
            {
                if (string.IsNullOrWhiteSpace(title.value)) { error.text = "Cần ghi điều phải đúng."; return; }
                if (mode.value == Auto && rules.Count == 0) { error.text = "Chưa có luật để chọn — dùng Kiểm tay."; return; }
                var c = old ?? new TrackingCase { Id = NewId(), Basis = "doc", Status = "active", Source = $"Thêm từ Unity · {Environment.UserName} · {DateTime.Now:dd/MM/yyyy}" };
                c.Title = title.value.Trim();
                c.Why = Blank(why.value);
                c.Category = category.value;
                c.Group = group.value == NoGroup ? "" : group.value;
                c.Example = Blank(example.value);
                c.Repro = repro.value.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (docOnly)
                {
                    var names = applies.value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    c.AppliesWhen = names.Count == 0 ? null : new Dictionary<string, JsonElement> { ["doc"] = Json(names.Count == 1 ? (object)names[0] : names) };
                }
                if (mode.value == Manual)
                {
                    c.Check = new Dictionary<string, JsonElement> { ["kind"] = Json("manual") };
                    c.FromRules = new List<string>();
                    c.Match = new List<CaseMatch>();
                    c.StaticCheck = "no";
                    c.RuntimeCheck = "manual";
                }
                else if (mode.value == Auto)
                {
                    c.Check = new Dictionary<string, JsonElement> { ["kind"] = Json("rule"), ["rule"] = Json(rule.value) };
                    c.FromRules = new List<string> { rule.value };
                    var m = new CaseMatch { Rule = rule.value, Subject = Blank(ev.value), Param = Blank(param.value), Value = Blank(value.value) };
                    c.Match = m.Subject == null && m.Param == null && m.Value == null ? new List<CaseMatch>() : new List<CaseMatch> { m };
                    c.StaticCheck = "yes";
                    c.RuntimeCheck = "yes";
                }
                try { using (Knowledge.ForGame(game)) CaseStore.Save(game, c); }
                catch (Exception e) { error.text = "Không lưu được: " + e.Message; return; }
                Debug.Log($"[Tracking QA] Đã lưu case {c.Id} trên máy — gửi cho cả team: cửa sổ Kho chung.");
                QaRunner.ReapplyKnowledge();
                Close();
            }) { text = "Lưu" };
            save.AddToClassList("primary");
            btns.Add(save);
            btns.Add(new Button(Close) { text = "Huỷ" });
            if (old != null)
                btns.Add(new Button(() => { if (QaRunner.RemoveCase(old.Id)) Close(); }) { text = "Bỏ case…", tooltip = "Chưa gửi → xoá hẳn; đã ở kho chung → chỉnh sửa chờ gửi (giữ mã)" });
            body.Add(btns);
            body.Add(error);
            body.Add(Muted("Lưu trên máy này, có hiệu lực ngay. " + (Common()
                ? "Case chung: gửi qua cửa sổ Kho chung → người duyệt merge thì áp cho mọi game."
                : "Case riêng game: gửi qua cửa sổ Kho chung để cả team có.")));
            body.Add(Muted("Muốn cách kiểm mới mà chưa có luật: lưu dạng Kiểm tay trước, rồi nhờ Claude viết luật tự động."));
            title.schedule.Execute(() => title.Focus());
        }

        static bool IsSimple(TrackingCase c) =>
            c.Kind == "manual" || (c.Kind == "rule" && c.FromRules.Count == 1 && c.Match.Count <= 1 &&
                                   c.Match.All(m => m.TitlePattern == null && m.TextPattern == null && (m.Rule == null || m.Rule == c.FromRules[0])));

        /// <summary>Luật chọn được: luật các case đang dùng + luật có trong kết quả Check all đang xem.</summary>
        static List<(string Rule, string Example)> KnownRules(string? game)
        {
            List<(string Rule, string Example)> res;
            using (Knowledge.ForGame(game)) res = CaseStore.KnownRules(game);
            foreach (var f in QaRunner.Report?.Findings ?? new List<Finding>())
                if (!res.Any(r => r.Rule == f.Rule)) res.Add((f.Rule, "vd " + f.Title));
            return res.OrderBy(r => r.Rule, StringComparer.Ordinal).ToList();
        }

        static IEnumerable<string> DocNames(Dictionary<string, JsonElement>? when)
        {
            if (when == null || !when.TryGetValue("doc", out var v)) return Array.Empty<string>();
            return v.ValueKind == JsonValueKind.String ? new[] { v.GetString()! }
                : v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!) : Array.Empty<string>();
        }

        static JsonElement Json(object v)
        {
            using var d = JsonDocument.Parse(JsonSerializer.Serialize(v));
            return d.RootElement.Clone();
        }

        static bool Eq(string filter, string? v) => filter.Trim().Length == 0 || string.Equals(filter.Trim(), v, StringComparison.Ordinal);
        static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        static TextField Field(string label, string value, bool multiline)
        {
            var f = new TextField(label) { value = value, multiline = multiline };
            if (multiline) f.AddToClassList("reason");
            return f;
        }

        static Label Muted(string text)
        {
            var l = new Label(text);
            l.AddToClassList("muted");
            return l;
        }
    }
}
