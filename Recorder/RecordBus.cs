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

        internal static void Begin()
        {
            Calls.Clear();
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
                if (string.IsNullOrEmpty(file) || m == null) continue;
                var t = m.DeclaringType;
                c.Stack.Add((file!, f.GetFileLineNumber(), t?.FullName ?? "", m.Name));
                if (c.Stack.Count >= 16) break;
            }
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
}
#endif
