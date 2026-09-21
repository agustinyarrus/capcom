using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Capcom
{
    /// <summary>Lo que devuelve un corrector: pasó o no, y POR QUÉ. El «por qué» es lo que hace útil al examen.</summary>
    internal struct Veredicto
    {
        public bool Ok;
        public string Porque;
        public double Parcial;      // 0..1 — algunos correctores dan crédito parcial
        public static Veredicto Bien(string p = "") { return new Veredicto { Ok = true, Parcial = 1, Porque = p }; }
        public static Veredicto Mal(string p) { return new Veredicto { Ok = false, Parcial = 0, Porque = p }; }
        public static Veredicto Medio(double v, string p) { return new Veredicto { Ok = v >= 0.999, Parcial = Math.Max(0, Math.Min(1, v)), Porque = p }; }
    }

    internal sealed class Pregunta
    {
        public string Clave = "";
        public string Categoria = "";
        public string Texto = "";
        public string Sistema = "";
        public int MaxTokens = 140;
        public double Temp = 0.2;
        public double Peso = 1;
        public Func<string, Veredicto> Corregir;
    }

    /// <summary>Lo que pasó con una pregunta en un modelo.</summary>
    internal sealed class Respuesta
    {
        public string Clave = "", Categoria = "", Texto = "";
        public bool Ok;
        public double Parcial;
        public string Porque = "";
        public long Ms, MsPrimerToken;
        public int Tokens;
        public double TokPorSeg;
        public bool Vacia;
    }

    internal sealed class ResultadoExamen
    {
        public string Modelo = "", Ruta = "", Cuant = "";
        public double Gb;
        public DateTime Cuando = DateTime.Now;
        public List<Respuesta> Respuestas = new List<Respuesta>();
        public string Problema = "";            // si no se pudo correr, acá está el motivo
        public long MsCarga;

        public double Puntaje
        {
            get
            {
                double suma = 0, peso = 0;
                foreach (var r in Respuestas)
                {
                    double p = Examen.PesoDe(r.Clave);
                    suma += r.Parcial * p;
                    peso += p;
                }
                return peso <= 0 ? 0 : suma / peso * 100;
            }
        }

        public double PorCategoria(string cat)
        {
            var rs = Respuestas.Where(r => r.Categoria == cat).ToList();
            if (rs.Count == 0) return double.NaN;
            return rs.Average(r => r.Parcial) * 100;
        }

        public int Aciertos => Respuestas.Count(r => r.Ok);
        public int Total => Respuestas.Count;
        public int Vacias => Respuestas.Count(r => r.Vacia);
        /// <summary>Mediana, no promedio: una sola respuesta lenta arruina cualquier promedio.</summary>
        public double SegMediana => Mediana(Respuestas.Where(r => r.Ms > 0).Select(r => r.Ms / 1000.0).ToList());
        public double SegPeor => Respuestas.Count == 0 ? 0 : Respuestas.Max(r => r.Ms) / 1000.0;
        public double TokPorSeg => Mediana(Respuestas.Where(r => r.TokPorSeg > 0).Select(r => r.TokPorSeg).ToList());
        public double LatenciaMediana => Mediana(Respuestas.Where(r => r.MsPrimerToken > 0).Select(r => r.MsPrimerToken / 1000.0).ToList());

        static double Mediana(List<double> v)
        {
            if (v.Count == 0) return 0;
            v.Sort();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        /// <summary>
        /// 🚨 El puntaje global MIENTE si no se mira por categoría: un modelo puede sacar 75 % y contestar en
        /// inglés las tres de castellano. El nivel castiga eso explícitamente.
        /// </summary>
        public string Nivel
        {
            get
            {
                if (Problema.Length > 0) return "—";
                double cast = PorCategoria("castellano");
                if (!double.IsNaN(cast) && cast < 50) return "D";
                double p = Puntaje;
                return p >= 85 ? "A" : p >= 70 ? "B" : p >= 50 ? "C" : "D";
            }
        }

        public string Veredicto
        {
            get
            {
                if (Problema.Length > 0) return Problema;
                double cast = PorCategoria("castellano");
                if (!double.IsNaN(cast) && cast < 50) return "contesta en inglés: no sirve acá por más puntaje que saque";
                if (Vacias > Total / 4) return "deja demasiadas respuestas vacías";
                if (SegMediana > 12) return "contesta bien pero tarda demasiado para conversar";
                switch (Nivel)
                {
                    case "A": return "sirve para todo: escribe bien, razona y respeta el formato";
                    case "B": return "sirve para conversar y resumir; flojea en lo exacto";
                    case "C": return "alcanza para respuestas cortas, no para razonar";
                    default: return "no da el nivel para este trabajo";
                }
            }
        }

        public Dictionary<string, object> AJson() => new Dictionary<string, object>
        {
            ["modelo"] = Modelo,
            ["ruta"] = Ruta,
            ["cuant"] = Cuant,
            ["gb"] = Math.Round(Gb, 3),
            ["cuando"] = Cuando,
            ["msCarga"] = (int)MsCarga,
            ["problema"] = Problema,
            ["puntaje"] = Math.Round(Puntaje, 2),
            ["nivel"] = Nivel,
            ["veredicto"] = Veredicto,
            ["segMediana"] = Math.Round(SegMediana, 3),
            ["tokPorSeg"] = Math.Round(TokPorSeg, 3),
            ["latenciaMediana"] = Math.Round(LatenciaMediana, 3),
            ["vacias"] = Vacias,
            ["respuestas"] = Respuestas.Select(r => new Dictionary<string, object>
            {
                ["clave"] = r.Clave,
                ["categoria"] = r.Categoria,
                ["ok"] = r.Ok,
                ["parcial"] = Math.Round(r.Parcial, 2),
                ["porque"] = r.Porque,
                ["ms"] = (int)r.Ms,
                // 🚨 sin estas dos, al releer el examen del disco la velocidad y la latencia vuelven en CERO:
                //    son propiedades calculadas a partir de cada respuesta, no un campo del resultado.
                ["msPrimerToken"] = (int)r.MsPrimerToken,
                ["tokPorSeg"] = Math.Round(r.TokPorSeg, 3),
                ["tokens"] = r.Tokens,
                ["texto"] = r.Texto.Length > 600 ? r.Texto.Substring(0, 600) + "…" : r.Texto,
            }).ToList(),
        };

        public static ResultadoExamen DeJson(Dictionary<string, object> d)
        {
            var r = new ResultadoExamen
            {
                Modelo = Json.S(d, "modelo"),
                Ruta = Json.S(d, "ruta"),
                Cuant = Json.S(d, "cuant"),
                Gb = Json.D(d, "gb"),
                Cuando = Json.F(d, "cuando") ?? DateTime.Now,
                MsCarga = Json.I(d, "msCarga"),
                Problema = Json.S(d, "problema"),
            };
            foreach (var o in Json.Lista(d, "respuestas"))
                r.Respuestas.Add(new Respuesta
                {
                    Clave = Json.S(o, "clave"),
                    Categoria = Json.S(o, "categoria"),
                    Ok = Json.B(o, "ok"),
                    Parcial = Json.D(o, "parcial"),
                    Porque = Json.S(o, "porque"),
                    Ms = Json.I(o, "ms"),
                    MsPrimerToken = Json.I(o, "msPrimerToken"),
                    TokPorSeg = Json.D(o, "tokPorSeg"),
                    Tokens = Json.I(o, "tokens"),
                    Texto = Json.S(o, "texto"),
                });
            return r;
        }
    }

    /// <summary>
    /// El examen: 20 preguntas con corrección automática, repartidas en nueve categorías.
    ///
    /// La idea es que ninguna pregunta se corrija «a ojo». Cada una trae su verificador: número exacto, JSON
    /// parseado y comparado campo por campo, formato contado renglón por renglón, y el de código **ejecuta**
    /// la función que escribió el modelo contra casos ocultos, en Node y con permisos apagados.
    ///
    /// 🚨 El puntaje global no alcanza para decidir: `castellano` pesa el doble y, si baja del 50 %, el nivel
    /// se hunde a D por más que el resto esté perfecto. Un modelo que contesta en inglés no sirve acá, aunque
    /// razone bárbaro.
    /// </summary>
    internal static class Examen
    {
        public const string Sistema =
            "Sos un asistente que trabaja en castellano rioplatense. Contestás corto y exacto, sin saludos, " +
            "sin disculpas y sin emojis. Cuando te piden un formato, lo respetás al pie de la letra.";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ el banco

        public static readonly List<Pregunta> Preguntas = Armar();

        public static double PesoDe(string clave)
        {
            var p = Preguntas.FirstOrDefault(x => x.Clave == clave);
            return p != null ? p.Peso : 1;
        }

        public static string[] Categorias =>
            Preguntas.Select(p => p.Categoria).Distinct().ToArray();

        static List<Pregunta> Armar()
        {
            var l = new List<Pregunta>();

            // ---------------- castellano (pesa doble: es la dimensión que el promedio tapa)
            l.Add(new Pregunta
            {
                Clave = "cas1", Categoria = "castellano", Peso = 2, MaxTokens = 90,
                Texto = "Che, ¿pudiste mirar el deploy de ayer? Avisame cuando puedas.",
                Corregir = r => EsCastellano(r, "contestó en inglés"),
            });
            l.Add(new Pregunta
            {
                Clave = "cas2", Categoria = "castellano", Peso = 2, MaxTokens = 80,
                Texto = "Traducí al castellano rioplatense, devolviendo SOLO la traducción: \"I'll check it later and let you know.\"",
                Corregir = r =>
                {
                    var v = EsCastellano(r, "no tradujo al castellano");
                    if (!v.Ok) return v;
                    string b = Normal(r);
                    bool despues = Tiene(b, "despues", "luego", "mas tarde", "ahora", "rato");
                    bool aviso = Tiene(b, "aviso", "avisar", "te digo", "comento", "cuento");
                    if (despues && aviso) return Veredicto.Bien();
                    return Veredicto.Medio(0.5, "traduce pero se come una de las dos ideas (cuándo / que avisa)");
                },
            });
            l.Add(new Pregunta
            {
                Clave = "cas3", Categoria = "castellano", Peso = 2, MaxTokens = 110,
                Texto = "Explicá en dos oraciones qué es un firmware, como se lo explicarías a un compañero de trabajo.",
                Corregir = r =>
                {
                    var v = EsCastellano(r, "explicó en inglés");
                    if (!v.Ok) return v;
                    string b = Normal(r);
                    if (!Tiene(b, "firmware", "programa", "software", "dispositivo", "hardware"))
                        return Veredicto.Mal("no habla del tema");
                    return Veredicto.Bien();
                },
            });

            // ---------------- aritmética (número exacto)
            l.Add(new Pregunta
            {
                Clave = "ari1", Categoria = "aritmética", MaxTokens = 40,
                Texto = "¿Cuánto es 17 × 23? Contestá solamente el número.",
                Corregir = r => NumeroEs(r, 391, 0),
            });
            l.Add(new Pregunta
            {
                Clave = "ari2", Categoria = "aritmética", MaxTokens = 40,
                Texto = "¿Cuánto es el 15% de 240? Contestá solamente el número.",
                Corregir = r => NumeroEs(r, 36, 0),
            });
            l.Add(new Pregunta
            {
                Clave = "ari3", Categoria = "aritmética", MaxTokens = 60,
                Texto = "Un archivo pesa 2,5 GB. ¿Cuántos MB son, con 1 GB = 1024 MB? Contestá solamente el número.",
                Corregir = r => NumeroEs(r, 2560, 1),
            });

            // ---------------- razonamiento
            l.Add(new Pregunta
            {
                Clave = "raz1", Categoria = "razonamiento", MaxTokens = 40,
                Texto = "Todos los hubs son dispositivos. Ningún dispositivo es una cuenta. ¿Puede un hub ser una cuenta? Contestá solamente SÍ o NO.",
                Corregir = r => PalabraEs(r, new[] { "no" }, new[] { "si", "sí" }, "tendría que decir NO"),
            });
            l.Add(new Pregunta
            {
                Clave = "raz2", Categoria = "razonamiento", MaxTokens = 40,
                Texto = "La caja A pesa más que la B. La C pesa menos que la B. ¿Cuál es la más liviana? Contestá solamente la letra.",
                Corregir = r => PalabraEs(r, new[] { "c" }, new[] { "a", "b" }, "la más liviana es la C"),
            });
            l.Add(new Pregunta
            {
                Clave = "raz3", Categoria = "razonamiento", MaxTokens = 50,
                Texto = "Un proceso arranca a las 09:40 y tarda 95 minutos. ¿A qué hora termina? Contestá solamente la hora en formato HH:MM.",
                Corregir = r =>
                {
                    var m = Regex.Match(r, @"\b(\d{1,2})[:.](\d{2})\b");
                    if (!m.Success) return Veredicto.Mal("no devolvió una hora");
                    int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
                    return h == 11 && mi == 15 ? Veredicto.Bien() : Veredicto.Mal("dijo " + m.Value + " y son las 11:15");
                },
            });

            // ---------------- extracción a JSON (se parsea y se compara campo por campo)
            l.Add(new Pregunta
            {
                Clave = "jso1", Categoria = "json", MaxTokens = 190,
                Texto = "De este renglón de log sacá un JSON con las claves ticket, ambiente y severidad, y devolvé SOLO el JSON:\n" +
                        "2026-09-14 11:02:33 [UAT] ERROR OPS-2417 el identificador del sensor llega vacío",
                Corregir = r =>
                {
                    var d = JsonDeTexto(r);
                    if (d == null) return Veredicto.Mal("no devolvió un JSON parseable");
                    double n = 0;
                    if (Json.S(d, "ticket", "").IndexOf("OPS-2417", StringComparison.OrdinalIgnoreCase) >= 0) n++;
                    if (Normal(Json.S(d, "ambiente", "")).Contains("uat")) n++;
                    string sev = Normal(Json.S(d, "severidad", ""));
                    if (sev.Contains("error") || sev.Contains("alta") || sev.Contains("critic")) n++;
                    return Veredicto.Medio(n / 3.0, n == 3 ? "" : "acertó " + n + " de 3 campos");
                },
            });
            l.Add(new Pregunta
            {
                Clave = "jso2", Categoria = "json", MaxTokens = 190,
                Texto = "Devolvé SOLO un JSON con la clave macs y un arreglo con las direcciones MAC que aparezcan acá, " +
                        "en mayúsculas y separadas por dos puntos:\n" +
                        "el hub 00-1a-2b-3c-4d-5e no engancha y el doorbell aa:bb:cc:dd:ee:ff sí",
                Corregir = r =>
                {
                    var d = JsonDeTexto(r);
                    if (d == null) return Veredicto.Mal("no devolvió un JSON parseable");
                    var macs = Json.Cadenas(d, "macs").Select(x => Regex.Replace(x, "[^0-9a-fA-F]", "").ToUpperInvariant()).ToList();
                    double n = 0;
                    if (macs.Contains("001A2B3C4D5E")) n++;
                    if (macs.Contains("AABBCCDDEEFF")) n++;
                    double v = n / 2.0;
                    if (v >= 1 && macs.Count > 2) v = 0.8;      // metió de más
                    return Veredicto.Medio(v, n == 2 ? "" : "encontró " + n + " de 2 MAC");
                },
            });

            // ---------------- código (se EJECUTA en Node contra casos ocultos)
            l.Add(new Pregunta
            {
                Clave = "cod1", Categoria = "código", MaxTokens = 420, Peso = 1.5,
                Texto = "Escribí una función JavaScript `normalizarMac(s)` que reciba una MAC en cualquier formato " +
                        "(con guiones, dos puntos, puntos o sin separador, en mayúsculas o minúsculas) y devuelva los " +
                        "12 dígitos en MAYÚSCULAS separados por dos puntos de a dos. Si la entrada no tiene 12 dígitos " +
                        "hexadecimales, devolvé null. Devolvé SOLO el bloque de código, sin explicación.",
                Corregir = r => Node.Probar(r, "normalizarMac", new[]
                {
                    new[] { "\"00-1a-2b-3c-4d-5e\"", "\"00:1A:2B:3C:4D:5E\"" },
                    new[] { "\"aabbccddeeff\"", "\"AA:BB:CC:DD:EE:FF\"" },
                    new[] { "\"AA:BB:CC:DD:EE:FF\"", "\"AA:BB:CC:DD:EE:FF\"" },
                    new[] { "\"0011.2233.4455\"", "\"00:11:22:33:44:55\"" },
                    new[] { "\"00:11:22:33:44\"", "null" },
                    new[] { "\"zz:11:22:33:44:55\"", "null" },
                }),
            });
            l.Add(new Pregunta
            {
                Clave = "cod2", Categoria = "código", MaxTokens = 360, Peso = 1.5,
                Texto = "Escribí una función JavaScript `contarPalabras(s)` que devuelva cuántas palabras tiene un texto, " +
                        "tratando cualquier cantidad de espacios, tabulaciones o saltos de línea como un solo separador, " +
                        "y devolviendo 0 si no hay ninguna. Devolvé SOLO el bloque de código, sin explicación.",
                Corregir = r => Node.Probar(r, "contarPalabras", new[]
                {
                    new[] { "\"hola mundo\"", "2" },
                    new[] { "\"  hola   mundo  \"", "2" },
                    new[] { "\"\"", "0" },
                    new[] { "\"   \"", "0" },
                    new[] { "\"uno\\ndos\\ttres\"", "3" },
                }),
            });

            // ---------------- seguir instrucciones al pie de la letra
            l.Add(new Pregunta
            {
                Clave = "for1", Categoria = "formato", MaxTokens = 30,
                Texto = "Contestá exactamente con la palabra NOMINAL, en mayúsculas, y nada más.",
                Corregir = r =>
                {
                    string t = r.Trim().Trim('.', '"', '\'', '*', '`');
                    if (t == "NOMINAL") return Veredicto.Bien();
                    if (t.ToUpperInvariant().Contains("NOMINAL")) return Veredicto.Medio(0.5, "dijo NOMINAL pero agregó cosas");
                    return Veredicto.Mal("no dijo NOMINAL");
                },
            });
            l.Add(new Pregunta
            {
                Clave = "for2", Categoria = "formato", MaxTokens = 140,
                Texto = "Dame exactamente tres viñetas con los pasos para reiniciar un hub. Cada renglón tiene que empezar " +
                        "con «- » y no puede haber título, introducción ni cierre.",
                Corregir = r =>
                {
                    var ls = r.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    int vin = ls.Count(x => x.StartsWith("- ") || x.StartsWith("-\t"));
                    if (ls.Count == 3 && vin == 3) return Veredicto.Bien();
                    if (vin == 3) return Veredicto.Medio(0.5, "las tres viñetas están pero agregó " + (ls.Count - 3) + " renglón/es de más");
                    return Veredicto.Mal("devolvió " + vin + " viñetas en vez de 3");
                },
            });
            l.Add(new Pregunta
            {
                Clave = "for3", Categoria = "formato", MaxTokens = 110,
                Texto = "Contame en UNA sola oración qué hace un router, sin emojis y sin signos de exclamación.",
                Corregir = r =>
                {
                    string t = r.Trim();
                    if (t.Length == 0) return Veredicto.Mal("vacío");
                    if (TieneEmoji(t)) return Veredicto.Mal("metió emojis");
                    if (t.Contains("!") || t.Contains("¡")) return Veredicto.Mal("usó signos de exclamación");
                    int puntos = t.Count(c => c == '.' || c == ';') + t.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries).Length - 1;
                    if (puntos > 1) return Veredicto.Medio(0.5, "se pasó de una oración");
                    return Veredicto.Bien();
                },
            });

            // ---------------- honestidad: lo que NO se puede saber hay que decirlo
            l.Add(new Pregunta
            {
                Clave = "hon1", Categoria = "honestidad", MaxTokens = 110, Peso = 1.5,
                Texto = "¿Qué versión del backend quedó desplegada en el ambiente de UAT el 30 de febrero de 2026?",
                Corregir = r =>
                {
                    string b = Normal(r);
                    bool admite = Tiene(b, "no se", "no tengo", "no puedo", "no dispongo", "no existe", "no hay informacion",
                                           "no cuento", "desconozco", "no tengo acceso", "30 de febrero no");
                    if (admite) return Veredicto.Bien();
                    if (Regex.IsMatch(r, @"\d+\.\d+\.\d+")) return Veredicto.Mal("inventó un número de versión");
                    return Veredicto.Medio(0.3, "no dice claramente que no puede saberlo");
                },
            });
            l.Add(new Pregunta
            {
                Clave = "hon2", Categoria = "honestidad", MaxTokens = 110, Peso = 1.5,
                Texto = "¿Cuántos usuarios tiene ahora mismo la base de datos inventario_qa?",
                Corregir = r =>
                {
                    string b = Normal(r);
                    bool admite = Tiene(b, "no se", "no tengo", "no puedo", "no dispongo", "no tengo acceso", "desconozco",
                                           "no cuento", "necesitaria", "habria que consultar", "consultar la base");
                    if (admite) return Veredicto.Bien();
                    if (Regex.IsMatch(r, @"\b\d{2,}\b")) return Veredicto.Mal("inventó una cantidad");
                    return Veredicto.Medio(0.3, "no dice claramente que no puede saberlo");
                },
            });

            // ---------------- contexto largo: encontrar la aguja
            l.Add(new Pregunta
            {
                Clave = "ctx1", Categoria = "contexto", MaxTokens = 40,
                Texto = TextoLargoConPin(),
                Corregir = r =>
                {
                    var m = Regex.Match(r, @"\b(\d{8})\b");
                    if (!m.Success) return Veredicto.Mal("no devolvió un PIN de 8 dígitos");
                    return m.Groups[1].Value == "58204917" ? Veredicto.Bien() : Veredicto.Mal("devolvió " + m.Groups[1].Value);
                },
            });

            // ---------------- resumen
            l.Add(new Pregunta
            {
                Clave = "res1", Categoria = "resumen", MaxTokens = 90,
                Texto = "Resumí esto en UNA sola oración de menos de 20 palabras:\n" +
                        "El equipo detectó que las facturas salían duplicadas porque el servicio de cobros reintentaba el " +
                        "pedido cuando la respuesta tardaba más de dos segundos, aunque el primer intento ya había quedado " +
                        "registrado en la base y el cliente no había hecho nada raro.",
                Corregir = r =>
                {
                    string t = r.Trim();
                    if (t.Length == 0) return Veredicto.Mal("vacío");
                    int palabras = t.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    string b = Normal(t);
                    bool tema = Tiene(b, "factura", "duplic", "reintent", "cobro", "doble");
                    if (!tema) return Veredicto.Mal("el resumen se fue de tema");
                    if (palabras <= 20) return Veredicto.Bien();
                    if (palabras <= 28) return Veredicto.Medio(0.5, "se pasó: " + palabras + " palabras");
                    return Veredicto.Mal("demasiado largo: " + palabras + " palabras");
                },
            });

            return l;
        }

        static string TextoLargoConPin()
        {
            var sb = new StringBuilder();
            sb.Append("Leé este extracto de log y contestá SOLO con el PIN de 8 dígitos que aparece en él.\n\n");
            string[] ruido =
            {
                "2026-09-14 10:{0:00}:{1:00} [UAT] INFO  sincronizando la cuenta 118204 con el nodo 3127",
                "2026-09-14 10:{0:00}:{1:00} [UAT] DEBUG caché de dispositivos vencida, se recalcula en 1200 ms",
                "2026-09-14 10:{0:00}:{1:00} [UAT] INFO  el invitado quedó sin ventana horaria asignada",
                "2026-09-14 10:{0:00}:{1:00} [UAT] WARN  reintento 2 de 5 contra el servicio de presencia",
                "2026-09-14 10:{0:00}:{1:00} [UAT] INFO  firmware 7f3a21c9 confirmado por el sensor de la puerta",
            };
            for (int i = 0; i < 26; i++)
            {
                if (i == 15) sb.Append("2026-09-14 10:31:07 [UAT] INFO  se generó el PIN de acceso temporal 58204917 para el invitado\n");
                sb.Append(string.Format(ruido[i % ruido.Length], i, (i * 7) % 60)).Append('\n');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ correctores compartidos

        static readonly string[] Ingles =
        {
            " the ", " you ", " your ", " please ", " sure,", " i'll ", " i will ", " let me ", " here's ",
            " it's ", " we'll ", " of the ", " to the ", " and the ", "hello", "hi there", "sorry,", " can i ",
        };

        public static Veredicto EsCastellano(string r, string porque)
        {
            if (string.IsNullOrWhiteSpace(r)) return Veredicto.Mal("vacío");
            string t = " " + r.ToLowerInvariant().Replace('\n', ' ') + " ";
            int ing = Ingles.Count(k => t.Contains(k));
            string b = Normal(r);
            int esp = new[] { " que ", " de ", " la ", " el ", " y ", " en ", " para ", " con ", " lo ", " te ", " se " }
                .Count(k => (" " + b + " ").Contains(k));
            if (ing >= 2 && ing > esp) return Veredicto.Mal(porque);
            if (esp == 0 && ing > 0) return Veredicto.Mal(porque);
            if (esp == 0) return Veredicto.Medio(0.5, "demasiado corto para saber en qué idioma está");
            return Veredicto.Bien();
        }

        static Veredicto NumeroEs(string r, double esperado, double tolerancia)
        {
            var nums = Regex.Matches(r.Replace(".", "").Replace(",", "."), @"-?\d+(?:\.\d+)?")
                            .Cast<Match>().Select(m => double.Parse(m.Value, Inv)).ToList();
            if (nums.Count == 0) return Veredicto.Mal("no devolvió ningún número");
            if (nums.Any(n => Math.Abs(n - esperado) <= tolerancia))
                return nums.Count == 1 ? Veredicto.Bien() : Veredicto.Medio(0.8, "acertó pero devolvió varios números");
            return Veredicto.Mal("dijo " + nums[0].ToString("0.##", Inv) + " y es " + esperado.ToString("0.##", Inv));
        }

        static Veredicto PalabraEs(string r, string[] buenas, string[] malas, string porque)
        {
            string b = " " + Normal(r) + " ";
            if (buenas.Any(x => b.Contains(" " + x + " ") || b.Trim() == x)) return Veredicto.Bien();
            if (malas.Any(x => b.Contains(" " + x + " "))) return Veredicto.Mal(porque);
            return Veredicto.Mal(porque);
        }

        public static string Normal(string s)
        {
            var sb = new StringBuilder((s ?? "").ToLowerInvariant());
            sb.Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u').Replace('ü', 'u');
            var t = Regex.Replace(sb.ToString(), @"[^\w\s:]", " ");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        static bool Tiene(string b, params string[] claves) => claves.Any(k => b.Contains(k));

        static bool TieneEmoji(string s) =>
            s.Any(c => c >= 0x1F300 || (c >= 0x2600 && c <= 0x27BF)) ||
            s.Where(char.IsHighSurrogate).Any();

        /// <summary>Saca el primer objeto JSON de una respuesta que puede venir con explicación o con vallas.</summary>
        public static Dictionary<string, object> JsonDeTexto(string r)
        {
            if (string.IsNullOrWhiteSpace(r)) return null;
            string t = r;
            var valla = Regex.Match(t, @"```(?:json)?\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
            if (valla.Success) t = valla.Groups[1].Value;
            else
            {
                int i = t.IndexOf('{'), j = t.LastIndexOf('}');
                if (i < 0 || j <= i) return null;
                t = t.Substring(i, j - i + 1);
            }
            return Json.Parsear(t);
        }
    }

    /// <summary>
    /// El corrector de código: ejecuta la función que escribió el modelo contra casos ocultos.
    ///
    /// ⭐ Es la diferencia entre «parece bien» y «anda»: un regex sobre el código aprueba cualquier cosa que
    /// tenga las palabras correctas. Acá el código se corre de verdad.
    /// 🚨 Se corre con `--permission` y sin ningún allow: el script no puede tocar el disco, la red ni lanzar
    /// procesos. Y con un techo de 10 s, porque un `while(true)` del modelo es perfectamente posible.
    /// </summary>
    internal static class Node
    {
        static string ruta;
        static bool buscado;

        public static string Ruta
        {
            get
            {
                if (buscado) return ruta;
                buscado = true;
                foreach (var c in new[] { "node.exe", @"C:\nvm4w\nodejs\node.exe", @"C:\Program Files\nodejs\node.exe" })
                {
                    try
                    {
                        if (c.Contains("\\")) { if (File.Exists(c)) { ruta = c; return ruta; } }
                        else
                        {
                            var p = Environment.GetEnvironmentVariable("PATH") ?? "";
                            foreach (var dir in p.Split(';'))
                            {
                                if (dir.Trim().Length == 0) continue;
                                var f = Path.Combine(dir.Trim(), c);
                                if (File.Exists(f)) { ruta = f; return ruta; }
                            }
                        }
                    }
                    catch { }
                }
                return ruta;
            }
        }

        public static bool Hay => !string.IsNullOrEmpty(Ruta);

        /// <summary>Extrae el bloque de código de la respuesta (con o sin vallas) y lo deja listo para correr.</summary>
        public static string SacarCodigo(string r)
        {
            if (string.IsNullOrWhiteSpace(r)) return "";
            var m = Regex.Match(r, @"```(?:js|javascript|node)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            string c = m.Success ? m.Groups[1].Value : r;
            // sacar una eventual línea de explicación antes del function/const
            int i = c.IndexOfAny(new[] { 'f', 'c', 'l', 'v', '(' });
            var mm = Regex.Match(c, @"(function\s|const\s|let\s|var\s|export\s)");
            if (mm.Success) c = c.Substring(mm.Index);
            return c.Trim();
        }

        /// <summary>
        /// Corre `fn(entrada)` para cada caso y compara con lo esperado usando igualdad estructural.
        /// Devuelve crédito parcial: 4 de 6 casos es 0,67, no un cero.
        /// </summary>
        public static Veredicto Probar(string respuesta, string fn, string[][] casos)
        {
            string codigo = SacarCodigo(respuesta);
            if (codigo.Length == 0) return Veredicto.Mal("no devolvió código");
            if (!Hay)
            {
                // sin Node no se puede ejecutar: se dice, y se corrige por estructura para no regalar el punto
                bool declara = Regex.IsMatch(codigo, @"\b" + Regex.Escape(fn) + @"\b");
                return Veredicto.Medio(declara ? 0.5 : 0, "no hay Node para ejecutarlo: corregido a ojo");
            }

            string dir = Path.Combine(Path.GetTempPath(), "capcom-examen");
            Directory.CreateDirectory(dir);
            string archivo = Path.Combine(dir, "p-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mjs");
            var sb = new StringBuilder();
            sb.AppendLine(codigo);
            sb.AppendLine();
            sb.AppendLine("const __casos = [");
            foreach (var c in casos) sb.AppendLine("  [" + c[0] + ", " + c[1] + "],");
            sb.AppendLine("];");
            sb.AppendLine("let __ok = 0; const __detalle = [];");
            sb.AppendLine("for (const [__e, __esp] of __casos) {");
            sb.AppendLine("  let __r; try { __r = " + fn + "(__e); } catch (ex) { __detalle.push('tiró ' + ex.message); continue; }");
            sb.AppendLine("  const __a = JSON.stringify(__r) ?? 'undefined', __b = JSON.stringify(__esp) ?? 'undefined';");
            sb.AppendLine("  if (__a === __b) __ok++; else __detalle.push(JSON.stringify(__e) + ' → ' + __a + ' (esperaba ' + __b + ')');");
            sb.AppendLine("}");
            sb.AppendLine("console.log(JSON.stringify({ ok: __ok, total: __casos.length, detalle: __detalle.slice(0, 3) }));");
            File.WriteAllText(archivo, sb.ToString(), new UTF8Encoding(false));

            try
            {
                var psi = new ProcessStartInfo(Ruta)
                {
                    // 🚨 permisos apagados: el código del modelo no puede leer el disco, salir a la red ni spawnear
                    Arguments = "--no-warnings --permission \"" + archivo + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = dir,
                };
                using (var p = Process.Start(psi))
                {
                    string salida = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000))
                    {
                        try { p.Kill(); } catch { }
                        return Veredicto.Mal("el código se colgó (más de 10 s)");
                    }
                    var linea = salida.Split('\n').LastOrDefault(x => x.TrimStart().StartsWith("{"));
                    if (linea == null)
                    {
                        string e = (err ?? "").Split('\n').FirstOrDefault(x => x.Contains("Error")) ?? "no compiló";
                        return Veredicto.Mal("no corrió: " + Corto(e, 70));
                    }
                    var d = Json.Parsear(linea);
                    int ok = Json.I(d, "ok"), total = Math.Max(1, Json.I(d, "total", casos.Length));
                    var det = Json.Cadenas(d, "detalle");
                    double v = (double)ok / total;
                    return Veredicto.Medio(v, ok == total ? "" : ok + "/" + total + " casos · " + Corto(string.Join(" · ", det), 90));
                }
            }
            catch (Exception ex) { return Veredicto.Mal("no pude ejecutarlo: " + Corto(ex.Message, 60)); }
            finally { try { File.Delete(archivo); } catch { } }
        }

        static string Corto(string s, int n) { s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim(); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }
    }
}
