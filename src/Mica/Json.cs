using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace Mica
{
    /// <summary>Tiny JSON helpers on top of the serializer built into .NET Framework.</summary>
    static class J
    {
        public static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static string Ser(object o) => S.Serialize(o);

        public static Dictionary<string, object> Obj(string json)
        {
            try { return S.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        public static string Str(this Dictionary<string, object> d, string k, string def = null)
            => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;

        public static double Num(this Dictionary<string, object> d, string k, double def = 0)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static int Int(this Dictionary<string, object> d, string k, int def = 0) => (int)Math.Round(d.Num(k, def));

        public static bool Bool(this Dictionary<string, object> d, string k, bool def = false)
            => d != null && d.TryGetValue(k, out var v) && v is bool b ? b : def;

        public static Dictionary<string, object> Sub(this Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
