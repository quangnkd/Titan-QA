#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TrackingChecker.Core;
using TrackingChecker.Core.Live;
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
        Label _status = null!;
        bool _busy;
        string? _lastCleaned;

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

        static List<Sent> LoadSent()
        {
            try { return File.Exists(SentPath) ? JsonSerializer.Deserialize<List<Sent>>(File.ReadAllText(SentPath)) ?? new List<Sent>() : new List<Sent>(); }
            catch { return new List<Sent>(); }
        }

        void Render()
        {
            if (_body == null) return;
            _body.Clear();
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
            if (_lastCleaned != null) _body.Add(Para(_lastCleaned, "detail-text"));

            // --- Chờ gửi
            List<KnowledgeShare.Item> items;
            try { items = KnowledgeShare.Plan(game); }
            catch (Exception e) { items = new List<KnowledgeShare.Item>(); _body.Add(Para("Không đọc được chỉnh sửa trên máy: " + e.Message, "detail-text")); }
            var sentAll = LoadSent();
            Dictionary<string, string> fp;
            try { fp = KnowledgeShare.Fingerprint(game); } catch { fp = new Dictionary<string, string>(); }
            var unsent = items.Count(i => !fp.TryGetValue(i.Rel, out var h) || SentFor(i.Rel, h, sentAll) == null);
            _body.Add(Section($"Chỉnh sửa trên máy ({items.Count}) — {unsent} chưa gửi, {items.Count - unsent} đã gửi"));
            if (items.Count == 0) _body.Add(Muted("Không có chỉnh sửa nào trên máy. Ngoại lệ (Đúng thiết kế / Check all báo nhầm), case G-xxx, chỉnh doc lưu trên máy sẽ hiện ở đây."));
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
                _body.Add(box);
            }

            var url = RepoUrl();
            var urlRow = Row("ev");
            urlRow.style.flexWrap = Wrap.Wrap;
            urlRow.Add(new Label("Kho chung: "));
            var urlField = new TextField { value = EditorPrefs.GetString(PrefUrl, ""), tooltip = "Để trống = dùng nguồn cài package (git URL trong manifest)" };
            urlField.style.minWidth = 260;
            urlField.style.flexGrow = 1;
            urlField.RegisterValueChangedCallback(e => EditorPrefs.SetString(PrefUrl, e.newValue.Trim()));
            urlRow.Add(urlField);
            _body.Add(urlRow);
            _body.Add(Muted(url != null ? "Gửi tới: " + KnowledgePublisher.Mask(url) : "Không biết kho chung ở đâu (package không cài từ git) — nhập URL git của repo package."));

            var btns = Row("accept-btns");
            var send = new Button(() => Publish(url!))
            {
                text = _busy ? "Đang gửi…" : unsent > 0 ? $"Gửi lên kho chung (tạo PR) · {unsent} chưa gửi" : "Không có gì mới để gửi",
                tooltip = "Gửi mọi chỉnh sửa trên máy chưa có trong kho chung (cả mục đã gửi mà chưa merge — PR mới thay PR cũ)",
            };
            send.AddToClassList("primary");
            send.SetEnabled(!_busy && unsent > 0 && url != null);
            btns.Add(send);
            var check = new Button(() => CheckStatus(true)) { text = _checking ? "Đang kiểm…" : "Kiểm tra trạng thái", tooltip = "Xem trên GitHub: đã tạo PR chưa, đã merge chưa" };
            check.SetEnabled(!_checking && url != null && sentAll.Count > 0);
            btns.Add(check);
            _body.Add(btns);
            if (_checkError != null) _body.Add(Muted("Không kiểm được trạng thái: " + _checkError));
            _body.Add(Muted("Tạo nhánh mới trong repo package, ghi các chỉnh sửa trên, push và tạo Pull Request cho người duyệt merge. " +
                            "Dùng quyền git trên máy này. Chưa merge thì bản trên máy vẫn giữ và vẫn có hiệu lực trên máy này."));

            // --- Đã gửi
            var sent = LoadSent();
            if (sent.Count > 0)
            {
                _body.Add(Section($"Đã gửi ({sent.Count})"));
                foreach (var s in sent.OrderByDescending(x => x.At).Take(10))
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
                    _body.Add(row);
                }
            }

            // --- Học từ chỗ Check all sai
            _body.Add(Section("Học từ chỗ Check all sai"));
            try
            {
                var games = Knowledge.KnownGames();
                if (game != null && !games.Contains(game)) games.Add(game);
                var stats = Learning.Collect(games);
                if (stats.Count == 0) _body.Add(Muted("Chưa có mục nào đánh “Check all báo nhầm” / “Đúng thiết kế”."));
                foreach (var s in stats)
                    _body.Add(Para($"<b>{s.Rule}</b> — báo nhầm {s.FalsePositives}" +
                                   (s.Causes.Count > 0 ? " (" + string.Join(", ", s.Causes.Select(c => $"{GameData.CauseLabel(c.Key)} ×{c.Value}")) + ")" : "") +
                                   (s.FpGames.Count > 0 ? $" ở {string.Join(", ", s.FpGames)}" : "") + $" · đúng thiết kế {s.ByDesign}"));
                foreach (var sug in Learning.Suggestions(stats)) { var p = Para("→ " + sug, "sc-check"); p.AddToClassList("warn"); _body.Add(p); }
            }
            catch (Exception e) { _body.Add(Muted("Không tổng hợp được: " + e.Message)); }

            var fb = RecordFeedback.Load(RecordController.FeedbackPath);
            // --- Đề xuất nâng case riêng game thành case chung
            try
            {
                var games = Knowledge.KnownGames();
                if (game != null && !games.Contains(game)) games.Add(game);
                var promos = Learning.CasePromotions(games);
                _body.Add(Section($"Đề xuất nâng thành case chung ({promos.Count(p => p.CoveredBy.Count == 0)})"));
                if (promos.Count == 0) _body.Add(Muted("Chưa có case riêng game (G-xxx) nào giống nhau ở từ 2 game. Khi QA ở nhiều game lưu case giống nhau, đề xuất sẽ hiện ở đây."));
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
                    _body.Add(box);
                }
            }
            catch (Exception e) { _body.Add(Muted("Không tổng hợp được đề xuất: " + e.Message)); }

            var diffs = fb.Scenarios.Values.Where(x => x.Diffs > 0).OrderByDescending(x => x.Diffs).ToList();
            if (diffs.Count > 0)
            {
                _body.Add(Para($"<b>Record lệch log giả lập</b> ({diffs.Count} thao tác) — chỗ Check all lần luồng khác thực tế:"));
                foreach (var d in diffs.Take(10))
                {
                    _body.Add(Para($"• {d.Trigger}: lệch {d.Diffs}/{d.Plays} lần"));
                    foreach (var n in d.LastDiff.Take(3)) _body.Add(Muted("    " + n));
                }
            }
            _status.text = _busy ? "Đang gửi lên kho chung…" : "";
            _status.style.display = _busy ? DisplayStyle.Flex : DisplayStyle.None;
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
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SentPath)!);
                    File.WriteAllText(SentPath, JsonSerializer.Serialize(sent, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
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

        static Label Section(string text)
        {
            var l = new Label(text);
            l.AddToClassList("section");
            return l;
        }
    }
}
