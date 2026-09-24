using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Capcom
{
    /// <summary>Un .gguf encontrado en el disco, con lo que se puede saber de él sin cargarlo.</summary>
    internal sealed class ModeloArchivo
    {
        public string Ruta = "";
        public string Nombre = "";
        public string Carpeta = "";
        public double Gb;
        public string Cuant = "";
        public string Familia = "";
        public bool EsProyector;          // mmproj: no se carga solo, acompaña a otro
        public DateTime Fecha;

        public string Etiqueta => Nombre + (Cuant.Length > 0 ? "  " + Cuant : "");

        /// <summary>RAM que hace falta de verdad: los pesos más el estado y el contexto. Del lado seguro.</summary>
        public double GbNecesarios(int contexto) => Gb * 1.08 + 0.35 + contexto / 1024.0 * 0.14;
    }

    /// <summary>
    /// Descubre los modelos del disco y sabe levantar y bajar el llama-server.
    ///
    /// 🚨 Sólo se mata lo que esta app levantó. Si hay un servidor ajeno escuchando (lo típico acá: la tarea
    /// programada que repone el 27B), se avisa y se deja en paz — matar procesos de otro es la clase de atajo
    /// que después cuesta una tarde.
    /// </summary>
    internal sealed class Servidor
    {
        readonly Config cfg;
        readonly Logger log;
        Process proceso;
        readonly List<string> salida = new List<string>();
        readonly object candado = new object();

        public event Action<string> Linea;          // cada renglón del log del servidor
        public bool Nuestro => proceso != null && !proceso.HasExited;
        public int Pid => proceso != null && !proceso.HasExited ? proceso.Id : 0;
        public ModeloArchivo Cargado;
        public DateTime Desde;

        public Servidor(Config c, Logger l) { cfg = c; log = l; }

        // ------------------------------------------------------------------ inventario

        public List<ModeloArchivo> Inventario()
        {
            var res = new List<ModeloArchivo>();
            foreach (var carpeta in cfg.CarpetasModelos.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(carpeta) || !Directory.Exists(carpeta)) continue;
                try
                {
                    foreach (var f in Directory.GetFiles(carpeta, "*.gguf", SearchOption.AllDirectories))
                    {
                        // 🚨 el filtro por patrón matchea también los ".gguf.partial" por los nombres 8.3
                        if (!f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                        var fi = new FileInfo(f);
                        string nombre = Path.GetFileNameWithoutExtension(f);
                        // un modelo partido (`-00001-of-00003`) se levanta por la parte 1 y llama.cpp busca las otras
                        // al lado: las demás no son modelos, y lo que cuenta para la RAM es lo que pesan todas juntas
                        int parte, de;
                        Descargas.Parte(nombre, out parte, out de);
                        if (de > 1 && parte != 1) continue;
                        res.Add(new ModeloArchivo
                        {
                            Ruta = f,
                            Nombre = nombre,
                            Carpeta = Path.GetFileName(Path.GetDirectoryName(f)),
                            Gb = (de > 1 ? PesoPartido(f, de) : fi.Length) / 1073741824.0,
                            Cuant = Cuantizacion(nombre),
                            Familia = Familia(nombre),
                            EsProyector = nombre.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0,
                            Fecha = fi.LastWriteTime,
                        });
                    }
                }
                catch (Exception ex) { log?.Aviso("No pude leer " + carpeta + ": " + ex.Message); }
            }
            return res.OrderBy(m => m.EsProyector).ThenBy(m => m.Gb).ToList();
        }

        /// <summary>Lo que pesan juntas las partes de un modelo partido que estén al lado de la primera.</summary>
        static long PesoPartido(string primera, int de)
        {
            string carpeta = Path.GetDirectoryName(primera);
            string raiz = Descargas.SinParte(Path.GetFileNameWithoutExtension(primera));
            long total = 0;
            for (int k = 1; k <= de; k++)
            {
                var fi = new FileInfo(Path.Combine(carpeta, raiz + "-" + k.ToString("00000") + "-of-" + de.ToString("00000") + ".gguf"));
                if (fi.Exists) total += fi.Length;
            }
            return total;
        }

        static string Cuantizacion(string n)
        {
            foreach (var q in new[] { "Q8_K_XL", "Q8_0", "Q6_K", "Q5_K_XL", "Q5_K_M", "Q5_K_S", "Q4_K_XL", "Q4_K_M", "Q4_K_S", "IQ4_XS", "IQ3_S", "IQ3_XXS", "IQ2_M", "Q3_K_M", "Q2_K_XL", "Q2_K", "F16", "BF16", "F32" })
                if (n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return q.ToUpperInvariant();
            return "";
        }

        static string Familia(string n)
        {
            string l = n.ToLowerInvariant();
            if (l.StartsWith("qwen")) return "Qwen";
            if (l.StartsWith("gemma")) return "Gemma";
            if (l.StartsWith("llama")) return "Llama";
            if (l.StartsWith("phi")) return "Phi";
            if (l.StartsWith("lfm")) return "LFM";
            if (l.StartsWith("smol")) return "SmolLM";
            if (l.StartsWith("mistral")) return "Mistral";
            return "";
        }

        /// <summary>¿Hay algún llama-server corriendo que no sea nuestro? (la tarea programada repone uno solo)</summary>
        public List<int> Ajenos()
        {
            var res = new List<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName("llama-server"))
                {
                    if (proceso == null || p.Id != proceso.Id) res.Add(p.Id);
                    p.Dispose();
                }
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------------ levantar

        /// <summary>Puerto en el que levanta ESTE servidor. El banco de pruebas usa uno aparte para no pisar el chat.</summary>
        public int Puerto;

        public string[] Argumentos(ModeloArchivo m)
        {
            var a = new List<string>
            {
                "-m", m.Ruta,
                "--host", "127.0.0.1",
                "--port", (Puerto > 0 ? Puerto : cfg.Puerto).ToString(),
                "-c", cfg.Contexto.ToString(),
                "-t", cfg.Hilos.ToString(),
                "-np", "1",
                "--no-warmup",
                "--jinja",
                "--metrics",
            };
            if (!string.IsNullOrWhiteSpace(cfg.FlagsExtra)) a.AddRange(PartirArgs(cfg.FlagsExtra));
            return a.ToArray();
        }

        static IEnumerable<string> PartirArgs(string s)
        {
            var sb = new StringBuilder();
            bool comillas = false;
            foreach (char c in s)
            {
                if (c == '"') { comillas = !comillas; continue; }
                if (c == ' ' && !comillas) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        static string Citar(string a) => string.IsNullOrEmpty(a) ? "\"\"" : a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a;

        public string Comando(ModeloArchivo m) =>
            Citar(cfg.LlamaServer) + " " + string.Join(" ", Argumentos(m).Select(Citar));

        /// <summary>
        /// Levanta el servidor y espera a que conteste. Bloquea: llamalo desde un hilo de fondo.
        /// `avisar` recibe el progreso en castellano para mostrarlo en la UI.
        /// </summary>
        public bool Levantar(ModeloArchivo m, Action<string> avisar, out string detalle)
        {
            detalle = "";
            if (m == null) { detalle = "no elegiste ningún modelo"; return false; }
            if (m.EsProyector) { detalle = "ese archivo es un proyector de visión (mmproj), no un modelo"; return false; }
            if (!File.Exists(cfg.LlamaServer)) { detalle = "no encuentro llama-server en " + cfg.LlamaServer; return false; }
            if (!File.Exists(m.Ruta)) { detalle = "el archivo del modelo ya no está"; return false; }

            double libre, total;
            Win32.Memoria(out libre, out total);
            double falta = m.GbNecesarios(cfg.Contexto);
            if (libre < falta)
            {
                detalle = $"no entra: hacen falta ~{falta:0.0} GB y hay {libre:0.0} GB libres";
                return false;
            }

            // 🚨🚨 Si el puerto ya está ocupado, el sondeo de más abajo contesta NOMINAL en 0,3 s y damos por
            //    cargado un modelo que NO es el nuestro: el banco terminaría evaluando a gemma con las respuestas
            //    de otro. Pasó de verdad, con un llama-server huérfano de una corrida anterior. Se mira ANTES.
            int puerto = Puerto > 0 ? Puerto : cfg.Puerto;
            var previo = new Cliente(log) { Url = "http://127.0.0.1:" + puerto }.Sondear(1200);
            if (previo.Señal == Señal.Nominal)
            {
                detalle = "el puerto " + puerto + " ya lo tiene ocupado «" + previo.ModeloCorto + "» (otro proceso): bajalo primero";
                return false;
            }

            lock (candado) salida.Clear();
            var psi = new ProcessStartInfo(cfg.LlamaServer)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(cfg.LlamaServer),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // 🚨 net48 no tiene ProcessStartInfo.ArgumentList: la línea se arma a mano, con comillas
                //    en todo lo que tenga espacios (las rutas de los modelos los tienen seguido).
                Arguments = string.Join(" ", Argumentos(m).Select(Citar)),
            };

            try
            {
                proceso = new Process { StartInfo = psi, EnableRaisingEvents = true };
                // 🚨 hay que DRENAR las dos tuberías: si se llenan, el servidor se bloquea escribiendo su propio log
                proceso.OutputDataReceived += (s, e) => Anotar(e.Data);
                proceso.ErrorDataReceived += (s, e) => Anotar(e.Data);
                proceso.Start();
                proceso.BeginOutputReadLine();
                proceso.BeginErrorReadLine();
            }
            catch (Exception ex) { detalle = "no pude arrancarlo: " + ex.Message; proceso = null; return false; }

            log?.Info($"Levantando {m.Nombre} ({m.Gb:0.0} GB) en el puerto {(Puerto > 0 ? Puerto : cfg.Puerto)} · pid {proceso.Id}");
            avisar?.Invoke("proceso arrancado · pid " + proceso.Id);

            var cli = new Cliente(log) { Url = "http://127.0.0.1:" + (Puerto > 0 ? Puerto : cfg.Puerto) };
            var reloj = Stopwatch.StartNew();
            int techoSeg = 600;
            while (reloj.Elapsed.TotalSeconds < techoSeg)
            {
                if (proceso.HasExited)
                {
                    detalle = "el servidor se cerró solo (código " + proceso.ExitCode + ") · " + UltimaLineaUtil();
                    proceso = null;
                    return false;
                }
                double l2, t2;
                Win32.Memoria(out l2, out t2);
                if (l2 < 0.9)
                {
                    detalle = "la RAM libre bajó de 0,9 GB mientras cargaba: lo bajo antes de que se cuelgue la máquina";
                    Bajar();
                    return false;
                }
                var est = cli.Sondear(1500);
                if (est.Señal == Señal.Nominal)
                {
                    // y por las dudas, que el que contesta sea EL QUE LEVANTAMOS
                    string suyo = Path.GetFileNameWithoutExtension(est.Modelo ?? "");
                    if (suyo.Length > 0 && !string.Equals(suyo, m.Nombre, StringComparison.OrdinalIgnoreCase))
                    {
                        detalle = "el que contesta en el puerto " + puerto + " es «" + suyo + "», no «" + m.Nombre + "»";
                        log?.Error("Servidor equivocado · " + detalle);
                        Bajar();
                        return false;
                    }
                    Cargado = m;
                    Desde = DateTime.Now;
                    detalle = $"listo en {reloj.Elapsed.TotalSeconds:0.0} s · {est.ModeloCorto}";
                    log?.Ok("Servidor NOMINAL · " + detalle);
                    return true;
                }
                avisar?.Invoke(Progreso(reloj.Elapsed));
                Thread.Sleep(700);
            }
            detalle = "no contestó en " + techoSeg + " s";
            Bajar();
            return false;
        }

        string Progreso(TimeSpan t)
        {
            string ult = UltimaLineaUtil();
            return $"cargando… {t.TotalSeconds:0} s" + (ult.Length > 0 ? " · " + ult : "");
        }

        void Anotar(string l)
        {
            if (string.IsNullOrWhiteSpace(l)) return;
            lock (candado)
            {
                salida.Add(l);
                while (salida.Count > 400) salida.RemoveAt(0);
            }
            try { Linea?.Invoke(l); } catch { }
        }

        public string[] Salida() { lock (candado) return salida.ToArray(); }

        string UltimaLineaUtil()
        {
            lock (candado)
            {
                for (int i = salida.Count - 1; i >= 0 && i > salida.Count - 25; i--)
                {
                    var l = salida[i].Trim();
                    if (l.Length < 6) continue;
                    if (l.StartsWith("ggml_") || l.StartsWith("llama_model_loader: - ")) continue;
                    return l.Length > 90 ? l.Substring(0, 89) + "…" : l;
                }
            }
            return "";
        }

        /// <summary>Baja SOLO el proceso que levantamos nosotros.</summary>
        public bool Bajar()
        {
            var p = proceso;
            proceso = null;
            Cargado = null;
            if (p == null) return false;
            try
            {
                if (!p.HasExited)
                {
                    log?.Info("Bajando el servidor · pid " + p.Id);
                    p.Kill();
                    p.WaitForExit(6000);
                }
                return true;
            }
            catch (Exception ex) { log?.Error("No pude bajar el servidor: " + ex.Message); return false; }
            finally { try { p.Dispose(); } catch { } }
        }
    }
}
