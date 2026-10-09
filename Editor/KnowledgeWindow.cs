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
        }

        VisualElement _body = null!;
        Label _status = null!;
        bool _busy;
        string? _lastCleaned;

        [MenuItem("Titan/QAUTO/Tracking QA Kho chung", false, 3)]
        public static void Open() => GetWindow<KnowledgeWindow>("Tracking QA · Kho chung").minSize = new Vector2(560, 360);

        /// <summary>Số chỉnh sửa trên máy chờ gửi của game đang mở (hiện trên nút ở cửa sổ CheckAll).</summary>
        public static int PendingCount()
        {
            try { return Knowledge.Pending(QaRunner.GameId).Count; } catch { return 0; }
        }

        void OnFocus() => Render();

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
            _body.Add(Section($"Chờ gửi lên kho chung ({items.Count})"));
            if (items.Count == 0) _body.Add(Muted("Không có chỉnh sửa nào trên máy. Ngoại lệ (Đúng thiết kế / Check all báo nhầm), case G-xxx, chỉnh doc lưu trên máy sẽ hiện ở đây."));
            foreach (var it in items)
            {
                var box = new VisualElement();
                box.AddToClassList("trace");
                box.Add(Para($"<b>{it.Title}</b>  <i>Knowledge/{it.Rel}</i>"));
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

            var send = new Button(() => Publish(url!)) { text = _busy ? "Đang gửi…" : $"Gửi lên kho chung (tạo PR) · {items.Count}" };
            send.AddToClassList("primary");
            send.SetEnabled(!_busy && items.Count > 0 && url != null);
            _body.Add(send);
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
                    row.Add(new Label($"{s.At:dd/MM HH:mm} · {s.Branch} · {string.Join(", ", s.Items.Take(3))}{(s.Items.Count > 3 ? "…" : "")}  "));
                    var link = s.PrUrl ?? s.CompareUrl;
                    if (link != null)
                    {
                        var l = new Label(s.PrUrl != null ? "Mở PR" : "Tạo PR") { tooltip = link };
                        l.AddToClassList("link");
                        l.RegisterCallback<ClickEvent>(_ => Application.OpenURL(link));
                        row.Add(l);
                    }
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
                learning = Learning.Markdown(Learning.Collect(games));
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
                sent.Add(new Sent { At = DateTime.Now, Branch = res.Branch, PrUrl = res.PrUrl, CompareUrl = res.CompareUrl, Items = res.Items.Select(i => i.Title).ToList() });
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
