#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace Titan.TrackingQA
{
    /// <summary>1 lời gọi tracking thô nghe được từ Titan (chưa kiểm). Editor đọc qua <see cref="RecordBus"/>.</summary>
    public sealed class RawTrackingCall
    {
        /// <summary>event / property / items.</summary>
        public string Kind = "event";
        public string Name = "";
        public List<KeyValuePair<string, object?>> Params = new List<KeyValuePair<string, object?>>();
        public List<List<KeyValuePair<string, object?>>> Items = new List<List<KeyValuePair<string, object?>>>();
        public float RealTime;
        public int Frame;
        public DateTime Time;
        /// <summary>Khung gọi (file đầy đủ, dòng, lớp, hàm) từ chỗ game gọi Titan trở ra.</summary>
        public List<(string File, int Line, string Type, string Method)> Stack = new List<(string, int, string, string)>();
        /// <summary>Xảy ra trong lúc xử lý 1 cú bấm nút / toggle UI (đường gọi đi qua UnityEngine.UI.Button / Toggle).</summary>
        public bool FromClick;
    }

    /// <summary>1 bước chơi: bấm nút / toggle UI, hoặc vào scene.</summary>
    public sealed class RawStep
    {
        /// <summary>click / scene / focus.</summary>
        public string Kind = "click";
        public string Name = "";
        /// <summary>Popup / màn chứa nút (vd OutOfSpacePanel).</summary>
        public string? Context;
        public string? Path;
        public float RealTime;
        public int Frame;
        public DateTime Time;
    }

    /// <summary>
    /// Kênh giữa phần nghe (chạy trong Play) và cửa sổ Record (Editor). Chỉ có trong Editor (asmdef defineConstraints UNITY_EDITOR)
    /// → không vào bản build. Bật / tắt bằng <see cref="Enabled"/> (Editor đặt trước khi vào Play).
    /// </summary>
    public static class RecordBus
    {
        public static bool Enabled = true;
        public static string Status = "";
        public static readonly List<RawTrackingCall> Calls = new List<RawTrackingCall>();
        /// <summary>Gọi trên main thread mỗi khi nghe được 1 lời gọi.</summary>
        public static event Action<RawTrackingCall>? Received;
        public static event Action? PlayStarted;
        public static readonly List<RawStep> Steps = new List<RawStep>();
        public static event Action<RawStep>? StepReceived;

        internal static void PushStep(RawStep s)
        {
            Steps.Add(s);
            try { StepReceived?.Invoke(s); } catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }

        internal static void Begin()
        {
            Calls.Clear();
            Steps.Clear();
            Status = "";
            PlayStarted?.Invoke();
        }

        internal static void Push(RawTrackingCall c)
        {
            Calls.Add(c);
            try { Received?.Invoke(c); } catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }

    /// <summary>
    /// Gắn vào Titan.Tracking.TrackingManager như 1 dịch vụ tracking (giống Firebase / AppsFlyer) ngay trước khi scene đầu chạy,
    /// qua reflection — game không dùng Titan thì chỉ ghi trạng thái, không lỗi. Không sửa code game hay Titan.
    /// </summary>
    static class TitanHook
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Hook()
        {
            if (!RecordBus.Enabled) return;
            RecordBus.Begin();
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Titan.Tracking.TrackingManager", false)).FirstOrDefault(t => t != null);
                if (type == null) { RecordBus.Status = "Không tìm thấy Titan.Tracking.TrackingManager — game không dùng package com.titan.tracking?"; return; }
                var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null) { RecordBus.Status = "Không lấy được TrackingManager.Instance"; return; }
                int n = 0;
                n += Register(type, instance, "RegisterTrackEventService", nameof(OnEvent)) ? 1 : 0;
                n += Register(type, instance, "RegisterTrackPropertyService", nameof(OnProperty)) ? 1 : 0;
                n += Register(type, instance, "RegisterTrackItemsService", nameof(OnItems)) ? 1 : 0;
                RecordBus.Status = n == 3 ? "Đang ghi" : $"Đang ghi (chỉ gắn được {n}/3 kênh tracking của Titan)";
                UiWatcher.Create();
            }
            catch (Exception e)
            {
                RecordBus.Status = "Không gắn được vào Titan: " + e.Message;
                UnityEngine.Debug.LogWarning("[Tracking QA] " + RecordBus.Status);
            }
        }

        /// <summary>Tạo delegate đúng kiểu Titan yêu cầu (vd Action&lt;string, ITrackingParam[]&gt;) trỏ về hàm nhận object của mình.</summary>
        static bool Register(Type type, object instance, string method, string handler)
        {
            var reg = type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
            if (reg == null) return false;
            var delType = reg.GetParameters()[0].ParameterType;
            var invoke = delType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var target = typeof(TitanHook).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static)!;
            var args = ps.Select((p, i) => (Expression)Expression.Convert(p, target.GetParameters()[i].ParameterType)).ToArray();
            var del = Expression.Lambda(delType, Expression.Call(target, args), ps).Compile();
            reg.Invoke(instance, new object[] { del });
            return true;
        }

        static void OnEvent(string name, object? parameters) =>
            RecordBus.Push(New("event", name, ReadParams(parameters)));

        static void OnProperty(string name, string value) =>
            RecordBus.Push(New("property", name, new List<KeyValuePair<string, object?>> { new KeyValuePair<string, object?>(name, value) }));

        static void OnItems(string name, object? parameters, object? items)
        {
            var c = New("items", name, ReadParams(parameters));
            if (items is Array arr)
                foreach (var it in arr) c.Items.Add(ReadParams(it));
            RecordBus.Push(c);
        }

        static RawTrackingCall New(string kind, string name, List<KeyValuePair<string, object?>> ps)
        {
            var c = new RawTrackingCall { Kind = kind, Name = name ?? "", Params = ps, RealTime = Time.realtimeSinceStartup, Frame = Time.frameCount, Time = DateTime.Now };
            // Bỏ các khung của chính Recorder + TrackingManager / dịch vụ Titan bên trong; giữ từ chỗ game gọi trở ra
            foreach (var f in new StackTrace(2, true).GetFrames() ?? Array.Empty<StackFrame>())
            {
                var file = f.GetFileName();
                var m = f.GetMethod();
                if (m?.DeclaringType?.FullName is "UnityEngine.UI.Button" or "UnityEngine.UI.Toggle") c.FromClick = true;
                if (string.IsNullOrEmpty(file) || m == null) continue;
                var t = m.DeclaringType;
                if (c.Stack.Count < 40) c.Stack.Add((file!, f.GetFileLineNumber(), t?.FullName ?? "", m.Name));
                if (c.Stack.Count >= 16 && c.FromClick) break;
                if (c.Stack.Count >= 40) break;
            }
            if (c.Stack.Count > 16) c.Stack.RemoveRange(16, c.Stack.Count - 16);
            return c;
        }

        /// <summary>ITrackingParam[] → (key, value) bằng reflection (key / value là property public của Titan.Tracking.TrackingParam&lt;T&gt;).</summary>
        static List<KeyValuePair<string, object?>> ReadParams(object? arr)
        {
            var res = new List<KeyValuePair<string, object?>>();
            if (arr is not Array a) return res;
            foreach (var p in a)
            {
                if (p == null) continue;
                var t = p.GetType();
                var key = t.GetProperty("key")?.GetValue(p) as string ?? "";
                // TrackingParam<T>.value (public, kiểu T) — lấy theo tên để không vướng property value của interface
                var value = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "value")?.GetValue(p);
                res.Add(new KeyValuePair<string, object?>(key, value));
            }
            return res;
        }
    }

    /// <summary>
    /// Ghi bước chơi: lúc Play, cứ ~0,3 giây tìm nút / toggle UI đang có (kể cả popup mới mở) và gắn thêm 1 người nghe vào sự kiện bấm;
    /// ghi cả vào scene. Chỉ gắn trong bộ nhớ khi Play (không sửa prefab / scene / code, dừng Play là mất). Dùng reflection để
    /// game không có uGUI vẫn biên dịch được.
    /// </summary>
    sealed class UiWatcher : MonoBehaviour
    {
        /// <summary>Tên GameObject chung chung, không nói được đang ở màn nào.</summary>
        static readonly System.Text.RegularExpressions.Regex GenericName = new System.Text.RegularExpressions.Regex(
            @"^(Panel|Canvas|Content|Root|Container|Bg|Background|Popup|Frame|Group|Holder|Layout|SafeArea|Safe ?Area|Main|UI|Image|Viewport|Scroll ?View|Body|Top|Bottom|Header|Footer|Center|Middle|Board|Grid|Buttons?)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Type? _button, _toggle;
        readonly HashSet<int> _hooked = new HashSet<int>();
        float _next;

        internal static void Create()
        {
            var go = new GameObject("[TrackingQA Record]") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<UiWatcher>();
        }

        void Awake()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                _button ??= a.GetType("UnityEngine.UI.Button", false);
                _toggle ??= a.GetType("UnityEngine.UI.Toggle", false);
            }
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnScene;
        }

        void OnDestroy() => UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnScene;

        // Rời / quay lại cửa sổ game (OnApplicationFocus / OnApplicationPause cùng lúc → chỉ ghi khi đổi trạng thái).
        // Game thường bắn level_exit… ở đây: trong Editor là do bấm ra ngoài cửa sổ Game, trên máy thật là app xuống nền.
        bool _away;
        void OnApplicationFocus(bool focus) => Focus(!focus, "OnApplicationFocus");
        void OnApplicationPause(bool pause) => Focus(pause, "OnApplicationPause");

        void Focus(bool away, string by)
        {
            if (away == _away || Time.frameCount < 2) return;
            _away = away;
            RecordBus.PushStep(new RawStep
            {
                Kind = "focus", Name = away ? "Rời cửa sổ game" : "Quay lại cửa sổ game", Context = by,
                RealTime = Time.realtimeSinceStartup, Frame = Time.frameCount, Time = DateTime.Now,
            });
        }

        void OnScene(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode mode) =>
            RecordBus.PushStep(new RawStep { Kind = "scene", Name = s.name, Context = mode == UnityEngine.SceneManagement.LoadSceneMode.Additive ? "thêm (additive)" : null,
                RealTime = Time.realtimeSinceStartup, Frame = Time.frameCount, Time = DateTime.Now });

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.3f;
            if (_button != null)
                foreach (var o in FindObjectsByType(_button, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (o is Component c && _hooked.Add(c.GetInstanceID()) && Member(c, "onClick") is UnityEngine.Events.UnityEvent ev)
                        ev.AddListener(() => Clicked(c, null));
            if (_toggle != null)
                foreach (var o in FindObjectsByType(_toggle, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (o is Component c && _hooked.Add(c.GetInstanceID()) && Member(c, "onValueChanged") is UnityEngine.Events.UnityEvent<bool> ev)
                        ev.AddListener(on => Clicked(c, on ? "bật" : "tắt"));
        }

        /// <summary>Tên script của game (không phải Unity / uGUI / TextMeshPro / package QA) gắn trên GameObject.</summary>
        static string? GameScript(Transform t)
        {
            foreach (var mb in t.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var type = mb.GetType();
                var asm = type.Assembly.GetName().Name ?? "";
                if (asm.StartsWith("UnityEngine") || asm.StartsWith("Unity.") || asm.StartsWith("TMPro") || asm.StartsWith("Titan.TrackingQA") || asm.StartsWith("DOTween")) continue;
                if (GenericName.IsMatch(type.Name)) continue;
                return type.Name;
            }
            return null;
        }

        static object? Member(Component c, string name)
        {
            var t = c.GetType();
            return t.GetProperty(name)?.GetValue(c) ?? t.GetField(name)?.GetValue(c);
        }

        static void Clicked(Component c, string? state)
        {
            if (c == null) return;
            var tr = c.transform;
            string? byScript = null, byName = null;
            var path = tr.name;
            for (var p = tr.parent; p != null; p = p.parent)
            {
                path = p.name + "/" + path;
                // Ưu tiên tên script của game gắn trên popup / màn (vd OutOfSpacePanel) — ổn định hơn tên GameObject
                byScript ??= GameScript(p);
                var clean = System.Text.RegularExpressions.Regex.Replace(p.name, @"\s*\((Clone|\d+)\)", "").Trim();
                if (byName == null && clean.Length > 0 && !GenericName.IsMatch(clean)) byName = clean;
            }
            var context = byScript ?? byName ?? (tr.root != tr ? tr.root.name : null);
            RecordBus.PushStep(new RawStep
            {
                Kind = "click", Name = tr.name + (state != null ? $" → {state}" : ""), Context = context, Path = path,
                RealTime = Time.realtimeSinceStartup, Frame = Time.frameCount, Time = DateTime.Now,
            });
        }
    }
}
#endif
