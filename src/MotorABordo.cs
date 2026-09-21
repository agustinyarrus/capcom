using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Capcom
{
    /// <summary>
    /// El motor de a bordo: lo que CAPCOM puede contestar SIN el modelo, con la nave a oscuras.
    ///
    /// No pretende ser una inteligencia: es un panel de instrumentos. Hace cuentas de verdad (parser propio de
    /// expresiones), sabe la hora, convierte unidades, codifica, sortea y se sabe el estado de la máquina. Todo lo
    /// demás lo dice en la cara: «esto no lo sé sin el modelo». Una maqueta honesta es infinitamente más útil que
    /// una que simula entender.
    /// </summary>
    internal static class MotorABordo
    {
        public sealed class Contexto
        {
            public EstadoServidor Servidor = new EstadoServidor();
            public DateTime Arranque = DateTime.Now;
            public int Transmisiones, MensajesEnHilo;
            public string Persona = "";
            public string Modelo = "";
        }

        /// <summary>Devuelve la respuesta, o null si no tiene nada honesto para decir.</summary>
        public static string Responder(string entrada, Contexto ctx)
        {
            string t = (entrada ?? "").Trim();
            if (t.Length == 0) return null;
            string b = Normalizar(t);

            foreach (var f in Reglas)
            {
                var r = f(t, b, ctx);
                if (r != null) return r;
            }
            return null;
        }

        delegate string Regla(string crudo, string normal, Contexto ctx);

        static readonly Regla[] Reglas =
        {
            Ayuda, Estado, Hora, Fecha, Faltan, Conversion, Porcentaje, Cuenta, Sorteo, Claves,
            Codificar, Contar, Saludo, Gracias,
        };

        // ------------------------------------------------------------------ reglas

        static string Ayuda(string crudo, string b, Contexto c)
        {
            if (!Contiene(b, "que podes hacer", "que sabes hacer", "que haces", "ayuda", "help", "comandos", "capacidades")) return null;
            bool vivo = c.Servidor.Señal == Señal.Nominal;
            return
"## Motor de a bordo\n\n" +
"Con el modelo apagado contesto **solo lo que puedo verificar**. Nada de inventar.\n\n" +
"| puedo | ejemplo |\n" +
"| --- | --- |\n" +
"| cuentas de verdad | `(2^10 - 24) / 8` |\n" +
"| porcentajes | `15% de 240` |\n" +
"| hora y fecha | `qué hora es` · `qué día es hoy` |\n" +
"| cuenta regresiva | `cuántos días faltan para 25/12` |\n" +
"| unidades | `20 C a F` · `10 km a millas` · `4.5 GB a MB` |\n" +
"| sorteos | `tirá un dado` · `moneda` · `elegí entre café, mate, nada` |\n" +
"| claves | `uuid` · `contraseña de 20` |\n" +
"| codificar | `base64 de hola` · `hex de 255` |\n" +
"| contar | `contá las palabras de: ...` |\n" +
"| estado | `estado` · `cuánta RAM hay` |\n\n" +
(vivo
 ? "El modelo **" + c.Servidor.ModeloCorto + "** está en línea: preguntame cualquier otra cosa y contesta él.\n"
 : "> [!IMPORTANT]\n> El modelo está apagado. Para todo lo demás, levantalo desde la pestaña **MODELOS** y volvé a preguntar.\n");
        }

        static string Estado(string crudo, string b, Contexto c)
        {
            if (!Contiene(b, "estado", "status", "cuanta ram", "memoria libre", "como estas de memoria", "diagnostico")) return null;
            double libre, total;
            Win32.Memoria(out libre, out total);
            var up = DateTime.Now - c.Arranque;
            string señal = c.Servidor.Señal == Señal.Nominal ? "NOMINAL" : c.Servidor.Señal == Señal.Cargando ? "CARGANDO" : "SIN SEÑAL";
            var sb = new StringBuilder();
            sb.Append("## Estado de la nave\n\n");
            sb.Append("| sistema | lectura |\n| --- | --- |\n");
            sb.Append("| enlace | **").Append(señal).Append("** · ").Append(c.Servidor.Detalle).Append(" |\n");
            if (c.Servidor.ModeloCorto.Length > 0) sb.Append("| modelo | `").Append(c.Servidor.ModeloCorto).Append("` |\n");
            if (c.Servidor.Contexto > 0) sb.Append("| ventana | ").Append(c.Servidor.Contexto.ToString("N0")).Append(" tokens |\n");
            sb.Append("| memoria | ").Append(libre.ToString("0.0")).Append(" GB libres de ").Append(total.ToString("0.0")).Append(" GB |\n");
            sb.Append("| tiempo de misión | ").Append(Tema.TMas(up)).Append(" |\n");
            sb.Append("| transmisiones | ").Append(c.Transmisiones).Append(" en el archivo |\n");
            sb.Append("| persona | ").Append(c.Persona.Length > 0 ? c.Persona : "—").Append(" |\n");
            return sb.ToString();
        }

        static string Hora(string crudo, string b, Contexto c)
        {
            if (!Contiene(b, "que hora es", "la hora", "hora actual", "que horas son")) return null;
            var n = DateTime.Now;
            return "Son las **" + n.ToString("HH:mm:ss") + "**, " + Dia(n.DayOfWeek) + " " + n.Day + " de " + Mes(n.Month) + ".\n\n" +
                   "`UTC " + n.ToUniversalTime().ToString("HH:mm") + "` · `" + TimeZoneInfo.Local.StandardName + "`";
        }

        static string Fecha(string crudo, string b, Contexto c)
        {
            if (!Contiene(b, "que dia es", "que fecha", "fecha de hoy", "hoy que dia", "en que dia estamos")) return null;
            var n = DateTime.Now;
            var cal = CultureInfo.InvariantCulture.Calendar;
            int semana = cal.GetWeekOfYear(n, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            int diaDelAño = n.DayOfYear;
            bool bisiesto = DateTime.IsLeapYear(n.Year);
            int quedan = (bisiesto ? 366 : 365) - diaDelAño;
            return "Hoy es **" + Dia(n.DayOfWeek) + " " + n.Day + " de " + Mes(n.Month) + " de " + n.Year + "**.\n\n" +
                   "- día " + diaDelAño + " del año, quedan " + quedan + "\n" +
                   "- semana " + semana + "\n" +
                   "- " + (bisiesto ? "año bisiesto" : "año común");
        }

        static readonly Regex ReFaltan = new Regex(@"(?:falta|faltan|quedan|cuanto falta).*?(?:para|hasta)\s+(?:el\s+)?(\d{1,2})[/\-.](\d{1,2})(?:[/\-.](\d{2,4}))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static string Faltan(string crudo, string b, Contexto c)
        {
            var m = ReFaltan.Match(b);
            if (!m.Success) return null;
            int d = int.Parse(m.Groups[1].Value), mes = int.Parse(m.Groups[2].Value);
            int año = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : DateTime.Now.Year;
            if (año < 100) año += 2000;
            DateTime objetivo;
            try { objetivo = new DateTime(año, mes, d); } catch { return "Esa fecha no existe."; }
            if (!m.Groups[3].Success && objetivo.Date < DateTime.Today) objetivo = objetivo.AddYears(1);
            var dif = objetivo.Date - DateTime.Today;
            int dias = (int)dif.TotalDays;
            string cuantos = dias == 0 ? "**es hoy**" : dias == 1 ? "falta **1 día**" : dias > 0 ? "faltan **" + dias + " días**" : "pasaron **" + (-dias) + " días**";
            int habiles = Habiles(DateTime.Today, objetivo.Date);
            return "Para el " + objetivo.ToString("dd/MM/yyyy") + " (" + Dia(objetivo.DayOfWeek) + ") " + cuantos + ".\n\n" +
                   "- " + habiles + " días hábiles\n- " + (dias / 7) + " semanas y " + Math.Abs(dias % 7) + " días";
        }

        static int Habiles(DateTime a, DateTime b)
        {
            if (b < a) { var t = a; a = b; b = t; }
            int n = 0;
            for (var d = a; d < b; d = d.AddDays(1)) if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) n++;
            return n;
        }

        static readonly Regex ReConv = new Regex(@"^\s*(-?[\d.,]+)\s*([a-zA-Zº°]+)\s*(?:a|en|to|->)\s*([a-zA-Zº°]+)\s*\??\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static string Conversion(string crudo, string b, Contexto c)
        {
            var m = ReConv.Match(crudo.Trim());
            if (!m.Success) return null;
            double v;
            if (!Numero(m.Groups[1].Value, out v)) return null;
            string de = Unidad(m.Groups[2].Value), a = Unidad(m.Groups[3].Value);
            double r;
            string nota = "";
            if (de == "c" && a == "f") { r = v * 9 / 5 + 32; nota = "°C → °F"; }
            else if (de == "f" && a == "c") { r = (v - 32) * 5 / 9; nota = "°F → °C"; }
            else if (de == "c" && a == "k") { r = v + 273.15; nota = "°C → K"; }
            else if (de == "k" && a == "c") { r = v - 273.15; nota = "K → °C"; }
            else
            {
                double fd, fa;
                string dimD, dimA;
                if (!Factor(de, out fd, out dimD) || !Factor(a, out fa, out dimA)) return null;
                if (dimD != dimA) return "No se puede: **" + de + "** mide " + dimD + " y **" + a + "** mide " + dimA + ".";
                r = v * fd / fa;
                nota = dimD;
            }
            return "**" + Bonito(v) + " " + m.Groups[2].Value + "** = **" + Bonito(r) + " " + m.Groups[3].Value + "**" +
                   (nota.Length > 0 ? "\n\n`" + nota + "`" : "");
        }

        static string Unidad(string u)
        {
            u = u.ToLowerInvariant().Trim('º', '°', '.');
            switch (u)
            {
                case "celsius": case "centigrados": return "c";
                case "fahrenheit": return "f";
                case "kelvin": return "k";
                case "kilometros": case "kilometro": case "kms": return "km";
                case "metros": case "metro": return "m";
                case "centimetros": return "cm";
                case "milimetros": return "mm";
                case "millas": case "milla": case "mi": return "mi";
                case "pies": case "pie": case "ft": return "ft";
                case "pulgadas": case "pulgada": case "in": return "in";
                case "kilos": case "kilo": case "kilogramos": return "kg";
                case "gramos": case "gramo": return "g";
                case "libras": case "libra": case "lb": case "lbs": return "lb";
                case "onzas": case "oz": return "oz";
                case "toneladas": case "tonelada": return "t";
                case "segundos": case "seg": case "s": return "s";
                case "minutos": case "min": return "min";
                case "horas": case "hora": case "h": return "h";
                case "dias": case "dia": case "d": return "d";
                case "bytes": case "byte": return "b";
                default: return u;
            }
        }

        /// <summary>Factor a la unidad base de cada dimensión (m, kg, s, byte).</summary>
        static bool Factor(string u, out double f, out string dim)
        {
            dim = ""; f = 1;
            switch (u)
            {
                case "km": f = 1000; dim = "longitud"; return true;
                case "m": f = 1; dim = "longitud"; return true;
                case "cm": f = 0.01; dim = "longitud"; return true;
                case "mm": f = 0.001; dim = "longitud"; return true;
                case "mi": f = 1609.344; dim = "longitud"; return true;
                case "ft": f = 0.3048; dim = "longitud"; return true;
                case "in": f = 0.0254; dim = "longitud"; return true;
                case "nmi": f = 1852; dim = "longitud"; return true;
                case "kg": f = 1; dim = "masa"; return true;
                case "g": f = 0.001; dim = "masa"; return true;
                case "lb": f = 0.45359237; dim = "masa"; return true;
                case "oz": f = 0.028349523; dim = "masa"; return true;
                case "t": f = 1000; dim = "masa"; return true;
                case "s": f = 1; dim = "tiempo"; return true;
                case "min": f = 60; dim = "tiempo"; return true;
                case "h": f = 3600; dim = "tiempo"; return true;
                case "d": f = 86400; dim = "tiempo"; return true;
                case "b": f = 1; dim = "datos"; return true;
                case "kb": f = 1024; dim = "datos"; return true;
                case "mb": f = 1048576; dim = "datos"; return true;
                case "gb": f = 1073741824; dim = "datos"; return true;
                case "tb": f = 1099511627776; dim = "datos"; return true;
                default: return false;
            }
        }

        static readonly Regex RePorc = new Regex(@"^\s*(-?[\d.,]+)\s*%\s*(?:de|of)\s*(-?[\d.,]+)\s*\??\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static string Porcentaje(string crudo, string b, Contexto c)
        {
            var m = RePorc.Match(crudo);
            if (!m.Success) return null;
            double p, v;
            if (!Numero(m.Groups[1].Value, out p) || !Numero(m.Groups[2].Value, out v)) return null;
            double r = v * p / 100;
            return "**" + Bonito(r) + "**\n\n`" + Bonito(p) + "% de " + Bonito(v) + "` · el resto es `" + Bonito(v - r) + "`";
        }

        static readonly Regex ReExpr = new Regex(@"^[\s\d.,+\-*/%^()a-záéíóúñ]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static string Cuenta(string crudo, string b, Contexto c)
        {
            string t = crudo.Trim().TrimEnd('?', '=', '.');
            t = Regex.Replace(t, @"^(cuanto es|cuánto es|calcula|calculá|cuenta|contá|resultado de)\s+", "", RegexOptions.IgnoreCase).Trim();
            if (t.Length == 0 || t.Length > 120) return null;
            if (!ReExpr.IsMatch(t)) return null;
            if (!t.Any(char.IsDigit)) return null;
            // que tenga al menos un operador: si no, "2026" contestaría "2026" y quedaría ridículo
            if (!t.Any(ch => "+-*/%^".IndexOf(ch) >= 0) && t.IndexOf("raiz", StringComparison.OrdinalIgnoreCase) < 0) return null;
            double r;
            string err;
            if (!Expresion.Evaluar(t, out r, out err)) return null;
            string exacto = r.ToString("0.##########", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append("**").Append(Bonito(r)).Append("**\n\n");
            sb.Append("`").Append(t).Append(" = ").Append(exacto).Append("`");
            if (Math.Abs(r) >= 1024 && Math.Abs(r) < 1e15 && Math.Abs(r % 1) < 1e-9)
                sb.Append("\n\n`hex 0x").Append(((long)r).ToString("X")).Append("` · `bin ").Append(Convert.ToString((long)r, 2)).Append('`');
            return sb.ToString();
        }

        static readonly Random rnd = new Random();
        static string Sorteo(string crudo, string b, Contexto c)
        {
            if (Contiene(b, "tira un dado", "tirá un dado", "un dado", "dado de", "tirar dado"))
            {
                var m = Regex.Match(b, @"d\s*(\d{1,3})|de\s+(\d{1,3})\s*caras");
                int caras = 6;
                if (m.Success) int.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out caras);
                if (caras < 2) caras = 6;
                return "**" + rnd.Next(1, caras + 1) + "**\n\n`d" + caras + "`";
            }
            if (Contiene(b, "moneda", "cara o ceca", "cara o cruz"))
                return "**" + (rnd.Next(2) == 0 ? "CARA" : "CECA") + "**";
            var e = Regex.Match(crudo, @"(?:elegi|elegí|elige|sortea|sorteá)\s+(?:entre\s+)?(.+)", RegexOptions.IgnoreCase);
            if (e.Success)
            {
                var ops = e.Groups[1].Value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (ops.Count >= 2) return "**" + ops[rnd.Next(ops.Count)] + "**\n\n`sorteado entre " + ops.Count + " opciones`";
            }
            return null;
        }

        static string Claves(string crudo, string b, Contexto c)
        {
            if (Contiene(b, "uuid", "guid"))
                return "`" + Guid.NewGuid().ToString() + "`\n\n`" + Guid.NewGuid().ToString("N") + "`";
            var m = Regex.Match(b, @"(?:contrasena|password|clave|pass)\s*(?:de\s*)?(\d{1,3})?");
            if (m.Success && Contiene(b, "contrasena", "password", "clave segura", "pass"))
            {
                int n = 20;
                if (m.Groups[1].Success) int.TryParse(m.Groups[1].Value, out n);
                n = Math.Max(8, Math.Min(96, n));
                const string abc = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*?-_=+";
                var sb = new StringBuilder();
                var bytes = new byte[n * 2];
                using (var g = System.Security.Cryptography.RandomNumberGenerator.Create()) g.GetBytes(bytes);
                for (int i = 0; i < n; i++) sb.Append(abc[bytes[i] % abc.Length]);
                return "`" + sb + "`\n\n" + n + " caracteres · generada con el RNG criptográfico del sistema, no con `Random`";
            }
            return null;
        }

        static string Codificar(string crudo, string b, Contexto c)
        {
            var m = Regex.Match(crudo, @"^\s*(base64|b64|hex|url|md5|sha1|sha256)\s*(?:de|of|:)\s*(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string que = m.Groups[1].Value.ToLowerInvariant(), val = m.Groups[2].Value.Trim().Trim('"');
            var bytes = Encoding.UTF8.GetBytes(val);
            switch (que)
            {
                case "base64": case "b64": return "`" + Convert.ToBase64String(bytes) + "`\n\n`" + val.Length + " caracteres → " + Convert.ToBase64String(bytes).Length + "`";
                case "hex":
                    {
                        double n;
                        if (Numero(val, out n) && Math.Abs(n % 1) < 1e-9 && Math.Abs(n) < 1e15)
                            return "`0x" + ((long)n).ToString("X") + "`\n\n`bin " + Convert.ToString((long)n, 2) + "` · `oct " + Convert.ToString((long)n, 8) + "`";
                        return "`" + BitConverter.ToString(bytes).Replace("-", " ") + "`";
                    }
                case "url": return "`" + Uri.EscapeDataString(val) + "`";
                case "md5": using (var h = System.Security.Cryptography.MD5.Create()) return "`" + Hex(h.ComputeHash(bytes)) + "`";
                case "sha1": using (var h = System.Security.Cryptography.SHA1.Create()) return "`" + Hex(h.ComputeHash(bytes)) + "`";
                default: using (var h = System.Security.Cryptography.SHA256.Create()) return "`" + Hex(h.ComputeHash(bytes)) + "`";
            }
        }

        static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        static string Contar(string crudo, string b, Contexto c)
        {
            var m = Regex.Match(crudo, @"^\s*(?:conta|contá|cuenta|cuantas|cuántas)\s+(?:las\s+)?(palabras|caracteres|letras|lineas|líneas)\s*(?:de|en)?\s*:?\s*(.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            string txt = m.Groups[2].Value;
            int palabras = txt.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
            int lineas = txt.Split('\n').Length;
            int sinEspacios = txt.Count(ch => !char.IsWhiteSpace(ch));
            return "| medida | valor |\n| --- | --- |\n" +
                   "| palabras | **" + palabras + "** |\n" +
                   "| caracteres | **" + txt.Length + "** |\n" +
                   "| sin espacios | " + sinEspacios + " |\n" +
                   "| líneas | " + lineas + " |\n" +
                   "| tokens (estimado) | ~" + Cliente.Estimar(txt) + " |";
        }

        static string Saludo(string crudo, string b, Contexto c)
        {
            if (!EsSolo(b, "hola", "buenas", "buen dia", "buenas tardes", "buenas noches", "que tal", "hey", "holis", "ey")) return null;
            bool vivo = c.Servidor.Señal == Señal.Nominal;
            var h = DateTime.Now.Hour;
            string saludo = h < 6 ? "De madrugada" : h < 13 ? "Buen día" : h < 20 ? "Buenas tardes" : "Buenas noches";
            return saludo + ". " + (vivo
                ? "El enlace está **NOMINAL** con `" + c.Servidor.ModeloCorto + "`. Decime."
                : "El modelo está apagado, así que por ahora contesto solo desde el **motor de a bordo**: cuentas, fechas, unidades y el estado de la máquina. Escribí `ayuda` para ver todo, o levantá el modelo en la pestaña **MODELOS**.");
        }

        static string Gracias(string crudo, string b, Contexto c)
        {
            if (!EsSolo(b, "gracias", "muchas gracias", "gracias!", "genial", "joya", "perfecto", "dale")) return null;
            return "De nada.";
        }

        // ------------------------------------------------------------------ utilidades

        static string Normalizar(string s)
        {
            var sb = new StringBuilder(s.ToLowerInvariant());
            sb.Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u').Replace('ü', 'u');
            var t = sb.ToString();
            t = Regex.Replace(t, @"[¿?¡!.,;]", " ");
            t = Regex.Replace(t, @"\s+", " ");
            return t.Trim();
        }

        static bool Contiene(string b, params string[] claves) => claves.Any(k => b.IndexOf(k, StringComparison.Ordinal) >= 0);
        static bool EsSolo(string b, params string[] claves) => claves.Any(k => b == k || b.StartsWith(k + " ", StringComparison.Ordinal) && b.Length < k.Length + 14);

        static bool Numero(string s, out double v) =>
            double.TryParse((s ?? "").Replace(" ", "").Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        static string Bonito(double v)
        {
            if (double.IsNaN(v)) return "no es un número";
            if (double.IsInfinity(v)) return "infinito";
            if (Math.Abs(v) >= 1e12 || (Math.Abs(v) < 1e-6 && v != 0)) return v.ToString("0.####e+0", CultureInfo.InvariantCulture);
            if (Math.Abs(v % 1) < 1e-9) return ((long)Math.Round(v)).ToString("N0", Es);
            return v.ToString("N4", Es).TrimEnd('0').TrimEnd(',');
        }
        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-AR");

        static string Dia(DayOfWeek d)
        {
            switch (d)
            {
                case DayOfWeek.Monday: return "lunes";
                case DayOfWeek.Tuesday: return "martes";
                case DayOfWeek.Wednesday: return "miércoles";
                case DayOfWeek.Thursday: return "jueves";
                case DayOfWeek.Friday: return "viernes";
                case DayOfWeek.Saturday: return "sábado";
                default: return "domingo";
            }
        }
        static string Mes(int m)
        {
            string[] n = { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" };
            return n[Math.Max(1, Math.Min(12, m)) - 1];
        }
    }

    /// <summary>
    /// Parser de expresiones por descenso recursivo. Soporta + - * / % ^, paréntesis, unario, funciones y
    /// constantes. Es corto porque la gramática es corta, y es exacto porque no usa `eval` de nadie.
    /// </summary>
    internal static class Expresion
    {
        public static bool Evaluar(string entrada, out double valor, out string error)
        {
            valor = 0; error = "";
            try
            {
                var p = new Parser(entrada);
                valor = p.Expr();
                p.SaltarEspacios();
                if (!p.Fin) { error = "sobra «" + p.Resto + "»"; return false; }
                return !double.IsNaN(valor);
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        sealed class Parser
        {
            readonly string s; int i;
            public Parser(string texto) { s = (texto ?? "").Replace(" ", ""); }
            public bool Fin => i >= s.Length;
            public string Resto => i < s.Length ? s.Substring(i) : "";
            public void SaltarEspacios() { while (i < s.Length && s[i] == ' ') i++; }
            char Actual => i < s.Length ? s[i] : '\0';

            public double Expr()
            {
                double v = Termino();
                while (true)
                {
                    if (Actual == '+') { i++; v += Termino(); }
                    else if (Actual == '-') { i++; v -= Termino(); }
                    else return v;
                }
            }
            double Termino()
            {
                double v = Potencia();
                while (true)
                {
                    if (Actual == '*') { i++; v *= Potencia(); }
                    else if (Actual == '/') { i++; double d = Potencia(); v = d == 0 ? double.NaN : v / d; }
                    else if (Actual == '%') { i++; double d = Potencia(); v = d == 0 ? double.NaN : v % d; }
                    else return v;
                }
            }
            double Potencia()
            {
                double b = Unario();
                if (Actual == '^') { i++; double e = Potencia(); return Math.Pow(b, e); }
                return b;
            }
            double Unario()
            {
                if (Actual == '-') { i++; return -Unario(); }
                if (Actual == '+') { i++; return Unario(); }
                return Atomo();
            }
            double Atomo()
            {
                if (Actual == '(')
                {
                    i++;
                    double v = Expr();
                    if (Actual == ')') i++;
                    return v;
                }
                if (char.IsLetter(Actual))
                {
                    int ini = i;
                    while (i < s.Length && char.IsLetter(s[i])) i++;
                    string id = s.Substring(ini, i - ini).ToLowerInvariant();
                    if (id == "pi") return Math.PI;
                    if (id == "e") return Math.E;
                    double arg = Atomo();
                    switch (id)
                    {
                        case "raiz": case "sqrt": return Math.Sqrt(arg);
                        case "abs": return Math.Abs(arg);
                        case "ln": return Math.Log(arg);
                        case "log": return Math.Log10(arg);
                        case "sin": case "sen": return Math.Sin(arg);
                        case "cos": return Math.Cos(arg);
                        case "tan": return Math.Tan(arg);
                        case "round": case "redondear": return Math.Round(arg);
                        case "floor": return Math.Floor(arg);
                        case "ceil": return Math.Ceiling(arg);
                        default: throw new Exception("no conozco «" + id + "»");
                    }
                }
                int i0 = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ',')) i++;
                if (i == i0) throw new Exception("esperaba un número en la posición " + i);
                string num = s.Substring(i0, i - i0).Replace(",", ".");
                double v2;
                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v2)) throw new Exception("«" + num + "» no es un número");
                return v2;
            }
        }
    }
}
