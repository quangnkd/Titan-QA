#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrackingChecker.Core;
using TrackingChecker.Core.Analysis;
using TrackingChecker.Core.Report;
using TrackingChecker.Core.Rules;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Cửa sổ Tracking QA: Check all tracking của project đang mở so với file tracking plan (Excel).
    /// Danh sách mục bên trái; bên phải là chi tiết với 2 tab "Lý do trong code" / "Tái hiện", bấm vị trí để mở code.
    /// </summary>
    public sealed class TrackingQAWindow : EditorWindow
    {
        static readonly FindingCategory[] Cats =
            { FindingCategory.CodeError, FindingCategory.Suspect, FindingCategory.Missing, FindingCategory.DocIssue, FindingCategory.OutOfPlan };

        // Bộ lọc (giữ khi vẽ lại)
        readonly HashSet<FindingCategory> _cats = new HashSet<FindingCategory>(Cats.Where(c => c != FindingCategory.OutOfPlan));
        bool _showAccepted, _hideRejected = true;
        bool _showNotOnDevice; // mục người chơi thật không gặp (chỉ Editor / bản debug / nút cheat) — ẩn mặc định
        string _search = "";
        int _tab; // 0 = Lý do trong code, 1 = Tái hiện
        string? _selectedKey;
        string? _acceptingKey;
        int _rendered = -1;

        // Chế độ xem: 0 = Theo lỗi, 1 = Theo case (kho case TC-xxx), 2 = Kịch bản (log giả lập)
        int _mode;
        string? _selectedCase, _selectedScenario;
        readonly HashSet<string> _caseFilter = new HashSet<string> { "vio", "warn", "miss", "doc", "acc", "pass", "todo" };

        /// <summary>Mục đang hiện trong danh sách: <see cref="Finding"/> (Theo lỗi) hoặc <see cref="CaseResult"/> (Theo case).</summary>
        readonly List<object> _items = new List<object>();

        // Phần tử giao diện
        Label _game = null!, _doc = null!, _status = null!, _meta = null!, _coverage = null!, _log = null!;
        Button _checkBtn = null!, _cancelBtn = null!, _androidBtn = null!, _iosBtn = null!, _continueBtn = null!;
        Button _modeFindings = null!, _modeCases = null!, _modeScenarios = null!;
        ToolbarSearchField _searchField = null!;
        VisualElement _summary = null!, _chips = null!, _detail = null!, _split = null!;
        Foldout _blind = null!;
        Label _empty = null!;
        Toggle _rejectedToggle = null!, _deviceToggle = null!;
        ListView _list = null!;
        ScrollView _detailScroll = null!;
        static Font? _mono;

        [MenuItem("Titan/Tracking QA")]
        public static void Open() => GetWindow<TrackingQAWindow>("Tracking QA").minSize = new Vector2(720, 420);

        void OnEnable() => QaRunner.Changed += Refresh;
        void OnDisable() => QaRunner.Changed -= Refresh;

        void CreateGUI()
        {
            QaRunner.EnsureLoaded();
            _rendered = -1;
            var root = rootVisualElement;
            var css = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.titan.tracking-qa/Editor/TrackingQAWindow.uss");
            if (css != null) root.styleSheets.Add(css);
            root.AddToClassList("tqa");
            _mono ??= EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font;

            // --- Hàng trên: game; file tracking, nền tảng, Check all
            _game = new Label { tooltip = "Bundle id của project — dùng để chọn kiến thức + ngoại lệ riêng của game" };
            _game.AddToClassList("game");
            root.Add(_game);
            var top = Row("top");
            _doc = new Label();
            _doc.AddToClassList("doc");
            top.Add(_doc);
            top.Add(new Button(PickDoc) { text = "Chọn file tracking…", tooltip = "File Excel tracking plan của game này (mỗi project nhớ file riêng)" });
            _androidBtn = new Button(() => { QaRunner.Platform = BuildPlatform.Android; Refresh(); }) { text = "Android" };
            _iosBtn = new Button(() => { QaRunner.Platform = BuildPlatform.iOS; Refresh(); }) { text = "iOS" };
            var seg = Row("seg");
            seg.Add(_androidBtn);
            seg.Add(_iosBtn);
            top.Add(seg);
            _checkBtn = new Button(() => QaRunner.CheckAll(QaRunner.DocPath, QaRunner.Platform)) { text = "Check all" };
            _checkBtn.AddToClassList("primary");
            top.Add(_checkBtn);
            _cancelBtn = new Button(QaRunner.Cancel) { text = "Huỷ" };
            top.Add(_cancelBtn);
            root.Add(top);

            _status = new Label();
            _status.AddToClassList("status");
            root.Add(_status);

            // --- Tóm tắt báo cáo
            _summary = new VisualElement();
            _summary.AddToClassList("summary");
            _meta = Selectable(new Label());
            _meta.AddToClassList("meta");
            _summary.Add(_meta);
            var covRow = Row("cov-row");
            _coverage = new Label();
            _coverage.AddToClassList("coverage");
            covRow.Add(_coverage);
            _continueBtn = new Button(QaRunner.ContinueBlindSpots)
            {
                tooltip = "Phân tích tiếp từ đúng các chỗ bị dừng do giới hạn, với giới hạn rộng hơn — không chạy lại từ đầu.",
            };
            covRow.Add(_continueBtn);
            covRow.Add(new VisualElement { style = { flexGrow = 1 } });
            covRow.Add(new Button(OpenHtml) { text = "Mở báo cáo HTML", tooltip = "Báo cáo đầy đủ (kèm bảng từng event) để gửi người khác" });
            _summary.Add(covRow);
            _blind = new Foldout { value = false };
            _blind.AddToClassList("blind");
            _summary.Add(_blind);

            // --- Bộ lọc
            var filter = Row("filter");
            var modeSeg = Row("seg");
            modeSeg.AddToClassList("mode");
            _modeFindings = new Button(() => SetMode(0)) { text = "Theo lỗi", tooltip = "Danh sách lỗi tìm được" };
            _modeCases = new Button(() => SetMode(1)) { text = "Theo case", tooltip = "Kết quả từng case của kho case (TC-xxx) trên game này" };
            _modeScenarios = new Button(() => SetMode(2)) { text = "Kịch bản", tooltip = "Log giả lập theo kịch bản (thắng / thua / chơi lại…): lần lượt bắn gì, biến đếm nào đổi — suy từ code" };
            modeSeg.Add(_modeFindings);
            modeSeg.Add(_modeCases);
            modeSeg.Add(_modeScenarios);
            filter.Add(modeSeg);
            _chips = Row("chips");
            filter.Add(_chips);
            var search = new ToolbarSearchField();
            search.AddToClassList("search");
            search.value = _search;
            search.RegisterValueChangedCallback(e => { _search = e.newValue ?? ""; RebuildList(); });
            _searchField = search;
            filter.Add(search);
            _rejectedToggle = new Toggle("Ẩn mục AI cho là báo nhầm") { value = _hideRejected };
            _rejectedToggle.RegisterValueChangedCallback(e => { _hideRejected = e.newValue; RebuildList(); });
            filter.Add(_rejectedToggle);
            _deviceToggle = new Toggle { value = _showNotOnDevice, tooltip = "Mục chỉ gặp khi chạy trong Unity Editor, ở bản debug hoặc bấm nút cheat — người chơi thật (bản release) không gặp nên mặc định ẩn" };
            _deviceToggle.RegisterValueChangedCallback(e => { _showNotOnDevice = e.newValue; RebuildList(); });
            filter.Add(_deviceToggle);
            _summary.Add(filter);
            root.Add(_summary);

            // --- Danh sách | chi tiết
            var split = new TwoPaneSplitView(0, 340, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("split");
            _split = split;
            _list = new ListView
            {
                itemsSource = _items, fixedItemHeight = 44, selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                makeItem = MakeRow, bindItem = BindRow,
            };
            _list.AddToClassList("list");
            _list.selectionChanged += sel =>
            {
                switch (sel.FirstOrDefault())
                {
                    case Finding f:
                        _selectedKey = f.StableKey();
                        _acceptingKey = null;
                        ShowDetail(f);
                        break;
                    case CaseResult c:
                        _selectedCase = c.Id;
                        ShowCase(c);
                        break;
                    case ScenarioLog s:
                        _selectedScenario = s.Id;
                        ShowScenario(s);
                        break;
                }
            };
            split.Add(_list);
            _detailScroll = new ScrollView(ScrollViewMode.Vertical);
            _detailScroll.AddToClassList("detail-scroll");
            _detail = new VisualElement();
            _detail.AddToClassList("detail");
            _detailScroll.Add(_detail);
            split.Add(_detailScroll);
            root.Add(split);

            _empty = new Label("Chọn file tracking (Excel) của game rồi bấm Check all.\n" +
                               "Tracking QA đọc code của project (cả package trong Library) và so với doc — không cần chạy game, không sửa gì trong project.");
            _empty.AddToClassList("empty");
            root.Add(_empty);

            // --- Nhật ký
            var logFold = new Foldout { text = "Nhật ký", value = false };
            logFold.AddToClassList("log");
            var logScroll = new ScrollView();
            _log = Selectable(new Label());
            logScroll.Add(_log);
            logFold.Add(logScroll);
            root.Add(logFold);

            Refresh();
        }

        // ------------------------------------------------------------------ cập nhật

        void Refresh()
        {
            if (_checkBtn == null) return;
            var r = QaRunner.Report;
            var running = QaRunner.Running;
            var doc = QaRunner.DocPath;

            _game.text = "Game: " + (QaRunner.GameId ?? "?");
            _doc.text = string.IsNullOrEmpty(doc) ? "Chưa chọn file tracking" : Path.GetFileName(doc);
            _doc.tooltip = doc;
            _doc.EnableInClassList("missing", string.IsNullOrEmpty(doc) || !File.Exists(doc));
            _androidBtn.EnableInClassList("on", QaRunner.Platform == BuildPlatform.Android);
            _iosBtn.EnableInClassList("on", QaRunner.Platform == BuildPlatform.iOS);
            _checkBtn.SetEnabled(!running && File.Exists(doc));
            _checkBtn.text = running ? "Đang chạy…" : "Check all";
            _cancelBtn.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;

            var status = QaRunner.Status;
            if (!running && r != null && !string.IsNullOrEmpty(doc) && !SamePath(r.SpecFile, doc))
                status = $"Báo cáo đang xem check với doc khác ({Path.GetFileName(r.SpecFile)}) — bấm Check all để check với file đang chọn.";
            _status.text = status;
            _status.style.display = string.IsNullOrEmpty(status) ? DisplayStyle.None : DisplayStyle.Flex;
            _status.EnableInClassList("running", running);
            _log.text = string.Join("\n", QaRunner.LogLines);

            _summary.style.display = r != null ? DisplayStyle.Flex : DisplayStyle.None;
            _split.style.display = r != null ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.style.display = r == null ? DisplayStyle.Flex : DisplayStyle.None;
            if (r == null) return;

            _continueBtn.style.display = QaRunner.CanContinue ? DisplayStyle.Flex : DisplayStyle.None;
            _continueBtn.text = $"Chạy tiếp điểm mù ({r.Coverage.Truncated})";
            _continueBtn.SetEnabled(!running);

            // Nhật ký cập nhật liên tục khi đang chạy → chỉ vẽ lại danh sách / chi tiết khi báo cáo thật sự đổi
            if (_rendered == QaRunner.Version) return;
            _rendered = QaRunner.Version;
            RenderSummary(r);
            RebuildList();
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        void RenderSummary(CheckReport r)
        {
            var docEvents = r.Subjects.Where(s => s.InDoc && !s.IsUserProperty).ToList();
            _meta.text = $"So với <b>{r.SpecName}</b> ({Path.GetFileName(r.SpecFile)}) · {r.Platform} · Unity {r.UnityVersion} · {r.FilesAnalyzed} file code · " +
                         $"{r.EmissionCount} luồng bắn · {r.AnalysisSeconds:F0}s · {r.CreatedAt:dd/MM HH:mm}" +
                         $"\nEvent trong doc không lỗi: <b>{docEvents.Count(s => s.Worst == FindingCategory.Ok)}/{docEvents.Count}</b>" +
                         (r.KnowledgeData != null ? $" · Kiến thức: {r.KnowledgeData}" : "") +
                         (r.AiStatus != null ? "\n" + r.AiStatus : "");

            var good = r.Coverage.Verdict == "Tốt";
            _coverage.text = good ? "Độ phủ phân tích: Tốt" : $"Độ phủ phân tích: Có điểm mù ({r.Coverage.BlindSpots.Count})";
            _coverage.EnableInClassList("good", good);
            _coverage.EnableInClassList("bad", !good);
            _blind.Clear();
            _blind.text = $"Điểm mù / ghi chú độ phủ ({r.Coverage.BlindSpots.Count})";
            _blind.style.display = r.Coverage.BlindSpots.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var b in r.Coverage.BlindSpots) _blind.Add(Selectable(new Label("• " + b)));
            RenderChips(r);
        }

        void RenderChips(CheckReport r)
        {
            _modeFindings.EnableInClassList("on", _mode == 0);
            _modeCases.EnableInClassList("on", _mode == 1);
            _modeScenarios.EnableInClassList("on", _mode == 2);
            _modeScenarios.SetEnabled(r.Scenarios.Count > 0);
            _modeCases.SetEnabled(r.Cases.Count > 0);
            _modeCases.tooltip = r.Cases.Count > 0 ? "Kết quả từng case của kho case (TC-xxx) trên game này" : "Báo cáo cũ chưa có kết quả theo case — bấm Check all lại";
            _chips.Clear();
            _rejectedToggle.style.display = _mode == 0 && r.Findings.Any(f => f.AiVerdict == "rejected") ? DisplayStyle.Flex : DisplayStyle.None;
            var hidden = r.Findings.Count(f => f.Accepted == null && f.NotOnDevice != null);
            _deviceToggle.label = $"Hiện mục chỉ gặp trong Editor / cheat ({hidden})";
            _deviceToggle.style.display = _mode == 0 && hidden > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            if (_mode == 2) return;
            if (_mode == 1)
            {
                foreach (var g in CaseGroups)
                {
                    var n = r.Cases.Count(c => CaseGroup(c.Status) == g.Key);
                    if (n == 0) continue;
                    var key = g.Key;
                    _chips.Add(Chip($"{g.Label} · {n}", key, _caseFilter.Contains(key), on =>
                    {
                        if (on) _caseFilter.Add(key); else _caseFilter.Remove(key);
                        RebuildList();
                    }));
                }
                return;
            }

            int N(FindingCategory c) => r.Findings.Count(f => f.Category == c && f.IsOpen);
            foreach (var c in Cats)
                _chips.Add(Chip($"{HtmlReport.CatLabel(c)} · {N(c)}", Cls(c), _cats.Contains(c), on =>
                {
                    if (on) _cats.Add(c); else _cats.Remove(c);
                    RebuildList();
                }));
            var acc = r.Findings.Count(f => f.Accepted != null);
            if (acc > 0) _chips.Add(Chip($"Đã chấp nhận · {acc}", "acc", _showAccepted, on => { _showAccepted = on; RebuildList(); }));
        }

        // Nhóm kết quả case để lọc / tô màu: chỉ mục "Lỗi" (bắn sai) là Vi phạm; Nghi ngờ / Thiếu / Lỗi doc / Ngoài plan xếp dưới
        static readonly (string Key, string Label)[] CaseGroups =
        {
            ("vio", "Vi phạm"), ("warn", "Nghi ngờ"), ("miss", "Thiếu"), ("doc", "Lỗi doc"), ("info", "Ngoài plan"),
            ("acc", "Đúng thiết kế"), ("pass", "Đạt"), ("todo", "Chưa kiểm được"), ("na", "Không áp dụng"), ("draft", "Nháp"),
        };

        static string CaseGroup(CaseStatus s) => s switch
        {
            CaseStatus.Violated => "vio",
            CaseStatus.Suspect => "warn",
            CaseStatus.Missing => "miss",
            CaseStatus.DocIssue => "doc",
            CaseStatus.OutOfPlan => "info",
            CaseStatus.Accepted => "acc",
            CaseStatus.Passed or CaseStatus.PassedPartial => "pass",
            CaseStatus.NotApplicable => "na",
            CaseStatus.Disabled => "draft",
            _ => "todo",
        };

        /// <summary>Đổi chế độ xem; có thể chọn sẵn 1 case / 1 lỗi (bấm mã case ở lỗi, hoặc bấm lỗi trong case).</summary>
        void SetMode(int mode, string? caseId = null, Finding? finding = null)
        {
            var r = QaRunner.Report;
            if (r == null) return;
            _mode = mode;
            if (caseId != null)
            {
                _selectedCase = caseId;
                var c = r.Cases.FirstOrDefault(x => x.Id == caseId);
                if (c != null) _caseFilter.Add(CaseGroup(c.Status));
            }
            if (finding != null)
            {
                _selectedKey = finding.StableKey();
                if (finding.Accepted != null) _showAccepted = true; else _cats.Add(finding.Category);
                if (finding.Accepted == null && finding.NotOnDevice != null) { _showNotOnDevice = true; _deviceToggle.SetValueWithoutNotify(true); }
            }
            if (caseId != null || finding != null)
            {
                _search = "";
                _searchField.SetValueWithoutNotify("");
            }
            RenderChips(r);
            RebuildList();
        }

        static Button Chip(string text, string cls, bool on, Action<bool> toggled)
        {
            var b = new Button { text = text };
            b.AddToClassList("chip");
            b.AddToClassList(cls);
            b.EnableInClassList("on", on);
            b.clicked += () =>
            {
                var now = !b.ClassListContains("on");
                b.EnableInClassList("on", now);
                toggled(now);
            };
            return b;
        }

        void RebuildList()
        {
            var r = QaRunner.Report;
            _items.Clear();
            if (r != null)
            {
                var q = _search.Trim();
                if (_mode == 2)
                    _items.AddRange(r.Scenarios.Where(s => q.Length == 0 || Has(s.Label, q)
                        || s.Runs.Any(run => Has(run.Trigger, q) || run.Items.Any(i => Has(i.Name, q)))));
                else if (_mode == 1)
                    _items.AddRange(r.Cases
                        .Where(c => _caseFilter.Contains(CaseGroup(c.Status)))
                        .Where(c => q.Length == 0 || Has(c.Id, q) || Has(c.Title, q) || Has(c.Group, q))
                        .OrderBy(c => CaseEngine.StatusOrder(c.Status)).ThenBy(c => c.Id, StringComparer.Ordinal));
                else
                    _items.AddRange(r.Findings
                        .Where(f => f.Accepted != null ? _showAccepted : _cats.Contains(f.Category) && (f.NotOnDevice == null || _showNotOnDevice))
                        .Where(f => !_hideRejected || f.AiVerdict != "rejected" || f.Accepted != null)
                        .Where(f => q.Length == 0 || Matches(f, q))
                        .OrderBy(f => f.Accepted != null ? 2 : f.NotOnDevice != null ? 1 : 0).ThenBy(f => f.Category.Rank()));
            }
            _list.RefreshItems();

            var idx = _mode switch
            {
                2 => _selectedScenario == null ? -1 : _items.FindIndex(o => o is ScenarioLog s && s.Id == _selectedScenario),
                1 => _selectedCase == null ? -1 : _items.FindIndex(o => o is CaseResult c && c.Id == _selectedCase),
                _ => _selectedKey == null ? -1 : _items.FindIndex(o => o is Finding f && f.StableKey() == _selectedKey),
            };
            if (idx < 0 && _items.Count > 0) idx = 0;
            if (idx < 0)
            {
                _list.ClearSelection();
                ShowDetail(null);
                return;
            }
            _list.SetSelectionWithoutNotify(new[] { idx });
            _list.ScrollToItem(idx);
            if (_items[idx] is CaseResult cr) ShowCase(cr);
            else if (_items[idx] is ScenarioLog sl) ShowScenario(sl);
            else ShowDetail((Finding)_items[idx]);
        }

        static bool Has(string? s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        static bool Matches(Finding f, string q) =>
            Has(f.Subject, q) || Has(f.Title, q) || Has(f.Detail, q) || Has(f.Id, q) || f.CaseIds.Any(c => Has(c, q))
            || f.Evidence.Any(e => Has(e.File, q) || Has(e.Member, q));

        // ------------------------------------------------------------------ danh sách

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
            var id = new Label();
            id.AddToClassList("fid");
            l1.Add(id);
            row.Add(l1);
            var title = new Label();
            title.AddToClassList("row-title");
            row.Add(title);
            return row;
        }

        static readonly string[] PillClasses = { "err", "warn", "miss", "doc", "info", "ok", "acc", "vio", "pass", "todo", "na", "draft" };

        void BindRow(VisualElement row, int i)
        {
            var pill = row.Q<Label>(className: "pill");
            foreach (var c in PillClasses) pill.RemoveFromClassList(c);
            if (_items[i] is ScenarioLog sl)
            {
                int err = sl.Runs.Sum(x => x.Checks.Count(c => c.Level == "error")), warn = sl.Runs.Sum(x => x.Checks.Count(c => c.Level == "warn"));
                pill.text = err > 0 ? $"{err} lỗi" : warn > 0 ? $"{warn} cần xem" : "Đúng doc";
                pill.AddToClassList(err > 0 ? "vio" : warn > 0 ? "warn" : "pass");
                row.Q<Label>(className: "subj").text = sl.Label;
                row.Q<Label>(className: "fid").text = sl.Steps.Count > 1 ? "nhiều bước" : $"{sl.Runs.Count} cách làm";
                row.Q<Label>(className: "row-title").text = sl.Runs.FirstOrDefault()?.Trigger ?? "";
                row.tooltip = sl.Label;
                return;
            }
            if (_items[i] is CaseResult cr)
            {
                pill.text = CaseEngine.StatusLabel(cr.Status);
                pill.AddToClassList(CaseGroup(cr.Status));
                row.Q<Label>(className: "subj").text = cr.Id;
                row.Q<Label>(className: "fid").text = (cr.Layer.Contains("game") ? "riêng game · " : "") + cr.Group;
                row.Q<Label>(className: "row-title").text = cr.Title;
                row.tooltip = cr.Title;
                return;
            }
            var f = (Finding)_items[i];
            pill.text = f.Accepted != null ? "Đã chấp nhận" : f.NotOnDevice != null ? "Chỉ Editor/cheat" : HtmlReport.CatLabel(f.Category);
            pill.AddToClassList(f.Accepted != null ? "acc" : f.NotOnDevice != null ? "na" : Cls(f.Category));
            row.Q<Label>(className: "subj").text = f.Subject;
            row.Q<Label>(className: "fid").text = f.CaseIds.Count > 0 ? $"{f.Id} · {string.Join(", ", f.CaseIds)}" : f.Id;
            row.Q<Label>(className: "row-title").text = f.Title;
            row.tooltip = f.Title;
        }

        static string Cls(FindingCategory c) => c switch
        {
            FindingCategory.CodeError => "err", FindingCategory.Suspect => "warn", FindingCategory.Missing => "miss",
            FindingCategory.DocIssue => "doc", FindingCategory.OutOfPlan => "info", _ => "ok",
        };

        // ------------------------------------------------------------------ chi tiết

        void ShowDetail(Finding? f)
        {
            _detail.Clear();
            var r = QaRunner.Report;
            if (r == null) return;
            if (f == null)
            {
                _detail.Add(Muted("Không có mục nào khớp bộ lọc."));
                return;
            }
            using var _ = Knowledge.ForGame(r.GameId);

            // Đầu mục: nhóm, mã, đối tượng, độ chắc
            var head = Row("d-head");
            var pill = new Label(f.Accepted != null ? "Đã chấp nhận" : HtmlReport.CatLabel(f.Category));
            pill.AddToClassList("pill");
            pill.AddToClassList(f.Accepted != null ? "acc" : Cls(f.Category));
            head.Add(pill);
            head.Add(Tag(f.Id));
            foreach (var cid in f.CaseIds)
            {
                var caseTitle = r.Cases.FirstOrDefault(c => c.Id == cid)?.Title;
                var t = Tag(cid, "case", (caseTitle ?? "") + "\n(bấm để xem case)");
                var id = cid;
                t.RegisterCallback<ClickEvent>(_ => SetMode(1, caseId: id));
                head.Add(t);
            }
            if (!string.IsNullOrEmpty(f.Subject)) head.Add(Tag((f.IsUserProperty ? "property " : "") + f.Subject, "subj-tag"));
            head.Add(Tag(HtmlReport.ConfLabel(f.Confidence)));
            if (f.Package != null) head.Add(Tag("trong package " + f.Package, "pkg", "Sửa ở package sẽ áp dụng cho mọi game dùng package này"));
            if (f.FromAi) head.Add(Tag("AI phát hiện", "ai"));
            if (f.NotOnDevice != null) head.Add(Tag("người chơi thật không gặp", "ai", f.NotOnDevice));
            if (f.AiVerdict != null) head.Add(Tag(f.AiVerdict switch { "confirmed" => "AI xác nhận", "rejected" => "AI: báo nhầm", _ => "AI: chưa chắc" }, "ai"));
            _detail.Add(head);

            var title = Selectable(new Label(f.Title));
            title.AddToClassList("d-title");
            _detail.Add(title);
            if (f.NotOnDevice != null)
                _detail.Add(Para("<b>Đã ẩn — người chơi thật (bản release trên điện thoại) không gặp:</b> " + f.NotOnDevice + ". Không tính vào số lỗi.", "detail-text"));

            AcceptBox(f);

            // 2 tab
            var tabs = Row("tabs");
            var content = new VisualElement();
            void Select(int t)
            {
                _tab = t;
                for (int i = 0; i < tabs.childCount; i++) tabs[i].EnableInClassList("on", i == t);
                content.Clear();
                using var __ = Knowledge.ForGame(r.GameId);
                if (t == 0) WhyPanel(content, f); else ReproPanel(content, f);
            }
            tabs.Add(new Button(() => Select(0)) { text = "Lý do trong code" });
            tabs.Add(new Button(() => Select(1)) { text = f.Repro?.NotApplicable != null ? "Tái hiện (không cần)" : "Tái hiện" });
            foreach (var b in tabs.Children()) b.AddToClassList("tab");
            _detail.Add(tabs);
            _detail.Add(content);
            Select(_tab);

            if (f.DocRef != null) _detail.Add(Muted("Doc: " + f.DocRef));
            _detailScroll.scrollOffset = Vector2.zero;
        }

        /// <summary>Log giả lập 1 kịch bản: mỗi cách làm 1 khối — các bước theo thứ tự trong code + đối chiếu doc / kho case.</summary>
        void ShowScenario(ScenarioLog log)
        {
            _detail.Clear();
            var title = Selectable(new Label("Kịch bản: " + log.Label));
            title.AddToClassList("d-title");
            _detail.Add(title);
            _detail.Add(Muted("Suy từ code, không chạy game: khi người chơi làm hành động này thì lần lượt bắn gì, biến đếm nào đổi. Giá trị phụ thuộc lúc chạy ghi theo biểu thức trong code; có nhánh thì ghi điều kiện. Bấm vị trí để mở code."));
            foreach (var run in log.Runs.Take(8))
            {
                var box = new VisualElement();
                box.AddToClassList("trace");
                box.Add(Para("<b>Thao tác:</b> " + run.Trigger));
                int n = 1;
                foreach (var it in run.Items)
                {
                    if (it.Kind == "step")
                    {
                        box.Add(Section("── " + it.Name + ": " + string.Join("", it.Values)));
                        continue;
                    }
                    var line = Row("sc-item");
                    var num = new Label((n++).ToString());
                    num.AddToClassList("hop-n");
                    line.Add(num);
                    line.Add(Tag(it.Kind switch { "event" => "event", "property" => "property", _ => "biến đếm" }, it.Kind == "counter" ? "" : "case"));
                    var col = new VisualElement();
                    col.AddToClassList("hop-col");
                    var name = new Label($"<b>{it.Name}</b>{(it.InDoc ? "" : "  <i>(ngoài doc)</i>")}");
                    col.Add(name);
                    if (it.Values.Count > 0) col.Add(Para(string.Join(" · ", it.Values)));
                    if (it.Condition != null) col.Add(Muted(it.Condition));
                    if (it.Loc != null) col.Add(Link(it.Loc));
                    line.Add(col);
                    box.Add(line);
                }
                foreach (var c in run.Checks)
                {
                    var sym = c.Level switch { "ok" => "✔", "error" => "✘", _ => "⚠" };
                    var refs = string.Join(", ", new[] { c.CaseId, c.FindingId }.Where(x => x != null));
                    var p = Para($"{sym} {c.Text}{(refs.Length > 0 ? $"  <i>({refs})</i>" : "")}", "sc-check");
                    p.AddToClassList(c.Level);
                    box.Add(p);
                }
                _detail.Add(box);
            }
            if (log.Runs.Count > 8) _detail.Add(Muted($"… và {log.Runs.Count - 8} cách làm khác (xem báo cáo HTML)."));
            _detailScroll.scrollOffset = Vector2.zero;
        }

        /// <summary>Chi tiết 1 case: điều phải đúng, kết quả trên game này, cách kiểm, và các lỗi vi phạm (bấm để xem).</summary>
        void ShowCase(CaseResult c)
        {
            _detail.Clear();
            var r = QaRunner.Report;
            if (r == null) return;

            var head = Row("d-head");
            var pill = new Label(CaseEngine.StatusLabel(c.Status));
            pill.AddToClassList("pill");
            pill.AddToClassList(CaseGroup(c.Status));
            head.Add(pill);
            head.Add(Tag(c.Id, "subj-tag"));
            if (!string.IsNullOrEmpty(c.Group)) head.Add(Tag(c.Group));
            if (c.Layer.Contains("game")) head.Add(Tag("riêng game này", "pkg", "Case lưu cho riêng game này (" + c.Layer + ")"));
            if (c.Basis != "doc") head.Add(Tag("có phần suy luận — chưa bật", "ai", "Phần doc / Master không ghi rõ: chưa kiểm cho tới khi được chốt"));
            _detail.Add(head);

            var title = Selectable(new Label(c.Title));
            title.AddToClassList("d-title");
            _detail.Add(title);

            var hint = CaseEngine.StatusHint(c.Status);
            if (hint.Length > 0) _detail.Add(Para(hint, "detail-text"));
            if (!string.IsNullOrEmpty(c.Why)) _detail.Add(Para("<b>Vì sao quan trọng:</b> " + c.Why));
            _detail.Add(Para($"<b>Vi phạm thì xếp nhóm:</b> {c.Category}"));
            var how = c.StaticCheck switch { "yes" => "có", "partial" => "một phần", "no" => "không (chỉ khi chơi thử)", _ => "chưa (đang làm)" };
            _detail.Add(Para($"<b>Check all kiểm:</b> {how} · <b>Record kiểm:</b> {(c.RuntimeCheck == "yes" ? "có" : "không")}"));

            if (c.FindingIds.Count > 0)
            {
                _detail.Add(Section($"Lỗi thuộc case này ({c.FindingIds.Count})"));
                foreach (var f in r.Findings.Where(x => x.CaseIds.Contains(c.Id)))
                {
                    var row = Row("case-finding");
                    var p = new Label(f.Accepted != null ? "Đã chấp nhận" : HtmlReport.CatLabel(f.Category));
                    p.AddToClassList("pill");
                    p.AddToClassList(f.Accepted != null ? "acc" : Cls(f.Category));
                    row.Add(p);
                    var link = new Label(f.Id + "  " + f.Title) { tooltip = "Bấm để xem lỗi này", enableRichText = false };
                    link.AddToClassList("link");
                    link.AddToClassList("case-link");
                    var ff = f;
                    link.RegisterCallback<ClickEvent>(_ => SetMode(0, finding: ff));
                    row.Add(link);
                    _detail.Add(row);
                }
            }

            if (c.Preconditions.Count > 0)
            {
                _detail.Add(Section("Điều kiện trước khi kiểm"));
                foreach (var s in c.Preconditions) _detail.Add(Para("• " + s));
            }
            if (c.Repro.Count > 0)
            {
                _detail.Add(Section("Cách kiểm khi chơi"));
                var steps = new VisualElement();
                steps.AddToClassList("steps");
                for (int i = 0; i < c.Repro.Count; i++)
                {
                    var row = Row("step");
                    var n = new Label((i + 1).ToString());
                    n.AddToClassList("step-n");
                    row.Add(n);
                    row.Add(Para(c.Repro[i]));
                    steps.Add(row);
                }
                _detail.Add(steps);
            }
            if (!string.IsNullOrEmpty(c.Example)) _detail.Add(Para("<b>Ví dụ:</b> " + c.Example));
            if (!string.IsNullOrEmpty(c.Source)) _detail.Add(Muted("Nguồn: " + c.Source));
            _detailScroll.scrollOffset = Vector2.zero;
        }

        /// <summary>Nút "Đúng thiết kế" (ngoại lệ riêng của game) / thông tin đã chấp nhận + "Bỏ chấp nhận".</summary>
        void AcceptBox(Finding f)
        {
            var r = QaRunner.Report!;
            if (r.GameId == null) return;
            if (f.Accepted is { } a)
            {
                var box = new VisualElement();
                box.AddToClassList("accepted-box");
                box.Add(Selectable(new Label($"<b>Đúng thiết kế của game này</b> — lý do: {a.Reason}" +
                                             (a.By != null ? $" · {a.By}" : "") + (a.At is { } at ? $" · {at:dd/MM/yyyy}" : "") +
                                             (a.Pending ? " · <i>mới lưu trên máy, chưa gửi lên kho chung</i>" : ""))));
                box.Add(new Button(() => QaRunner.Unaccept(f)) { text = "Bỏ chấp nhận" });
                _detail.Add(box);
                return;
            }
            if (f.Category == FindingCategory.OutOfPlan) return;

            var key = f.StableKey();
            if (_acceptingKey != key)
            {
                var b = new Button(() => { _acceptingKey = key; ShowDetail(f); })
                {
                    text = "Đúng thiết kế…",
                    tooltip = "Đánh dấu mục này là đúng thiết kế của game này — lần sau không tính là lỗi (ghi kèm lý do).",
                };
                b.AddToClassList("accept-btn");
                _detail.Add(b);
                return;
            }

            var form = new VisualElement();
            form.AddToClassList("accept-form");
            form.Add(new Label("Vì sao đây là đúng thiết kế của game? (vd: game cố ý bắn win=3 khi thua Golden Tiles)"));
            var tf = new TextField { multiline = true };
            tf.AddToClassList("reason");
            form.Add(tf);
            var btns = Row("accept-btns");
            var save = new Button(() =>
            {
                if (string.IsNullOrWhiteSpace(tf.value)) return;
                _acceptingKey = null;
                QaRunner.Accept(f, tf.value);
            }) { text = "Lưu" };
            save.AddToClassList("primary");
            btns.Add(save);
            btns.Add(new Button(() => { _acceptingKey = null; ShowDetail(f); }) { text = "Huỷ" });
            form.Add(btns);
            form.Add(Muted("Lưu trên máy này, chỉ cho game này. Gửi lên kho chung cho cả team sẽ có ở bản sau."));
            _detail.Add(form);
            tf.schedule.Execute(() => tf.Focus());
        }

        /// <summary>Tab "Lý do trong code": vì sao + đường đi từ thao tác người chơi tới chỗ bắn + vị trí liên quan.</summary>
        static void WhyPanel(VisualElement p, Finding f)
        {
            if (!string.IsNullOrEmpty(f.Why)) p.Add(Para((f.WhyFromAi ? "<b>Claude:</b> " : "") + f.Why, "why"));
            if (!string.IsNullOrEmpty(f.AiReason) && f.AiReason != f.Why) p.Add(Para("<b>Claude:</b> " + f.AiReason));
            if (!string.IsNullOrEmpty(f.Detail)) p.Add(Para(f.Detail, "detail-text"));

            var shown = new HashSet<CodeLocation>();
            if (f.Traces.Count > 0)
            {
                p.Add(Section(f.Traces.Count > 1 ? $"Đường đi trong code ({f.Traces.Count} đường)" : "Đường đi trong code"));
                bool firstTrace = true;
                foreach (var t in f.Traces)
                {
                    var box = new VisualElement();
                    box.AddToClassList("trace");
                    var entry = Row("tr-entry");
                    entry.Add(Selectable(new Label("<b>" + t.Entry.Action + "</b>")));
                    entry.Add(Tag(HtmlReport.EntryKindLabel(t.Entry.Kind)));
                    if (TriggerTracer.IsDebug(t)) entry.Add(Tag("chỉ có ở bản cheat/debug", "ai"));
                    if (t.Entry.Where is { } w) entry.Add(Link(w));
                    box.Add(entry);
                    for (int i = 0; i < t.Hops.Count; i++)
                    {
                        var h = t.Hops[i];
                        var last = i == t.Hops.Count - 1;
                        // "1. Hàm" rồi dòng dưới là vị trí (đường dẫn dài không đè sang bên)
                        var hop = Row("hop");
                        hop.EnableInClassList("last", last);
                        var n = new Label((i + 1) + ".");
                        n.AddToClassList("hop-n");
                        hop.Add(n);
                        var col = new VisualElement();
                        col.AddToClassList("hop-col");
                        var member = new Label(h.Member);
                        member.AddToClassList("member");
                        col.Add(member);
                        col.Add(Link(h));
                        hop.Add(col);
                        box.Add(hop);
                        if (last && firstTrace && f.Category != FindingCategory.OutOfPlan && CodeNav.Snippet(h.File, h.Line) is { } snip)
                        {
                            box.Add(Code(snip));
                            shown.Add(h);
                        }
                    }
                    if (t.Notes.Count > 0) box.Add(Muted("Ghi chú: " + string.Join("; ", t.Notes.Distinct())));
                    p.Add(box);
                    firstTrace = false;
                }
            }
            if (!string.IsNullOrEmpty(f.Suggestion)) p.Add(Para("<b>Gợi ý sửa:</b> " + f.Suggestion, "sugg"));

            if (f.Evidence.Count > 0)
            {
                VisualElement host = p;
                if (f.Traces.Count > 0)
                {
                    var fold = new Foldout { text = $"Tất cả vị trí liên quan ({f.Evidence.Count})", value = false };
                    p.Add(fold);
                    host = fold;
                }
                else p.Add(Section("Vị trí liên quan"));
                bool first = f.Traces.Count == 0;
                foreach (var e in f.Evidence.Take(20))
                {
                    var row = Row("ev");
                    row.Add(Link(e));
                    if (!string.IsNullOrEmpty(e.Member))
                    {
                        var m = new Label(e.Member);
                        m.AddToClassList("member");
                        row.Add(m);
                    }
                    host.Add(row);
                    if (first && f.Category != FindingCategory.OutOfPlan && !shown.Contains(e) && CodeNav.Snippet(e.File, e.Line) is { } snip)
                    {
                        host.Add(Code(snip));
                        first = false;
                    }
                }
                if (f.Evidence.Count > 20) host.Add(Muted($"… và {f.Evidence.Count - 20} chỗ khác"));
            }
        }

        /// <summary>Tab "Tái hiện": các bước như người chơi + mong đợi theo doc / thực tế theo code.</summary>
        static void ReproPanel(VisualElement p, Finding f)
        {
            var rp = f.Repro;
            if (rp == null) { p.Add(Muted("Chưa có thông tin tái hiện.")); return; }
            if (rp.NotApplicable != null) { p.Add(Para(rp.NotApplicable, "na")); return; }

            var src = Row("rp-src");
            src.Add(Tag(rp.Source == "claude" ? "Claude soạn" : "Tự suy ra từ code", rp.Source == "claude" ? "ai" : ""));
            if (rp.Uncertain) src.Add(Tag("chưa chắc thao tác", "ai"));
            src.Add(Muted("Chưa chạy thật — QA làm theo 1 lần để xác nhận."));
            p.Add(src);
            if (rp.DocTrigger != null) p.Add(Para("<b>Theo doc, event bắn khi:</b> " + rp.DocTrigger));

            var steps = new VisualElement();
            steps.AddToClassList("steps");
            for (int i = 0; i < rp.Steps.Count; i++)
            {
                var row = Row("step");
                var n = new Label((i + 1).ToString());
                n.AddToClassList("step-n");
                row.Add(n);
                row.Add(Para(rp.Steps[i]));
                steps.Add(row);
            }
            p.Add(steps);

            if (rp.Expected != null || rp.Actual != null)
            {
                var ea = Row("ea");
                if (rp.Expected != null) ea.Add(Box("Mong đợi (theo doc)", rp.Expected, "exp"));
                if (rp.Actual != null) ea.Add(Box("Sẽ thấy (theo code hiện tại)", rp.Actual, "act"));
                p.Add(ea);
            }
            if (rp.Verify != null) p.Add(Para("<b>Cách kiểm tra:</b> " + rp.Verify));
            if (rp.Alternatives.Count > 0)
            {
                p.Add(Section(f.Rule is "value_never_sent" or "event_missing" or "event_only_in_dead_code" or "prop_missing"
                    ? "Các thao tác đang đi tới chỗ bắn này (để đối chiếu)" : "Các thao tác khác cũng đi vào luồng này"));
                foreach (var a in rp.Alternatives) p.Add(Para("• " + a));
            }
        }

        // ------------------------------------------------------------------ hành động

        void PickDoc()
        {
            var cur = QaRunner.DocPath;
            var p = EditorUtility.OpenFilePanel("Chọn file tracking plan (Excel)", string.IsNullOrEmpty(cur) ? "" : Path.GetDirectoryName(cur), "xlsx");
            if (string.IsNullOrEmpty(p)) return;
            QaRunner.DocPath = p.Replace('/', Path.DirectorySeparatorChar);
            Refresh();
        }

        static void OpenHtml()
        {
            var p = QaRunner.WriteHtml();
            if (p != null) Application.OpenURL("file:///" + p.Replace('\\', '/'));
        }

        // ------------------------------------------------------------------ phần tử nhỏ

        static VisualElement Row(string cls)
        {
            var v = new VisualElement();
            v.AddToClassList("hrow");
            v.AddToClassList(cls);
            return v;
        }

        static T Selectable<T>(T l) where T : TextElement
        {
            l.selection.isSelectable = true;
            return l;
        }

        static Label Para(string text, string? cls = null)
        {
            var l = Selectable(new Label(text));
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

        static Label Code(string text)
        {
            var l = Selectable(new Label(text) { enableRichText = false });
            l.AddToClassList("code");
            if (_mono != null) l.style.unityFontDefinition = FontDefinition.FromFont(_mono);
            return l;
        }

        static VisualElement Box(string title, string text, string cls)
        {
            var b = new VisualElement();
            b.AddToClassList("ea-box");
            b.AddToClassList(cls);
            var k = new Label(title);
            k.AddToClassList("ea-k");
            b.Add(k);
            b.Add(Para(text));
            return b;
        }

        static Label Link(CodeLocation loc)
        {
            // Package trong Library hiện gọn "Packages/<tên>/..." (bỏ mã hash), đường dẫn đầy đủ ở tooltip
            var shown = CodeNav.AssetPath(loc.File) ?? loc.File;
            var l = new Label(shown + (loc.Line > 0 ? ":" + loc.Line : ""))
            {
                tooltip = (loc.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "Mở trong IDE: " : "Chọn trong Project: ") + loc.File,
                enableRichText = false,
            };
            l.AddToClassList("link");
            l.RegisterCallback<ClickEvent>(_ => CodeNav.Open(loc.File, loc.Line));
            return l;
        }
    }
}
