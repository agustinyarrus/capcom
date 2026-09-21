using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Capcom
{
    /// <summary>Un repo de HuggingFace del catálogo, con por qué está en la lista.</summary>
    internal sealed class Repo
    {
        public string Nombre = "", Ruta = "", Nota = "";
        public string Prefiere = "";        // la cuantización que conviene bajar primero
        public double GbAprox;
    }

    /// <summary>Un archivo .gguf disponible en un repo, tal como lo lista la API.</summary>
    internal sealed class ArchivoRemoto
    {
        public string Repo = "", Ruta = "", Sha256 = "";
        public long Bytes;
        public string Nombre => Path.GetFileName(Ruta);
        public double Gb => Bytes / 1073741824.0;
        public string Cuant
        {
            get
            {
                foreach (var q in new[] { "Q8_K_XL", "Q8_0", "Q6_K", "Q5_K_XL", "Q5_K_M", "Q4_K_XL", "Q4_K_M", "Q4_K_S", "IQ4_XS", "IQ3_S", "Q3_K_M", "Q2_K", "F16", "BF16" })
                    if (Nombre.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return q.ToUpperInvariant();
                return "";
            }
        }
        public string Url => "https://huggingface.co/" + Repo + "/resolve/main/" + Ruta.Replace(" ", "%20") + "?download=true";
    }

    /// <summary>
    /// Bajar modelos de HuggingFace, con reanudación.
    ///
    /// 🚨🚨 En muchas redes la descarga SE CORTA: el gateway estrangula y las conexiones largas se caen a la mitad.
    /// Por eso el archivo se baja a `.partial` y cada reintento manda `Range: bytes=<lo que ya tengo>-`. Un
    /// descargador sin reanudación no termina nunca un archivo de 2 GB.
    ///
    /// 🚨 El proxy: `Cliente` anula `WebRequest.DefaultWebProxy` para todo el proceso (sin eso el primer pedido
    /// a 127.0.0.1 se cuelga en el WPAD). Para salir a internet hay que volver a ponerlo A MANO en el pedido:
    /// se intenta directo y, si falla la conexión, se reintenta con el proxy del sistema.
    /// </summary>
    internal sealed class Descargas
    {
        readonly Logger log;
        Thread hilo;
        volatile bool cortar;

        public bool Bajando { get; private set; }
        public ArchivoRemoto Actual;
        public long Hechos, Totales;
        public double BytesPorSeg;
        public string Estado = "";
        public string Ultimo = "";

        public event Action Cambio;
        public event Action<string> Listo;          // ruta del archivo terminado

        public Descargas(Logger l) { log = l; }

        void Avisar() { try { Cambio?.Invoke(); } catch { } }

        public double Fraccion => Totales > 0 ? Math.Max(0, Math.Min(1, Hechos / (double)Totales)) : 0;
        public TimeSpan Falta => BytesPorSeg > 1024 && Totales > Hechos
            ? TimeSpan.FromSeconds((Totales - Hechos) / BytesPorSeg) : TimeSpan.Zero;

        // ------------------------------------------------------------------ catálogo

        /// <summary>
        /// Modelos chicos que tienen sentido en una notebook común (4 núcleos físicos, sin GPU útil para esto).
        /// No están por fama: están porque entran en RAM y contestan en segundos, no en minutos.
        /// </summary>
        public static readonly List<Repo> Catalogo = new List<Repo>
        {
            new Repo { Nombre = "Qwen3.5-1.5B", Ruta = "Qwen/Qwen2.5-1.5B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 1.0,
                       Nota = "chico y correcto en castellano; buen punto de partida" },
            new Repo { Nombre = "Qwen3.5-3B", Ruta = "Qwen/Qwen2.5-3B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 2.0,
                       Nota = "el salto de calidad más barato en RAM" },
            new Repo { Nombre = "Qwen3.5-7B", Ruta = "Qwen/Qwen2.5-7B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 4.7,
                       Nota = "escribe notablemente mejor; ya se siente lento para chatear" },
            new Repo { Nombre = "Qwen3.5-Coder-7B", Ruta = "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 4.7,
                       Nota = "para el examen de código: es lo suyo" },
            new Repo { Nombre = "Llama-3.2-3B", Ruta = "bartowski/Llama-3.2-3B-Instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 2.0,
                       Nota = "ojo: en las pruebas viejas contestaba en inglés" },
            new Repo { Nombre = "Phi-3.5-mini", Ruta = "bartowski/Phi-3.5-mini-instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 2.4,
                       Nota = "razona bien para lo que pesa; flojo en castellano" },
            new Repo { Nombre = "Mistral-7B-v0.3", Ruta = "bartowski/Mistral-7B-Instruct-v0.3-GGUF", Prefiere = "Q4_K_M", GbAprox = 4.4,
                       Nota = "clásico, sólido, sin sorpresas" },
            new Repo { Nombre = "granite-3.1-2b", Ruta = "bartowski/granite-3.1-2b-instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 1.6,
                       Nota = "de IBM, entrenado fuerte en instrucciones" },
            new Repo { Nombre = "SmolLM2-1.7B", Ruta = "bartowski/SmolLM2-1.7B-Instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 1.1,
                       Nota = "el más liviano que todavía sirve para algo" },
            new Repo { Nombre = "gemma-2-2b", Ruta = "bartowski/gemma-2-2b-it-GGUF", Prefiere = "Q4_K_M", GbAprox = 1.7,
                       Nota = "hermano chico del gemma que ya usás" },
        };

        /// <summary>Le pregunta a la API qué .gguf tiene el repo. Anónimo: `?blobs=true` pide token, el tree no.</summary>
        public List<ArchivoRemoto> Listar(Repo r, out string porque)
        {
            porque = "";
            var res = new List<ArchivoRemoto>();
            try
            {
                string url = "https://huggingface.co/api/models/" + r.Ruta + "/tree/main?recursive=true";
                string cuerpo = Traer(url, 20000);
                foreach (var d in Json.ParsearLista(cuerpo))
                {
                    string ruta = Json.S(d, "path");
                    if (!ruta.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                    var a = new ArchivoRemoto { Repo = r.Ruta, Ruta = ruta, Bytes = (long)Json.D(d, "size") };
                    if (d.TryGetValue("lfs", out var lfs) && lfs is Dictionary<string, object> ld)
                    {
                        // ⭐ para un archivo LFS, `oid` ES el sha256: alcanza para verificar sin pedir nada más
                        a.Sha256 = Json.S(ld, "oid");
                        long t = (long)Json.D(ld, "size");
                        if (t > 0) a.Bytes = t;
                    }
                    res.Add(a);
                }
                if (res.Count == 0) porque = "el repo no tiene ningún .gguf";
            }
            catch (Exception ex) { porque = Corto(ex.Message); }
            return res.OrderBy(x => x.Bytes).ToList();
        }

        // ------------------------------------------------------------------ la bajada

        public bool Bajar(ArchivoRemoto a, string carpeta, out string porque)
        {
            porque = "";
            if (Bajando) { porque = "ya hay una descarga en curso"; return false; }
            if (a == null) { porque = "no elegiste ningún archivo"; return false; }
            if (string.IsNullOrWhiteSpace(carpeta)) { porque = "no hay carpeta de modelos configurada"; return false; }
            try { Directory.CreateDirectory(carpeta); }
            catch (Exception ex) { porque = "no puedo escribir en " + carpeta + ": " + ex.Message; return false; }

            string destino = Path.Combine(carpeta, a.Nombre);
            if (File.Exists(destino)) { porque = "ya lo tenés: " + destino; return false; }

            double libre = EspacioLibre(carpeta);
            if (libre > 0 && a.Gb > libre - 1)
            {
                porque = $"no entra en el disco: pide {a.Gb:0.0} GB y quedan {libre:0.0} GB";
                return false;
            }

            cortar = false;
            Bajando = true;
            Actual = a;
            Hechos = 0;
            Totales = a.Bytes;
            Estado = "conectando";
            Avisar();
            hilo = new Thread(() => Rutina(a, destino)) { IsBackground = true, Name = "capcom-descarga" };
            hilo.Start();
            return true;
        }

        public void Cortar()
        {
            if (!Bajando) return;
            cortar = true;
            Estado = "cortando";
            Avisar();
        }

        void Rutina(ArchivoRemoto a, string destino)
        {
            string parcial = destino + ".partial";
            int intento = 0;
            var reloj = Stopwatch.StartNew();
            try
            {
                while (intento < 40 && !cortar)
                {
                    intento++;
                    long desde = 0;
                    try { if (File.Exists(parcial)) desde = new FileInfo(parcial).Length; } catch { }
                    Hechos = desde;
                    if (a.Bytes > 0 && desde >= a.Bytes) break;
                    Estado = intento == 1 ? "bajando" : "reanudando (intento " + intento + ")";
                    Avisar();
                    try
                    {
                        if (Tramo(a, parcial, desde)) break;
                    }
                    catch (Exception ex)
                    {
                        log?.Aviso($"Descarga cortada en {Tema.Bytes(Hechos)}: {Corto(ex.Message)} · reintento {intento}");
                        Estado = "se cortó · " + Corto(ex.Message, 60);
                        Avisar();
                        Thread.Sleep(Math.Min(8000, 800 * intento));
                    }
                }

                if (cortar) { Estado = "cortada · lo bajado queda para seguir después"; log?.Info("Descarga cortada: " + a.Nombre); return; }

                long final = File.Exists(parcial) ? new FileInfo(parcial).Length : 0;
                if (a.Bytes > 0 && final != a.Bytes)
                {
                    Estado = $"quedó incompleta ({Tema.Bytes(final)} de {Tema.Bytes(a.Bytes)})";
                    log?.Error("Descarga incompleta: " + a.Nombre + " · " + Estado);
                    return;
                }

                if (a.Sha256.Length == 64)
                {
                    Estado = "verificando el SHA256";
                    Avisar();
                    string sha = Sha256(parcial);
                    if (!string.Equals(sha, a.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Estado = "el SHA256 no da: el archivo llegó corrupto";
                        log?.Error("SHA256 distinto en " + a.Nombre + " · esperaba " + a.Sha256.Substring(0, 12) + "… y vino " + sha.Substring(0, 12) + "…");
                        return;
                    }
                }

                File.Move(parcial, destino);
                reloj.Stop();
                Estado = $"listo en {reloj.Elapsed.TotalMinutes:0.0} min";
                Ultimo = destino;
                log?.Ok($"Bajado {a.Nombre} · {Tema.Bytes(a.Bytes)} en {reloj.Elapsed.TotalMinutes:0.0} min" +
                        (a.Sha256.Length == 64 ? " · SHA256 verificado" : ""));
                try { Listo?.Invoke(destino); } catch { }
            }
            catch (Exception ex) { Estado = "falló: " + Corto(ex.Message); log?.Error("Descarga fallida: " + ex.Message); }
            finally { Bajando = false; Actual = null; Avisar(); }
        }

        /// <summary>Un tramo de descarga. Devuelve true si llegó al final del archivo.</summary>
        bool Tramo(ArchivoRemoto a, string parcial, long desde)
        {
            var req = Armar(a.Url, 60000);
            if (desde > 0) req.AddRange(desde);
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                if (desde > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    // el servidor ignoró el Range: hay que empezar de cero para no pegar dos mitades distintas
                    log?.Aviso("El servidor no acepta reanudar: se baja de nuevo desde el principio");
                    try { File.Delete(parcial); } catch { }
                    desde = 0;
                    Hechos = 0;
                }
                long total = resp.ContentLength > 0 ? resp.ContentLength + desde : a.Bytes;
                if (total > 0) Totales = total;

                var buffer = new byte[128 * 1024];
                var reloj = Stopwatch.StartNew();
                long enEsteTramo = 0;
                using (var st = resp.GetResponseStream())
                using (var fs = new FileStream(parcial, desde > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                {
                    int n;
                    var ultimoAviso = Stopwatch.StartNew();
                    while ((n = st.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (cortar) { fs.Flush(); return false; }
                        fs.Write(buffer, 0, n);
                        Hechos += n;
                        enEsteTramo += n;
                        if (ultimoAviso.ElapsedMilliseconds > 250)
                        {
                            BytesPorSeg = enEsteTramo / Math.Max(0.001, reloj.Elapsed.TotalSeconds);
                            ultimoAviso.Restart();
                            Avisar();
                        }
                    }
                    fs.Flush();
                }
                return Totales <= 0 || Hechos >= Totales;
            }
        }

        // ------------------------------------------------------------------ plomería

        static double EspacioLibre(string carpeta)
        {
            try
            {
                var raiz = Path.GetPathRoot(Path.GetFullPath(carpeta));
                return new DriveInfo(raiz).AvailableFreeSpace / 1073741824.0;
            }
            catch { return 0; }
        }

        static string Sha256(string ruta)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
            using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return string.Concat(h.ComputeHash(fs).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// Pedido a internet. Primero directo; si la conexión falla, se reintenta con el proxy del sistema.
        /// (El proceso tiene el proxy por defecto anulado a propósito, por el localhost.)
        /// </summary>
        static HttpWebRequest Armar(string url, int timeoutMs, bool conProxy = false)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "capcom/1.0";
            req.KeepAlive = true;
            req.AllowAutoRedirect = true;
            req.Proxy = conProxy ? WebRequest.GetSystemWebProxy() : null;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            return req;
        }

        static string Traer(string url, int timeoutMs)
        {
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    var req = Armar(url, timeoutMs, i == 1);
                    using (var r = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (WebException ex) when (i == 0 &&
                    (ex.Status == WebExceptionStatus.ConnectFailure || ex.Status == WebExceptionStatus.NameResolutionFailure ||
                     ex.Status == WebExceptionStatus.ProxyNameResolutionFailure))
                {
                    // sin salida directa: probamos con el proxy del sistema
                }
            }
            throw new WebException("no pude llegar a " + url);
        }

        static string Corto(string s, int n = 90)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
