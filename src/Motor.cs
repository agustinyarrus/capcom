using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Capcom
{
    /// <summary>Los parámetros de muestreo que viajan en cada pedido.</summary>
    internal sealed class Ajustes
    {
        public double Temperatura = 0.7, TopP = 0.8, Presencia = 0, Repeticion = 1.0;
        public int TopK = 20, MaxTokens = 512;
        public bool Pensar;

        public static Ajustes De(Config c) => new Ajustes
        {
            Temperatura = c.Temperatura, TopP = c.TopP, TopK = c.TopK,
            MaxTokens = c.MaxTokens, Presencia = c.Presencia, Repeticion = c.Repeticion, Pensar = c.Pensar
        };
    }

    internal enum Señal { SinSeñal, Cargando, Nominal }

    /// <summary>Lo que se sabe del servidor en el último sondeo.</summary>
    internal sealed class EstadoServidor
    {
        public Señal Señal = Señal.SinSeñal;
        public string Modelo = "";
        public string Detalle = "sin sondear";
        public int Contexto;
        public int Slots;
        public DateTime Cuando = DateTime.MinValue;
        public long MsSondeo;
        public string ModeloCorto => Modelo.Length > 0 ? Path.GetFileNameWithoutExtension(Modelo) : "";
    }

    /// <summary>Lo que devuelve una generación completa.</summary>
    internal sealed class Resultado
    {
        public string Texto = "";
        public string Pensamiento = "";
        public int Tokens, TokensPrompt;
        public long Ms, MsPrimerToken;
        public double TokPorSeg;
        public bool Cancelado;
        public string Error = "";
        public string Modelo = "";
        public string Fin = "";
    }

    /// <summary>
    /// Cliente del llama-server local (API compatible con OpenAI). Hace streaming de verdad: lee el SSE token a
    /// token y avisa a medida que llega, que es lo que hace que la espera se sienta corta.
    ///
    /// 🚨 Tres cosas que si faltan hacen que el servidor parezca sano y la respuesta vuelva VACÍA:
    ///   1. `req.Proxy = null` — el proxy corporativo se come los POST a 127.0.0.1 (el GET de /health pasa igual).
    ///   2. `chat_template_kwargs.enable_thinking = false` — los Qwen3 razonan por defecto y con --jinja ese
    ///      razonamiento va a `reasoning_content`, dejando `content` vacío hasta quemar el presupuesto entero.
    ///   3. leer `reasoning_content` igual: si el modelo pensó y no contestó, eso es un diagnóstico, no un "vacío".
    /// </summary>
    internal sealed class Cliente
    {
        /// <summary>
        /// 🚨🚨 Sin esto, el PRIMER pedido a 127.0.0.1 tarda segundos y termina en «The operation has timed out»
        /// aunque no haya nadie escuchando: .NET arranca el descubrimiento automático de proxy (WPAD) del sistema
        /// antes de mirar la URL, y en una red corporativa eso se cuelga. Poner `req.Proxy = null` por pedido NO
        /// alcanza porque el proxy por defecto ya se inicializó. Se anula UNA vez, para todo el proceso.
        /// </summary>
        static Cliente()
        {
            try
            {
                WebRequest.DefaultWebProxy = null;
                ServicePointManager.DefaultConnectionLimit = 32;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
            }
            catch { }
        }

        readonly Logger log;
        public string Url = "http://127.0.0.1:8080";
        public EstadoServidor Estado = new EstadoServidor();

        HttpWebRequest enVuelo;
        readonly object candado = new object();
        public bool Generando { get; private set; }

        public Cliente(Logger l) { log = l; }

        // ------------------------------------------------------------------ sondeo

        /// <summary>Le pregunta al servidor quién es. Barato: se llama cada pocos segundos desde la telemetría.</summary>
        public EstadoServidor Sondear(int timeoutMs = 2500)
        {
            var e = new EstadoServidor { Cuando = DateTime.Now };
            var reloj = Stopwatch.StartNew();
            try
            {
                string cuerpo = Pedir(Url.TrimEnd('/') + "/props", null, timeoutMs);
                var d = Json.Parsear(cuerpo);
                e.Modelo = Json.S(d, "model_path");
                if (e.Modelo.Length == 0) e.Modelo = Json.S(d, "model");
                if (d != null && d.TryGetValue("default_generation_settings", out var gs) && gs is Dictionary<string, object> gd)
                {
                    e.Contexto = Json.I(gd, "n_ctx");
                    if (e.Contexto == 0 && gd.TryGetValue("params", out var pp) && pp is Dictionary<string, object> pd) e.Contexto = Json.I(pd, "n_ctx");
                }
                if (e.Contexto == 0) e.Contexto = Json.I(d, "n_ctx");
                e.Slots = Json.I(d, "total_slots");
                e.Señal = Señal.Nominal;
                e.Detalle = e.ModeloCorto.Length > 0 ? e.ModeloCorto : "modelo sin nombre";
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.ConnectFailure)
            {
                e.Señal = Señal.SinSeñal;
                e.Detalle = "no hay servidor escuchando en " + Url;
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
            {
                e.Señal = Señal.SinSeñal;
                e.Detalle = "el servidor no contestó en " + timeoutMs + " ms";
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r && (int)r.StatusCode == 503)
            {
                e.Señal = Señal.Cargando;
                e.Detalle = "el modelo se está cargando";
            }
            catch (Exception ex)
            {
                e.Señal = Señal.SinSeñal;
                e.Detalle = Corto(ex.Message);
            }
            reloj.Stop();
            e.MsSondeo = reloj.ElapsedMilliseconds;
            Estado = e;
            return e;
        }

        static string Corto(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > 120 ? s.Substring(0, 119) + "…" : s; }

        // ------------------------------------------------------------------ generación con streaming

        /// <summary>
        /// Manda el hilo y va entregando el texto a medida que sale. Bloquea el hilo en el que se lo llama:
        /// el que llama tiene que estar en un hilo de fondo y marshalear los callbacks a la UI.
        /// </summary>
        public Resultado Generar(string sistema, IEnumerable<Mensaje> historia, Ajustes a,
                                 Action<string> alLlegarTexto, Action<string> alPensar, int timeoutMs = 600000)
        {
            var res = new Resultado();
            var reloj = Stopwatch.StartNew();
            long msPrimero = 0;
            var salida = new StringBuilder();
            var pensado = new StringBuilder();

            string cuerpo = Cuerpo(sistema, historia, a);
            HttpWebRequest req = null;
            try
            {
                req = Armar(Url.TrimEnd('/') + "/v1/chat/completions", timeoutMs);
                var datos = new UTF8Encoding(false).GetBytes(cuerpo);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.ContentLength = datos.Length;
                req.Accept = "text/event-stream";
                using (var s = req.GetRequestStream()) s.Write(datos, 0, datos.Length);

                lock (candado) { enVuelo = req; Generando = true; }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var st = resp.GetResponseStream())
                using (var sr = new StreamReader(st, new UTF8Encoding(false)))
                {
                    string linea;
                    while ((linea = sr.ReadLine()) != null)
                    {
                        if (linea.Length == 0) continue;
                        if (linea.StartsWith("data:", StringComparison.Ordinal)) linea = linea.Substring(5).Trim();
                        else continue;
                        if (linea == "[DONE]") break;

                        var d = Json.Parsear(linea);
                        if (d == null) continue;

                        // error en medio del stream (pasa con plantillas que no aceptan un parámetro)
                        if (d.TryGetValue("error", out var errObj))
                        {
                            res.Error = errObj is Dictionary<string, object> ed ? Json.S(ed, "message", errObj.ToString()) : errObj.ToString();
                            break;
                        }

                        foreach (var ch in Json.Lista(d, "choices"))
                        {
                            string fin = Json.S(ch, "finish_reason");
                            if (fin.Length > 0) res.Fin = fin;
                            if (!ch.TryGetValue("delta", out var dl) || !(dl is Dictionary<string, object> delta)) continue;

                            string trozo = Json.S(delta, "content");
                            if (trozo.Length > 0)
                            {
                                if (salida.Length == 0) msPrimero = reloj.ElapsedMilliseconds;
                                salida.Append(trozo);
                                try { alLlegarTexto?.Invoke(trozo); } catch { }
                            }
                            string piensa = Json.S(delta, "reasoning_content");
                            if (piensa.Length > 0)
                            {
                                pensado.Append(piensa);
                                try { alPensar?.Invoke(piensa); } catch { }
                            }
                        }

                        if (d.TryGetValue("usage", out var us) && us is Dictionary<string, object> ud)
                        {
                            res.Tokens = Json.I(ud, "completion_tokens", res.Tokens);
                            res.TokensPrompt = Json.I(ud, "prompt_tokens", res.TokensPrompt);
                        }
                        if (d.TryGetValue("timings", out var tm) && tm is Dictionary<string, object> td)
                        {
                            int pe = Json.I(td, "predicted_n"); if (pe > 0) res.Tokens = pe;
                            int pr = Json.I(td, "prompt_n"); if (pr > 0) res.TokensPrompt = pr;
                        }
                        if (res.Error.Length > 0) break;
                    }
                }
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.RequestCanceled)
            {
                res.Cancelado = true;
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.ConnectFailure)
                    res.Error = "no hay servidor escuchando en " + Url + " — levantalo desde la pestaña MODELOS";
                else if (ex.Status == WebExceptionStatus.Timeout)
                    res.Error = "el servidor dejó de contestar (se cortó la espera)";
                else
                {
                    res.Error = Corto(ex.Message);
                    try
                    {
                        if (ex.Response != null)
                            using (var r = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                            {
                                var cuerpoErr = r.ReadToEnd();
                                var de = Json.Parsear(cuerpoErr);
                                if (de != null && de.TryGetValue("error", out var eo))
                                    res.Error = eo is Dictionary<string, object> ed2 ? Json.S(ed2, "message", res.Error) : Corto(eo.ToString());
                                else if (cuerpoErr.Length > 0) res.Error = Corto(cuerpoErr);
                            }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { res.Error = Corto(ex.Message); }
            finally
            {
                lock (candado) { enVuelo = null; Generando = false; }
                reloj.Stop();
            }

            res.Texto = salida.ToString();
            res.Pensamiento = pensado.ToString();
            res.Ms = reloj.ElapsedMilliseconds;
            res.MsPrimerToken = msPrimero;
            res.Modelo = Estado.ModeloCorto;
            if (res.Tokens == 0 && res.Texto.Length > 0) res.Tokens = Estimar(res.Texto);
            double seg = Math.Max(0.001, (res.Ms - res.MsPrimerToken) / 1000.0);
            if (res.Tokens > 0) res.TokPorSeg = res.Tokens / seg;

            if (res.Texto.Length == 0 && res.Error.Length == 0 && !res.Cancelado)
                res.Error = res.Pensamiento.Length > 0
                    ? "el modelo se quedó pensando y no llegó a contestar — subí max tokens o apagá «pensar»"
                    : res.Fin == "length" ? "se quedó sin presupuesto de tokens antes de escribir nada"
                    : "el modelo contestó vacío";
            return res;
        }

        /// <summary>Corta la generación en curso: aborta el pedido HTTP, que es lo único que de verdad la frena.</summary>
        public void Cortar()
        {
            HttpWebRequest r;
            lock (candado) r = enVuelo;
            if (r == null) return;
            try { r.Abort(); } catch { }
        }

        /// <summary>Estimación grosera de tokens para castellano cuando el servidor no manda `usage`.</summary>
        public static int Estimar(string s) => string.IsNullOrEmpty(s) ? 0 : Math.Max(1, (int)Math.Round(s.Length / 3.6));

        // ------------------------------------------------------------------ armado del pedido

        string Cuerpo(string sistema, IEnumerable<Mensaje> historia, Ajustes a)
        {
            var sb = new StringBuilder();
            sb.Append("{\"messages\":[");
            bool primero = true;
            if (!string.IsNullOrWhiteSpace(sistema))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(Json.Cita(sistema)).Append('}');
                primero = false;
            }
            foreach (var m in historia)
            {
                if (m.Rol == Rol.Sistema) continue;
                if (string.IsNullOrWhiteSpace(m.Texto)) continue;
                if (m.Error) continue;                       // un error de la app no es parte de la conversación
                if (!primero) sb.Append(',');
                primero = false;
                sb.Append("{\"role\":\"").Append(m.Rol == Rol.Usuario ? "user" : "assistant")
                  .Append("\",\"content\":").Append(Json.Cita(m.Texto)).Append('}');
            }
            sb.Append("],");
            var ci = CultureInfo.InvariantCulture;
            sb.Append("\"stream\":true,\"stream_options\":{\"include_usage\":true},\"timings_per_token\":true,");
            sb.Append("\"max_tokens\":").Append(Math.Max(16, a.MaxTokens)).Append(',');
            sb.Append("\"temperature\":").Append(a.Temperatura.ToString("0.###", ci)).Append(',');
            sb.Append("\"top_p\":").Append(a.TopP.ToString("0.###", ci)).Append(',');
            sb.Append("\"top_k\":").Append(a.TopK.ToString(ci)).Append(',');
            sb.Append("\"presence_penalty\":").Append(a.Presencia.ToString("0.###", ci)).Append(',');
            sb.Append("\"repeat_penalty\":").Append(a.Repeticion.ToString("0.###", ci)).Append(',');
            sb.Append("\"chat_template_kwargs\":{\"enable_thinking\":").Append(a.Pensar ? "true" : "false").Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        HttpWebRequest Armar(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = Math.Min(timeoutMs, 120000);        // esto es solo hasta las cabeceras
            req.ReadWriteTimeout = timeoutMs;                 // esto sí cubre la espera entre tokens
            req.Proxy = null;                                 // 🚨 el proxy corporativo se come el localhost
            req.KeepAlive = true;
            req.ServicePoint.Expect100Continue = false;
            req.AutomaticDecompression = DecompressionMethods.None;
            req.UserAgent = "capcom/1.0";
            return req;
        }

        string Pedir(string url, string cuerpo, int timeoutMs)
        {
            var req = Armar(url, timeoutMs);
            if (cuerpo != null)
            {
                req.Method = "POST";
                req.ContentType = "application/json";
                var datos = new UTF8Encoding(false).GetBytes(cuerpo);
                req.ContentLength = datos.Length;
                using (var s = req.GetRequestStream()) s.Write(datos, 0, datos.Length);
            }
            using (var r = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>Cuenta los tokens del hilo con el tokenizador REAL del servidor. Si no se puede, estima.</summary>
        public int ContarContexto(string sistema, IEnumerable<Mensaje> historia)
        {
            var texto = new StringBuilder(sistema ?? "");
            foreach (var m in historia) { texto.Append('\n').Append(m.Texto); }
            try
            {
                string resp = Pedir(Url.TrimEnd('/') + "/tokenize", "{\"content\":" + Json.Cita(texto.ToString()) + "}", 4000);
                var d = Json.Parsear(resp);
                if (d != null && d.TryGetValue("tokens", out var t) && t is System.Collections.IEnumerable en && !(t is string))
                    return en.Cast<object>().Count();
            }
            catch { }
            return Estimar(texto.ToString());
        }
    }
}
