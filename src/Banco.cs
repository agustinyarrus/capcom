using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Capcom
{
    /// <summary>
    /// El banco de pruebas: levanta cada modelo elegido, le toma el examen, lo puntúa y lo baja. Uno por vez.
    ///
    /// 🚨 Usa un PUERTO APARTE (`Puerto + 1`): así el modelo con el que estás conversando no se cae mientras se
    /// evalúan otros. Y antes de cada uno mira la RAM: cargar algo que no entra deja la máquina inutilizable
    /// durante minutos, así que se saltea con el motivo escrito en vez de intentarlo igual.
    ///
    /// Todo corre en un hilo de fondo y se puede cortar; lo que ya se midió queda guardado.
    /// </summary>
    internal sealed class Banco
    {
        readonly Config cfg;
        readonly Logger log;
        readonly Servidor srv;
        readonly string carpeta;
        Thread hilo;
        volatile bool cortar;

        public bool Corriendo { get; private set; }
        public string Fase = "";
        public string ModeloActual = "";
        public int Hecho, Cuantos, PreguntaActual, PreguntasTotal;
        public List<ResultadoExamen> Resultados = new List<ResultadoExamen>();

        public event Action Cambio;
        public event Action<ResultadoExamen> Termino;      // uno terminado
        public event Action Fin;                            // todos terminados

        public Banco(Config c, Logger l, string carpetaDatos)
        {
            cfg = c; log = l;
            carpeta = Path.Combine(carpetaDatos, "examenes");
            try { Directory.CreateDirectory(carpeta); } catch { }
            srv = new Servidor(c, l) { Puerto = c.Puerto + 1 };
            Cargar();
        }

        void Avisar() { try { Cambio?.Invoke(); } catch { } }

        // ------------------------------------------------------------------ persistencia

        /// <summary>Los resultados viven en disco: el ranking sobrevive a cerrar la app.</summary>
        public void Cargar()
        {
            Resultados.Clear();
            try
            {
                foreach (var f in Directory.GetFiles(carpeta, "*.json"))
                {
                    var d = Json.LeerObjeto(f);
                    if (d == null) continue;
                    Resultados.Add(ResultadoExamen.DeJson(d));
                }
            }
            catch (Exception ex) { log?.Aviso("No pude leer los exámenes: " + ex.Message); }
            Ordenar();
        }

        public void Ordenar() =>
            Resultados = Resultados
                .OrderByDescending(r => r.Problema.Length == 0)
                .ThenByDescending(r => r.Puntaje)
                .ThenBy(r => r.SegMediana)
                .ToList();

        void Guardar(ResultadoExamen r)
        {
            try
            {
                string limpio = r.Modelo;
                foreach (var c in Path.GetInvalidFileNameChars()) limpio = limpio.Replace(c, '-');
                Json.Escribir(Path.Combine(carpeta, limpio + ".json"), r.AJson());
            }
            catch (Exception ex) { log?.Error("No pude guardar el examen: " + ex.Message); }
        }

        public void Borrar(ResultadoExamen r)
        {
            if (r == null) return;
            try
            {
                string limpio = r.Modelo;
                foreach (var c in Path.GetInvalidFileNameChars()) limpio = limpio.Replace(c, '-');
                var f = Path.Combine(carpeta, limpio + ".json");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }
            Resultados.Remove(r);
            Avisar();
        }

        public ResultadoExamen DeModelo(string nombre) =>
            Resultados.FirstOrDefault(r => string.Equals(r.Modelo, nombre, StringComparison.OrdinalIgnoreCase));

        public string Carpeta => carpeta;

        // ------------------------------------------------------------------ correr

        public bool Correr(List<ModeloArchivo> modelos, out string porque)
        {
            porque = "";
            if (Corriendo) { porque = "ya hay un examen en curso"; return false; }
            var lista = (modelos ?? new List<ModeloArchivo>()).Where(m => m != null && !m.EsProyector).ToList();
            if (lista.Count == 0) { porque = "no elegiste ningún modelo (los mmproj no cuentan: son de visión)"; return false; }
            if (!File.Exists(cfg.LlamaServer)) { porque = "no encuentro llama-server en " + cfg.LlamaServer; return false; }

            cortar = false;
            Corriendo = true;
            Hecho = 0;
            Cuantos = lista.Count;
            PreguntasTotal = Examen.Preguntas.Count;
            PreguntaActual = 0;
            Fase = "arrancando";
            ModeloActual = "";
            Avisar();

            hilo = new Thread(() => Rutina(lista)) { IsBackground = true, Name = "capcom-banco" };
            hilo.Start();
            return true;
        }

        public void Cortar()
        {
            if (!Corriendo) return;
            cortar = true;
            Fase = "cortando";
            Avisar();
        }

        /// <summary>
        /// Baja lo que el banco haya dejado levantado. 🚨 Hay que llamarlo al cerrar la app: el servidor del
        /// banco es OTRO proceso distinto del que usa el chat, y si no se lo baja queda huérfano ocupando el
        /// puerto 8081 — y el próximo examen se lo encuentra ahí.
        /// </summary>
        public void Apagar()
        {
            cortar = true;
            try { srv.Bajar(); } catch { }
        }

        void Rutina(List<ModeloArchivo> lista)
        {
            log?.Info($"Banco de pruebas: {lista.Count} modelo/s · {Examen.Preguntas.Count} preguntas c/u · puerto {cfg.Puerto + 1}" +
                      (Node.Hay ? " · con Node para ejecutar el código" : " · sin Node: el código se corrige a ojo"));
            try
            {
                foreach (var m in lista)
                {
                    if (cortar) break;
                    ModeloActual = m.Nombre;
                    PreguntaActual = 0;
                    var r = Uno(m);
                    Resultados.RemoveAll(x => string.Equals(x.Modelo, r.Modelo, StringComparison.OrdinalIgnoreCase));
                    Resultados.Add(r);
                    Guardar(r);
                    Ordenar();
                    Hecho++;
                    try { Termino?.Invoke(r); } catch { }
                    Avisar();
                }
            }
            catch (Exception ex) { log?.Error("El banco se cayó: " + ex.Message); }
            finally
            {
                srv.Bajar();
                Corriendo = false;
                Fase = "";
                ModeloActual = "";
                Avisar();
                try { Fin?.Invoke(); } catch { }
            }
        }

        ResultadoExamen Uno(ModeloArchivo m)
        {
            var res = new ResultadoExamen { Modelo = m.Nombre, Ruta = m.Ruta, Cuant = m.Cuant, Gb = m.Gb };
            double libre, total;
            Win32.Memoria(out libre, out total);
            double falta = m.GbNecesarios(cfg.Contexto);
            if (falta > libre)
            {
                res.Problema = $"no entra: pide ~{falta:0.0} GB y hay {libre:0.0} GB";
                log?.Aviso($"Banco: salteo {m.Nombre} · {res.Problema}");
                return res;
            }

            var reloj = Stopwatch.StartNew();
            Fase = "cargando el modelo";
            Avisar();
            string detalle;
            if (!srv.Levantar(m, p => { Fase = p; Avisar(); }, out detalle))
            {
                res.Problema = "no pude levantarlo: " + detalle;
                log?.Error($"Banco: {m.Nombre} · {res.Problema}");
                srv.Bajar();
                return res;
            }
            res.MsCarga = reloj.ElapsedMilliseconds;

            var cli = new Cliente(log) { Url = "http://127.0.0.1:" + (cfg.Puerto + 1) };
            try
            {
                foreach (var p in Examen.Preguntas)
                {
                    if (cortar) break;
                    PreguntaActual++;
                    Fase = p.Categoria + " · " + p.Clave;
                    Avisar();
                    res.Respuestas.Add(Preguntar(cli, p));
                }
            }
            finally { srv.Bajar(); }

            if (res.Respuestas.Count > 0)
                log?.Ok($"Banco: {m.Nombre} → {res.Puntaje:0.0} puntos · nivel {res.Nivel} · {res.SegMediana:0.0} s de mediana · {res.Veredicto}");
            return res;
        }

        Respuesta Preguntar(Cliente cli, Pregunta p)
        {
            var r = new Respuesta { Clave = p.Clave, Categoria = p.Categoria };
            var hist = new List<Mensaje> { new Mensaje { Rol = Rol.Usuario, Texto = p.Texto } };
            var a = new Ajustes
            {
                // el examen se toma con la misma mano para todos: temperatura baja y nada de pensar en voz alta
                Temperatura = p.Temp, TopP = 0.9, TopK = 40, MaxTokens = p.MaxTokens, Presencia = 0, Repeticion = 1.05, Pensar = false,
            };
            string sistema = p.Sistema.Length > 0 ? p.Sistema : Examen.Sistema;
            var salida = cli.Generar(sistema, hist, a, null, null, 180000);

            r.Texto = (salida.Texto ?? "").Trim();
            r.Ms = salida.Ms;
            r.MsPrimerToken = salida.MsPrimerToken;
            r.Tokens = salida.Tokens;
            r.TokPorSeg = salida.TokPorSeg;
            r.Vacia = r.Texto.Length == 0;

            if (r.Vacia)
            {
                r.Ok = false;
                r.Parcial = 0;
                r.Porque = salida.Error.Length > 0 ? salida.Error : "no contestó nada";
                return r;
            }
            try
            {
                var v = p.Corregir(r.Texto);
                r.Ok = v.Ok;
                r.Parcial = v.Parcial;
                r.Porque = v.Porque;
            }
            catch (Exception ex) { r.Ok = false; r.Parcial = 0; r.Porque = "el corrector falló: " + ex.Message; }
            return r;
        }

        // ------------------------------------------------------------------ informe

        /// <summary>Arma el ranking en Markdown, listo para pegar donde sea.</summary>
        public string Informe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Banco de pruebas de modelos · CAPCOM").AppendLine();
            sb.AppendLine("- " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            sb.AppendLine("- " + Examen.Preguntas.Count + " preguntas · " + Examen.Categorias.Length + " categorías · contexto " + cfg.Contexto.ToString("N0"));
            sb.AppendLine("- " + (Node.Hay ? "el código se **ejecuta** en Node contra casos ocultos" : "sin Node: el código se corrigió a ojo"));
            sb.AppendLine().AppendLine("| # | modelo | nivel | puntaje | mediana | tok/s | carga | veredicto |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            int i = 1;
            foreach (var r in Resultados)
            {
                sb.AppendLine($"| {i++} | `{r.Modelo}` | **{r.Nivel}** | {r.Puntaje:0.0} | {r.SegMediana:0.0} s | {r.TokPorSeg:0.0} | {r.MsCarga / 1000.0:0.0} s | {r.Veredicto} |");
            }
            sb.AppendLine().AppendLine("## Por categoría").AppendLine();
            var cats = Examen.Categorias;
            sb.Append("| modelo |");
            foreach (var c in cats) sb.Append(' ').Append(c).Append(" |");
            sb.AppendLine();
            sb.Append("| --- |");
            foreach (var c in cats) sb.Append(" --- |");
            sb.AppendLine();
            foreach (var r in Resultados.Where(x => x.Problema.Length == 0))
            {
                sb.Append("| `").Append(r.Modelo).Append("` |");
                foreach (var c in cats)
                {
                    double v = r.PorCategoria(c);
                    sb.Append(' ').Append(double.IsNaN(v) ? "—" : v.ToString("0")).Append(" |");
                }
                sb.AppendLine();
            }
            var peor = Resultados.Where(x => x.Problema.Length == 0).SelectMany(x => x.Respuestas.Where(y => !y.Ok).Select(y => new { x.Modelo, y }))
                                 .OrderBy(x => x.y.Clave).Take(25).ToList();
            if (peor.Count > 0)
            {
                sb.AppendLine().AppendLine("## Lo que falló").AppendLine();
                foreach (var x in peor) sb.AppendLine("- `" + x.Modelo + "` · **" + x.y.Clave + "** (" + x.y.Categoria + "): " + (x.y.Porque.Length > 0 ? x.y.Porque : "no pasó"));
            }
            return sb.ToString();
        }
    }
}
