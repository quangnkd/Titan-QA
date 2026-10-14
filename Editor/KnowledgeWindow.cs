#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TrackingChecker.Core;
using TrackingChecker.Core.Live;
using TrackingChecker.Core.Rules;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Titan.TrackingQA
{
    /// <summary>
    /// Cửa sổ Kho chung: chỉnh sửa trên máy chờ gửi (ngoại lệ, case G-xxx, chỉnh doc) → gửi lên kho chung bằng PR (người duyệt merge);
    /// lịch sử đã gửi; học từ chỗ Check all sai (báo nhầm theo luật + lý do, lệch log giả lập khi Record).
    /// Bản trên máy chỉ được dọn khi package đã cập nhật có đủ (sau khi PR được merge và phát hành bản mới).
    /// </summary>
    public sealed class KnowledgeWindow : EditorWindow
    {
        const string PrefUrl = "Titan.TrackingQA.KbRepoUrl";
        static string SentPath => Path.Combine(QaRunner.ProjectRoot, "UserSettings", "TrackingQA", "kb-sent.json");
        static string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TitanTrackingQA", "kb-repo");

        sealed class Sent
        {
            public DateTime At { get; set; }
            public string Branch { get; set; } = "";
            public string? PrUrl { get; set; }
            public string? CompareUrl { get; set; }
            public List<string> Items { get; set; } = new List<string>();
            /// <summary>Chỉnh sửa trên máy lúc gửi: đường dẫn → mã băm (chưa đổi từ lúc gửi = đã gửi, không tính là chờ gửi).</summary>
            public Dictionary<string, string>? Files { get; set; }
            /// <summary>File đã ghi vào kho chung (đường dẫn trong Knowledge/, không gồm .meta) — để kiểm đã merge chưa.</summary>
            public List<string> Targets { get; set; } = new List<string>();
        }

        /// <summary>Trạng thái trên GitHub của các lần gửi (nhánh → PR / đã merge), kiểm ngầm khi mở cửa sổ.</summary>
        static Dictionary<string, KnowledgePublisher.BranchStatus> _remote = new Dictionary<string, KnowledgePublisher.BranchStatus>();
        static double _lastCheck = -1000;
        static bool _checking;
        static string? _checkError;

        VisualElement _body = null!;
        /// <summary>Nơi đang thêm nội dung: thân cửa sổ, hoặc mục thu gọn vừa mở bằng <see cref="Fold"/>.</summary>
        VisualElement _cur = null!;
        Label _status = null!;
        bool _busy;
        string? _lastCleaned;
        string _caseQuery = "";

        public static void Open() => GetWindow<KnowledgeWindow>("Tracking QA · Kho chung").minSize = new Vector2(560, 360);

        /// <summary>Chỉnh sửa trên máy của game đang mở: chưa gửi (hoặc đã đổi sau khi gửi) / đã gửi, đang chờ duyệt (hiện trên nút ở cửa sổ CheckAll).</summary>
        public static (int Unsent, int Waiting) Counts()
        {
            try
            {
                var fp = KnowledgeShare.Fingerprint(QaRunner.GameId);
                var sent = LoadSent();
                var waiting = fp.Count(x => SentFor(x.Key, x.Value, sent) != null);
                return (fp.Count - waiting, waiting);
            }
            catch { return (0, 0); }
        }

        /// <summary>Lần gửi gần nhất có đúng nội dung này (chưa đổi từ lúc gửi); bản ghi cũ không có mã băm thì so theo thời điểm sửa file.</summary>
        static Sent? SentFor(string rel, string hash, List<Sent> sent)
        {
            foreach (var s in sent.OrderByDescending(x => x.At))
            {
                if (s.Files != null) { if (s.Files.TryGetValue(rel, out var h) && h == hash) return s; continue; }
                var path = Path.Combine(Knowledge.LocalDir, rel);
                if (File.Exists(path) && File.GetLastWriteTime(path) <= s.At && s.Items.Count > 0) return s;
            }
            return null;
        }

        void OnFocus()
        {
            Render();
            CheckStatus(false);
        }

        /// <summary>Kiểm ngầm trạng thái trên GitHub (nhánh, PR, đã merge) của các lần gửi — tối đa 1 lần / phút trừ khi bấm nút.</summary>
        void CheckStatus(bool force)
        {
            var url = RepoUrl();
            var sent = LoadSent();
            if (url == null || sent.Count == 0 || _checking) return;
            if (!force && EditorApplication.timeSinceStartup - _lastCheck < 60) return;
            _checking = true;
            _lastCheck = EditorApplication.timeSinceStartup;
            var list = sent.Select(s => (s.Branch, s.Targets.Count > 0 ? s.Targets : (s.Files?.Keys.ToList() ?? new List<string>()))).ToList();
            var work = WorkDir;
            Task.Run(async () =>
            {
                try
                {
                    var st = await KnowledgePublisher.StatusAsync(url, work, list, _ => { });
                    QaRunner.Post(() => { _remote = st; _checkError = null; _checking = false; Render(); });
                }
                catch (Exception e) { QaRunner.Post(() => { _checkError = KnowledgePublisher.Mask(e.Message); _checking = false; Render(); }); }
            });
            Render();
        }

        /// <summary>Lời trạng thái của 1 lần gửi.</summary>
        static (string Text, string Cls, string? Link, string? LinkText) StateOf(Sent s)
        {
            if (!_remote.TryGetValue(s.Branch, out var st))
                return (_checking ? "đã gửi — đang kiểm trạng thái…" : "đã gửi", "info", s.PrUrl ?? s.CompareUrl, s.PrUrl != null ? "Mở PR" : "Tạo PR");
            if (st.Merged) return ("✔ đã merge vào kho chung — chờ phát hành bản package mới (cập nhật package xong thì tự dọn trên máy)", "ok", st.PrUrl, st.PrUrl != null ? $"PR #{st.PrNumber}" : null);
            if (st.PrNumber != null) return ($"PR #{st.PrNumber} đang chờ duyệt", "warn", st.PrUrl, "Mở PR");
            if (st.BranchExists) return ("đã đẩy nhánh, CHƯA tạo PR", "warn", s.CompareUrl, "Tạo PR");
            return ("nhánh đã bị xoá mà chưa merge — gửi lại nếu vẫn cần", "error", null, null);
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            var css = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.titan.tracking-qa/Editor/TrackingQAWindow.uss");
            if (css != null) root.styleSheets.Add(css);
            root.AddToClassList("tqa");
            var head = new Label();
            head.AddToClassList("game");
            head.text = "Game: " + (QaRunner.GameId ?? "?");
            root.Add(head);
            _status = new Label();
            _status.AddToClassList("status");
            root.Add(_status);
            var scroll = new ScrollView();
            scroll.AddToClassList("detail-scroll");
            _body = new VisualElement();
            _body.AddToClassList("detail");
            scroll.Add(_body);
            root.Add(scroll);
            Render();
            CheckStatus(false);
        }

        /// <summary>URL git của kho chung: ghi đè trong cửa sổ, hoặc nguồn cài package (git URL / remote của thư mục file:).</summary>
        static string? RepoUrl()
        {
            var custom = EditorPrefs.GetString(PrefUrl, "");
            if (custom.Length > 0) return custom;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(KnowledgeWindow).Assembly);
            if (info == null) return null;
            if (info.source == UnityEditor.PackageManager.PackageSource.Git)
            {
                var id = info.packageId;
                var at = id.IndexOf('@');
                if (at < 0) return null;
                var url = id.Substring(at + 1);
                var hash = url.IndexOf('#');
                return hash >= 0 ? url.Substring(0, hash) : url;
            }
            if (info.source == UnityEditor.PackageManager.PackageSource.Local)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("git", "remote get-url origin")
                    {
                        WorkingDirectory = info.resolvedPath, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    var o = p?.StandardOutput.ReadToEnd().Trim();
                    p?.WaitForExit();
                    return string.IsNullOrEmpty(o) ? null : o;
                }
                catch { return null; }
            }
            return null;
        }

        /// <summary>Huỷ các chỉnh sửa trên máy chưa gửi (hỏi xác nhận). Case bị xoá thì gỡ khỏi lỗi Record đã lưu.</summary>
        static bool Discard(IReadOnlyList<KnowledgeShare.Item> items)
        {
            if (items.Count == 0) return false;
            var names = string.Join("\n", items.Take(8).Select(i => "• " + i.Title)) + (items.Count > 8 ? $"\n… và {items.Count - 8} mục khác" : "");
            var exNote = items.Any(i => i.Kind == "exceptions") ? "\n\nNgoại lệ: xoá mọi ngoại lệ lưu trên máy chưa gửi — các mục đó hiện lại trong CheckAll." : "";
            if (!EditorUtility.DisplayDialog("Xoá chỉnh sửa chưa gửi", $"Xoá {items.Count} chỉnh sửa trên máy?\n\n{names}{exNote}\n\nKho chung không đổi.", "Xoá", "Huỷ"))
                return false;
            foreach (var i in items) KnowledgeShare.Discard(i.Rel);
            QaRunner.ForgetCase(null);
            return true;
        }

        static List<Sent> LoadSent()
        {
            try { return File.Exists(SentPath) ? JsonSerializer.Deserialize<List<Sent>>(File.ReadAllText(SentPath)) ?? new List<Sent>() : new List<Sent>(); }
            catch { return new List<Sent>(); }
        }

        static void SaveSent(List<Sent> sent)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SentPath)!);
                File.WriteAllText(SentPath, JsonSerializer.Serialize(sent, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>
        /// Bỏ khỏi lịch sử các lần gửi đã xong việc: bản trên máy đã gửi không còn (đã dọn vì kho chung có đủ, hoặc QA đã xoá / sửa và gửi lại).
        /// Chỉ giữ lần gửi còn cần theo dõi (PR chờ duyệt, chưa tạo PR, đã merge mà chưa cập nhật package).
        /// </summary>
        static List<Sent> PruneSent(List<Sent> sent, Dictionary<string, string> fp)
        {
            var keep = sent.Where(s => s.Files != null ? s.Files.Any(x => fp.TryGetValue(x.Key, out var h) && h == x.Value) : fp.Count > 0).ToList();
            if (keep.Count != sent.Count) SaveSent(keep);
            return keep;
        }

        void Render()
        {
            if (_body == null) return;
            _body.Clear();
            _cur = _body;
            var game = QaRunner.GameId;
            // Package đã cập nhật (PR đã merge) → dọn bản trên máy đã có đủ trong kho chung
            try
            {
                var cleaned = KnowledgeShare.CleanLocal(game);
                if (cleaned.Count > 0)
                {
                    _lastCleaned = $"Đã dọn {cleaned.Count} chỉnh sửa trên máy vì kho chung (bản package đang cài) đã có: {string.Join(", ", cleaned)}";
                    QaRunner.ReapplyKnowledge();
                }
            }
            catch (Exception e) { Debug.LogWarning("[Tracking QA] Không dọn được bản trên máy: " + e.Message); }
            if (_lastCleaned != null) _cur.Add(Para(_lastCleaned, "detail-text"));

            // --- Chờ gửi
            List<KnowledgeShare.Item> items;
            try { items = KnowledgeShare.Plan(game); }
            catch (Exception e) { items = new List<KnowledgeShare.Item>(); _cur.Add(Para("Không đọc được chỉnh sửa trên máy: " + e.Message, "detail-text")); }
            Dictionary<string, string> fp;
            try { fp = KnowledgeShare.Fingerprint(game); } catch { fp = new Dictionary<string, string>(); }
            var sentAll = PruneSent(LoadSent(), fp);
            var unsent = items.Count(i => !fp.TryGetValue(i.Rel, out var h) || SentFor(i.Rel, h, sentAll) == null);
            var url = RepoUrl();
            if (items.Count == 0)
            {
                var none = Muted("Chỉnh sửa trên máy: không có gì chờ gửi.");
                none.tooltip = "Ngoại lệ, case G-xxx, chỉnh doc lưu trên máy sẽ hiện ở đây để gửi cho cả team.";
                _body.Add(none);
            }
            else Fold(null, $"Chỉnh sửa trên máy ({items.Count}) — {unsent} chưa gửi, {items.Count - unsent} đã gửi", true);
            foreach (var it in items)
            {
                var box = new VisualElement();
                box.AddToClassList("trace");
                var by = fp.TryGetValue(it.Rel, out var hh) ? SentFor(it.Rel, hh, sentAll) : null;
                var head = Row("ev");
                head.style.flexWrap = Wrap.Wrap;
                var (stText, stCls, stLink, stLinkText) = by != null ? StateOf(by) : ("CHƯA GỬI", "warn", null, null);
                var pill = new Label(by != null ? $"Đã gửi {by.At:dd/MM HH:mm}" : "Chưa gửi");
                pill.AddToClassList("pill");
                pill.AddToClassList(by == null ? "warn" : stCls == "ok" ? "pass" : "info");
                head.Add(pill);
                head.Add(Para($"  <b>{it.Title}</b>  <i>Knowledge/{it.Rel}</i>"));
                if (by == null)
                {
                    var item = it;
                    head.Add(new Button(() => { if (Discard(new[] { item })) Render(); })
                    {
                        text = "Xoá",
                        tooltip = "Huỷ chỉnh sửa này trên máy (chưa gửi nên kho chung không đổi)",
                    });
                }
                box.Add(head);
                if (by != null)
                {
                    var stRow = Row("ev");
                    stRow.style.flexWrap = Wrap.Wrap;
                    var sp = Para("→ " + stText + "  ", "sc-check");
                    sp.AddToClassList(stCls);
                    stRow.Add(sp);
                    if (stLink != null) stRow.Add(Link(stLinkText ?? "Mở", stLink));
                    box.Add(stRow);
                }
                foreach (var l in it.Lines.Take(30)) box.Add(Para("• " + l));
                if (it.Lines.Count > 30) box.Add(Muted($"… và {it.Lines.Count - 30} mục khác"));
                _cur.Add(box);
            }

            if (items.Count > 0) SendBox(url, unsent, items, fp, sentAll);

            // --- Đã gửi: chỉ lần gửi còn cần theo dõi (xong việc thì tự bỏ khỏi danh sách)
            if (sentAll.Count > 0)
            {
                Fold("sent", $"Đã gửi, đang chờ ({sentAll.Count})", true);
                foreach (var s in sentAll.OrderByDescending(x => x.At).Take(10))
                {
                    var row = Row("ev");
                    row.style.flexWrap = Wrap.Wrap;
                    var (t, cls, link, linkText) = StateOf(s);
                    row.Add(new Label($"{s.At:dd/MM HH:mm} · {string.Join(", ", s.Items.Take(3))}{(s.Items.Count > 3 ? "…" : "")} · "));
                    var sl = Para(t + "  ", "sc-check");
                    sl.AddToClassList(cls);
                    row.Add(sl);
                    if (link != null) row.Add(Link(linkText ?? "Mở", link));
                    row.tooltip = s.Branch;
                    _cur.Add(row);
                }
                var check = new Button(() => CheckStatus(true)) { text = _checking ? "Đang kiểm…" : "Kiểm tra trạng thái", tooltip = "Xem trên GitHub: đã tạo PR chưa, đã merge chưa" };
                check.SetEnabled(!_checking && url != null);
                _cur.Add(check);
                if (_checkError != null) _cur.Add(Muted("Không kiểm được trạng thái: " + _checkError));
            }

            Rest(game);
        }

        /// <summary>Ô gửi lên kho chung (chỉ hiện khi có chỉnh sửa trên máy).</summary>
        void SendBox(string? url, int unsent, List<KnowledgeShare.Item> items, Dictionary<string, string> fp, List<Sent> sentAll)
        {
            var urlRow = Row("ev");
            urlRow.style.flexWrap = Wrap.Wrap;
            urlRow.Add(new Label("Kho chung: "));
            var urlField = new TextField { value = EditorPrefs.GetString(PrefUrl, ""), tooltip = "Để trống = dùng nguồn cài package (git URL trong manifest)" };
            urlField.style.minWidth = 260;
            urlField.style.flexGrow = 1;
            urlField.RegisterValueChangedCallback(e => EditorPrefs.SetString(PrefUrl, e.newValue.Trim()));
            urlRow.Add(urlField);
            _cur.Add(urlRow);
            _cur.Add(Muted(url != null ? "Gửi tới: " + KnowledgePublisher.Mask(url) : "Không biết kho chung ở đâu (package không cài từ git) — nhập URL git của repo package."));

            var btns = Row("accept-btns");
            var send = new Button(() => Publish(url!))
            {
                text = _busy ? "Đang gửi…" : unsent > 0 ? $"Gửi lên kho chung (tạo PR) · {unsent} chưa gửi" : "Không có gì mới để gửi",
                tooltip = "Gửi mọi chỉnh sửa trên máy chưa có trong kho chung (cả mục đã gửi mà chưa merge — PR mới thay PR cũ)",
            };
            send.AddToClassList("primary");
            send.SetEnabled(!_busy && unsent > 0 && url != null);
            btns.Add(send);
            if (unsent > 1)
            {
                var unsentItems = items.Where(i => !fp.TryGetValue(i.Rel, out var h) || SentFor(i.Rel, h, sentAll) == null).ToList();
                btns.Add(new Button(() => { if (Discard(unsentItems)) Render(); }) { text = $"Xoá hết {unsent} chưa gửi", tooltip = "Huỷ mọi chỉnh sửa trên máy chưa gửi" });
            }
            _cur.Add(btns);
            _cur.Add(Muted("Tạo nhánh mới trong repo package, ghi các chỉnh sửa trên, push và tạo Pull Request cho người duyệt merge. " +
                            "Dùng quyền git trên máy này. Chưa merge thì bản trên máy vẫn giữ và vẫn có hiệu lực trên máy này."));
        }

        /// <summary>Phần dưới: ngoại lệ, học từ chỗ Check all sai, đề xuất nâng case chung, lệch giả lập (ẩn phần trống).</summary>
        void Rest(string? game)
        {
            CaseList(game);

            // --- Ngoại lệ đang có hiệu lực (CheckAll không hiện mục đã chấp nhận → bỏ ở đây nếu lỡ đánh nhầm)
            Dictionary<string, (GameData.AcceptedEntry E, bool Pending)> ex;
            try { ex = game != null ? GameData.Exceptions(game) : new Dictionary<string, (GameData.AcceptedEntry, bool)>(); } catch { ex = new Dictionary<string, (GameData.AcceptedEntry, bool)>(); }
            if (ex.Count > 0)
            {
                Fold("ex", $"Ngoại lệ của game ({ex.Count})", false);
                foreach (var (key, (e, pending)) in ex.OrderBy(x => x.Value.E.Subject, StringComparer.Ordinal))
                {
                    var row = Row("ev");
                    row.style.flexWrap = Wrap.Wrap;
                    var isFp = e.Kind == "false_positive";
                    var pill = new Label(isFp ? "Báo nhầm" : "Đúng thiết kế");
                    pill.AddToClassList("pill");
                    pill.AddToClassList(isFp ? "info" : "acc");
                    row.Add(pill);
                    var text = Para($"  <b>{e.Subject}</b> · {e.Rule} — {(isFp ? GameData.CauseLabel(e.Cause) + " · " : "")}{e.Reason}{(e.By != null ? $" ({e.By})" : "")}{(pending ? "  <i>trên máy</i>" : "  <i>kho chung</i>")}  ");
                    text.tooltip = e.Title;
                    row.Add(text);
                    var k = key;
                    row.Add(new Button(() =>
                    {
                        if (!EditorUtility.DisplayDialog("Bỏ ngoại lệ", $"Bỏ ngoại lệ “{e.Title}”?\nMục này sẽ hiện lại trong CheckAll." + (pending ? "" : "\nNgoại lệ đang ở kho chung → việc bỏ cũng cần gửi lên kho chung."), "Bỏ", "Huỷ")) return;
                        QaRunner.UnacceptKey(k);
                        Render();
                    }) { text = "Bỏ", tooltip = "Bỏ ngoại lệ này — mục hiện lại trong CheckAll" });
                    _cur.Add(row);
                }
            }

            // --- Học từ chỗ Check all sai
            try
            {
                var games = Knowledge.KnownGames();
                if (game != null && !games.Contains(game)) games.Add(game);
                var stats = Learning.Collect(games);
                if (stats.Count > 0) Fold("learn", $"Học từ chỗ Check all sai ({stats.Count} luật)", false);
                foreach (var s in stats)
                    _cur.Add(Para($"<b>{s.Rule}</b> — báo nhầm {s.FalsePositives}" +
                                   (s.Causes.Count > 0 ? " (" + string.Join(", ", s.Causes.Select(c => $"{GameData.CauseLabel(c.Key)} ×{c.Value}")) + ")" : "") +
                                   (s.FpGames.Count > 0 ? $" ở {string.Join(", ", s.FpGames)}" : "") + $" · đúng thiết kế {s.ByDesign}"));
                foreach (var sug in Learning.Suggestions(stats)) { var p = Para("→ " + sug, "sc-check"); p.AddToClassList("warn"); _cur.Add(p); }
            }
            catch (Exception e) { _cur.Add(Muted("Không tổng hợp được: " + e.Message)); }

            var fb = RecordFeedback.Load(RecordController.FeedbackPath);
            // --- Đề xuất nâng case riêng game thành case chung
            try
            {
                var games = Knowledge.KnownGames();
                if (game != null && !games.Contains(game)) games.Add(game);
                var promos = Learning.CasePromotions(games);
                if (promos.Count > 0) Fold("promo", $"Đề xuất nâng thành case chung ({promos.Count})", false);
                foreach (var p in promos)
                {
                    var box = new VisualElement();
                    box.AddToClassList("trace");
                    var list = string.Join(", ", p.Cases.Select(c => $"{c.Id} ({c.Game})"));
                    var text = p.CoveredBy.Count > 0
                        ? $"<b>{p.Label}</b> — đã có case chung {string.Join(", ", p.CoveredBy)} bao → có thể bỏ {list}"
                        : $"<b>{p.Label}</b> — gặp ở {p.GameCount} game: {list} → đề xuất nâng thành TC chung";
                    box.Add(Para(text));
                    foreach (var t in p.Cases.Select(c => c.Title).Distinct().Take(3)) box.Add(Muted("“" + t + "”"));
                    var req = p.CoveredBy.Count > 0
                        ? $"Bỏ case riêng {list} vì case chung {string.Join(", ", p.CoveredBy)} đã bao ({p.Label.Replace("`", "")})."
                        : $"Nâng thành case chung: {p.Label.Replace("`", "")} — gặp ở {p.GameCount} game ({list}). Soạn TC, chạy bộ game chuẩn, gửi PR để duyệt.";
                    box.Add(new Button(() => { EditorGUIUtility.systemCopyBuffer = req; _status.text = "Đã copy — dán cho Claude để làm."; _status.style.display = DisplayStyle.Flex; })
                    {
                        text = "Copy yêu cầu", tooltip = req,
                    });
                    _cur.Add(box);
                }
            }
            catch (Exception e) { _cur.Add(Muted("Không tổng hợp được đề xuất: " + e.Message)); }

            var diffs = fb.Scenarios.Values.Where(x => x.Diffs > 0).OrderByDescending(x => x.Diffs).ToList();
            if (diffs.Count > 0)
            {
                Fold("diff", $"Record lệch log giả lập ({diffs.Count} thao tác)", false);
                _cur.Add(Muted("Chỗ Check all lần luồng khác thực tế:"));
                foreach (var d in diffs.Take(10))
                {
                    _cur.Add(Para($"• {d.Trigger}: lệch {d.Diffs}/{d.Plays} lần"));
                    foreach (var n in d.LastDiff.Take(3)) _cur.Add(Muted("    " + n));
                }
            }
            _status.text = _busy ? "Đang gửi lên kho chung…" : "";
            _status.style.display = _busy ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>File Excel case dùng chung: đang dùng file nào (mở / đổi / thôi dùng), hoặc tạo file từ các case hiện có.</summary>
        void SheetRow(int count)
        {
            var path = QaRunner.CaseSheetPath;
            var row = Row("ev");
            row.style.flexWrap = Wrap.Wrap;
            if (path != null && File.Exists(path))
            {
                row.Add(new Label("File case: "));
                var name = new Label(path) { tooltip = "Thêm / sửa / bỏ case ở đây hoặc ngay trong Excel — cả team dùng chung file này. Dòng mới để trống cột Mã; gạch ngang dòng = bỏ case." };
                name.style.flexShrink = 1;
                row.Add(name);
                row.Add(new Button(() => EditorUtility.OpenWithDefaultApp(path)) { text = "Mở file" });
                row.Add(new Button(() => PickSheet(path)) { text = "Đổi…" });
                row.Add(new Button(() =>
                {
                    if (!EditorUtility.DisplayDialog("File case", "Thôi dùng file case trên máy này? Case quay về kho chung (package + chỉnh sửa trên máy).", "Thôi dùng", "Huỷ")) return;
                    QaRunner.CaseSheetPath = null;
                    Render();
                }) { text = "Thôi dùng" });
                _cur.Add(row);
                _cur.Add(Muted("Sửa trong Excel xong, bấm Check all để đọc lại. Dòng mới để trống cột Mã — Check all tự cấp mã."));
                return;
            }
            if (path != null) _cur.Add(Muted($"Không thấy file case: {path} (Drive chưa đồng bộ?) — đang dùng kho chung."));
            row.Add(new Label("Chưa dùng file case chung: "));
            row.Add(new Button(() =>
            {
                var p = EditorUtility.SaveFilePanel("Tạo file case dùng chung (vd trong thư mục Drive của team)", "", "Tracking QA - Case.xlsx", "xlsx");
                if (string.IsNullOrEmpty(p)) return;
                if (File.Exists(p) && !EditorUtility.DisplayDialog("File case", "File đã có — ghi đè bằng các case hiện có?", "Ghi đè", "Huỷ")) return;
                if (QaRunner.CreateCaseSheet(p)) Render();
            }) { text = $"Tạo file ({count} case)…", tooltip = "Tạo file Excel gồm mọi case đang dùng (kể cả case lưu trên máy) — đặt trong thư mục Drive để cả team dùng chung" });
            row.Add(new Button(() => PickSheet(null)) { text = "Chọn file có sẵn…" });
            _cur.Add(row);
        }

        void PickSheet(string? current)
        {
            var p = EditorUtility.OpenFilePanel("Chọn file case dùng chung", current != null ? Path.GetDirectoryName(current) : "", "xlsx");
            if (string.IsNullOrEmpty(p)) return;
            QaRunner.CaseSheetPath = p;
            Render();
        }

        /// <summary>Kho case: mọi case đang dùng (TC chung + G riêng game) — tìm, thêm, sửa, bỏ.</summary>
        void CaseList(string? game)
        {
            List<TrackingCase> cases;
            try { using (Knowledge.ForGame(game)) cases = CaseStore.Active(game); }
            catch (Exception e) { _cur.Add(Muted("Không đọc được kho case: " + e.Message)); return; }
            var nCommon = cases.Count(c => CaseStore.IsCommon(c.Id));
            Fold("cases", $"Kho case ({nCommon} chung · {cases.Count - nCommon} riêng game)", false);
            var top = Row("ev");
            top.style.flexWrap = Wrap.Wrap;
            var add = new Button(CaseEditWindow.Add) { text = "+ Thêm case", tooltip = "Thêm case chung (TC, mọi game) hoặc riêng game này (G)" };
            add.AddToClassList("primary");
            top.Add(add);
            var search = new TextField { value = _caseQuery, tooltip = "Tìm theo mã, nội dung, nhóm" };
            search.style.minWidth = 220;
            search.style.flexGrow = 1;
            top.Add(search);
            _cur.Add(top);
            SheetRow(cases.Count);
            var list = new VisualElement();
            _cur.Add(list);

            void Fill()
            {
                list.Clear();
                var q = _caseQuery.Trim();
                foreach (var c in cases.Where(c => q.Length == 0 || c.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                                   || c.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                                   || c.Group.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var row = Row("ev");
                    row.style.alignItems = Align.FlexStart;
                    var id = new Label($"<b>{c.Id}</b>");
                    id.style.minWidth = 52;
                    row.Add(id);
                    var local = c.Layer.StartsWith("máy", StringComparison.Ordinal);
                    var text = Para(c.Title + (CaseStore.IsCommon(c.Id) ? "" : "  <i>riêng game</i>") +
                                    (CaseStore.IsManual(c) ? "  <i>kiểm tay</i>" : "") +
                                    (local ? (game != null && CaseStore.IsLocalOnly(game, c.Id) ? "  <i>mới, chưa gửi</i>" : "  <i>đã sửa trên máy</i>") : ""));
                    text.style.flexGrow = 1;
                    text.style.flexShrink = 1;
                    text.tooltip = (c.Group.Length > 0 ? c.Group + " · " : "") + (c.Kind == "manual" ? "Kiểm tay" : "Cách kiểm: " + c.Kind) +
                                   (c.FromRules.Count > 0 ? " · luật " + string.Join(", ", c.FromRules) : "");
                    row.Add(text);
                    var cid = c.Id;
                    row.Add(new Button(() => CaseEditWindow.Edit(cid)) { text = "Sửa" });
                    row.Add(new Button(() => { if (QaRunner.RemoveCase(cid)) Render(); }) { text = "Bỏ" });
                    list.Add(row);
                }
                if (list.childCount == 0) list.Add(Muted("Không có case nào khớp."));
            }
            search.RegisterValueChangedCallback(e => { _caseQuery = e.newValue; Fill(); });
            Fill();
        }

        void Publish(string url)
        {
            var game = QaRunner.GameId;
            if (!EditorUtility.DisplayDialog("Gửi lên kho chung",
                    $"Tạo nhánh mới và Pull Request trong repo:\n{KnowledgePublisher.Mask(url)}\n\nGồm {KnowledgeShare.Plan(game).Count} chỉnh sửa trên máy của game {game ?? "(chung)"}. Người duyệt sẽ xem và merge.",
                    "Gửi", "Huỷ")) return;
            _busy = true;
            Render();
            string learning;
            try
            {
                var games = Knowledge.KnownGames();
                if (game != null && !games.Contains(game)) games.Add(game);
                learning = Learning.Markdown(Learning.Collect(games)) + Environment.NewLine + Learning.PromotionsMarkdown(Learning.CasePromotions(games));
            }
            catch { learning = ""; }
            var req = new KnowledgePublisher.Request { RepoUrl = url, WorkDir = WorkDir, Game = game, Author = Environment.UserName, Learning = learning };
            Task.Run(async () =>
            {
                try
                {
                    var res = await KnowledgePublisher.PublishAsync(req, s => QaRunner.Post(() => { if (_status != null) _status.text = s; }));
                    QaRunner.Post(() => Done(res, null));
                }
                catch (Exception e) { QaRunner.Post(() => Done(null, e)); }
            });
        }

        void Done(KnowledgePublisher.Result? res, Exception? error)
        {
            _busy = false;
            if (error != null)
            {
                Debug.LogWarning("[Tracking QA] Gửi kho chung lỗi: " + KnowledgePublisher.Mask(error.Message));
                EditorUtility.DisplayDialog("Gửi lên kho chung", "Không gửi được:\n" + KnowledgePublisher.Mask(error.Message), "OK");
            }
            else if (res != null && res.NothingNew)
                EditorUtility.DisplayDialog("Gửi lên kho chung", "Kho chung đã có đủ các chỉnh sửa này — không cần gửi.", "OK");
            else if (res != null)
            {
                var sent = LoadSent();
                Dictionary<string, string>? files;
                try { files = KnowledgeShare.Fingerprint(QaRunner.GameId); } catch { files = null; } // sau khi đổi mã case trên máy
                sent.Add(new Sent
                {
                    At = DateTime.Now, Branch = res.Branch, PrUrl = res.PrUrl, CompareUrl = res.CompareUrl, Items = res.Items.Select(i => i.Title).ToList(),
                    Files = files, Targets = res.Files.Where(f => !f.EndsWith(".meta", StringComparison.Ordinal)).ToList(),
                });
                _lastCheck = -1000;
                SaveSent(sent);
                if (res.Renamed.Count > 0) QaRunner.ReapplyKnowledge();
                var link = res.PrUrl ?? res.CompareUrl;
                var msg = res.PrUrl != null ? $"Đã tạo PR:\n{res.PrUrl}" : $"Đã đẩy nhánh {res.Branch}.\nMở trình duyệt để tạo PR.";
                if (res.Renamed.Count > 0) msg += "\n\nĐổi mã case (trùng mã đã có): " + string.Join(", ", res.Renamed.Select(x => $"{x.Key} → {x.Value}"));
                if (EditorUtility.DisplayDialog("Gửi lên kho chung", msg, link != null ? "Mở" : "OK", "Đóng") && link != null) Application.OpenURL(link);
            }
            Render();
        }

        // ------------------------------------------------------------------ phần tử nhỏ

        static Label Link(string text, string url)
        {
            var l = new Label(text) { tooltip = url };
            l.AddToClassList("link");
            l.RegisterCallback<ClickEvent>(_ => Application.OpenURL(url));
            return l;
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

        /// <summary>Mở 1 mục thu gọn được (bấm tiêu đề để thu lên / xổ xuống); nội dung thêm sau đó vào mục này. key != null → nhớ trạng thái trên máy.</summary>
        void Fold(string? key, string title, bool open)
        {
            var pref = "Titan.TrackingQA.Kb.Fold." + key;
            var f = new Foldout { text = title, value = key == null ? open : EditorPrefs.GetBool(pref, open) };
            f.AddToClassList("kb-fold");
            if (key != null) f.RegisterValueChangedCallback(e => { if (e.target == f) EditorPrefs.SetBool(pref, e.newValue); });
            _body.Add(f);
            _cur = f;
        }
    }
}
