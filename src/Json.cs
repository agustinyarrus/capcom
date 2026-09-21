using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Capcom
{
    /// <summary>JSON legible a mano: lectura con JavaScriptSerializer, escritura propia con sangría.</summary>
    internal static class Json
    {
        public static Dictionary<string, object> LeerObjeto(string ruta)
        {
            try
            {
                if (!File.Exists(ruta)) return null;
                return Parsear(File.ReadAllText(ruta, Encoding.UTF8));
            }
            catch { return null; }
        }

        public static Dictionary<string, object> Parsear(string texto)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(texto)) return null;
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
                return js.Deserialize<Dictionary<string, object>>(texto);
            }
            catch { return null; }
        }

        /// <summary>
        /// Parsea un JSON cuyo nivel de arriba es un ARREGLO (la API de HuggingFace devuelve así el árbol de
        /// archivos). `Parsear` sólo sirve para objetos: con un arreglo devuelve null sin decir por qué.
        /// </summary>
        public static List<Dictionary<string, object>> ParsearLista(string texto)
        {
            var res = new List<Dictionary<string, object>>();
            try
            {
                if (string.IsNullOrWhiteSpace(texto)) return res;
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
                var o = js.DeserializeObject(texto);
                if (o is object[] arr)
                    foreach (var x in arr) if (x is Dictionary<string, object> d) res.Add(d);
            }
            catch { }
            return res;
        }

        public static void Escribir(string ruta, object valor)
        {
            var sb = new StringBuilder();
            Serializar(sb, valor, 0);
            sb.AppendLine();
            var dir = Path.GetDirectoryName(ruta);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = ruta + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(ruta)) File.Replace(tmp, ruta, null); else File.Move(tmp, ruta);
        }

        public static string Texto(object valor) { var sb = new StringBuilder(); Serializar(sb, valor, 0); return sb.ToString(); }

        /// <summary>Escapa una cadena como literal JSON, con las comillas puestas.</summary>
        public static string Cita(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x2028 || c == 0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static void Serializar(StringBuilder sb, object v, int nivel)
        {
            string sangria = new string(' ', nivel * 2), sangria2 = new string(' ', (nivel + 1) * 2);
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { sb.Append(Cita(s)); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is int i) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is long l) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double d) { sb.Append(d.ToString("0.#####", CultureInfo.InvariantCulture)); return; }
            if (v is float f) { sb.Append(f.ToString("0.#####", CultureInfo.InvariantCulture)); return; }
            if (v is decimal m) { sb.Append(m.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is DateTime dt) { sb.Append(Cita(dt.ToString("yyyy-MM-dd HH:mm:ss"))); return; }
            if (v is Enum e) { sb.Append(Cita(e.ToString())); return; }
            if (v is IDictionary dic)
            {
                if (dic.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int k = 0;
                foreach (DictionaryEntry de in dic)
                {
                    sb.Append(sangria2).Append(Cita(de.Key.ToString())).Append(": ");
                    Serializar(sb, de.Value, nivel + 1);
                    sb.Append(++k < dic.Count ? ",\n" : "\n");
                }
                sb.Append(sangria).Append('}');
                return;
            }
            if (v is IEnumerable en)
            {
                var items = en.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                bool simples = items.All(x => x == null || x is string || x is bool || x is int || x is long || x is double);
                if (simples && items.Count <= 14)
                {
                    sb.Append('[');
                    for (int j = 0; j < items.Count; j++) { if (j > 0) sb.Append(", "); Serializar(sb, items[j], nivel + 1); }
                    sb.Append(']');
                    return;
                }
                sb.Append("[\n");
                for (int j = 0; j < items.Count; j++)
                {
                    sb.Append(sangria2);
                    Serializar(sb, items[j], nivel + 1);
                    sb.Append(j < items.Count - 1 ? ",\n" : "\n");
                }
                sb.Append(sangria).Append(']');
                return;
            }
            // objeto plano: campos y propiedades públicas
            var t = v.GetType();
            var dd = new Dictionary<string, object>();
            foreach (var fi in t.GetFields()) if (!fi.IsStatic && !Attribute.IsDefined(fi, typeof(ScriptIgnoreAttribute))) dd[Camel(fi.Name)] = fi.GetValue(v);
            foreach (var pi in t.GetProperties()) if (pi.CanRead && pi.GetIndexParameters().Length == 0 && !Attribute.IsDefined(pi, typeof(ScriptIgnoreAttribute))) dd[Camel(pi.Name)] = pi.GetValue(v, null);
            Serializar(sb, dd, nivel);
        }

        static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        // --- lectura tolerante -------------------------------------------------
        public static string S(Dictionary<string, object> d, string k, string def = "") => d != null && d.TryGetValue(k, out var v) && v != null ? v.ToString() : def;
        public static int I(Dictionary<string, object> d, string k, int def = 0)
        { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : def; } catch { return def; } }
        public static double D(Dictionary<string, object> d, string k, double def = 0)
        { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def; } catch { return def; } }
        public static bool B(Dictionary<string, object> d, string k, bool def = false)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return def;
            if (v is bool b) return b;
            var s = v.ToString().Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "si" || s == "sí";
        }
        public static DateTime? F(Dictionary<string, object> d, string k)
        {
            var s = S(d, k, "");
            if (s.Length == 0) return null;
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            return null;
        }
        public static List<Dictionary<string, object>> Lista(Dictionary<string, object> d, string k)
        {
            var res = new List<Dictionary<string, object>>();
            if (d == null || !d.TryGetValue(k, out var v) || !(v is IEnumerable en) || v is string) return res;
            foreach (var o in en) if (o is Dictionary<string, object> od) res.Add(od);
            return res;
        }
        public static string[] Cadenas(Dictionary<string, object> d, string k)
        {
            var res = new List<string>();
            if (d == null || !d.TryGetValue(k, out var v) || !(v is IEnumerable en) || v is string) return res.ToArray();
            foreach (var o in en) if (o != null) res.Add(o.ToString());
            return res.ToArray();
        }
    }
}
