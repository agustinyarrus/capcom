using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Capcom
{
    internal enum Rol { Usuario, Asistente, Sistema }

    /// <summary>Un mensaje del hilo. Lleva su propia telemetría: cuánto tardó, cuántos tokens y a qué velocidad.</summary>
    internal sealed class Mensaje
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 12);
        public Rol Rol = Rol.Usuario;
        public string Texto = "";
        public DateTime Hora = DateTime.Now;
        public string Modelo = "";
        public string Persona = "";
        public int Tokens;
        public int TokensPrompt;
        public long Ms;
        public long MsPrimerToken;          // latencia hasta la primera palabra: lo que se siente
        public double TokPorSeg;
        public string Pensamiento = "";     // reasoning_content, si el modelo piensa
        public bool Error;
        public bool Cancelado;
        public bool Motor;                  // lo contestó el motor de a bordo, no el modelo

        /// <summary>Versión del texto: la usa la caché de maquetado para saber si tiene que re-medir.</summary>
        public int Version;
        public void Cambio() { Version++; }

        public Dictionary<string, object> AJson() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["rol"] = Rol.ToString().ToLowerInvariant(),
            ["texto"] = Texto,
            ["hora"] = Hora,
            ["modelo"] = Modelo,
            ["persona"] = Persona,
            ["tokens"] = Tokens,
            ["tokensPrompt"] = TokensPrompt,
            ["ms"] = (int)Ms,
            ["msPrimerToken"] = (int)MsPrimerToken,
            ["tokPorSeg"] = Math.Round(TokPorSeg, 3),
            ["pensamiento"] = Pensamiento,
            ["error"] = Error,
            ["cancelado"] = Cancelado,
            ["motor"] = Motor,
        };

        public static Mensaje DeJson(Dictionary<string, object> d)
        {
            var m = new Mensaje
            {
                Id = Json.S(d, "id", Guid.NewGuid().ToString("N").Substring(0, 12)),
                Texto = Json.S(d, "texto"),
                Hora = Json.F(d, "hora") ?? DateTime.Now,
                Modelo = Json.S(d, "modelo"),
                Persona = Json.S(d, "persona"),
                Tokens = Json.I(d, "tokens"),
                TokensPrompt = Json.I(d, "tokensPrompt"),
                Ms = Json.I(d, "ms"),
                MsPrimerToken = Json.I(d, "msPrimerToken"),
                TokPorSeg = Json.D(d, "tokPorSeg"),
                Pensamiento = Json.S(d, "pensamiento"),
                Error = Json.B(d, "error"),
                Cancelado = Json.B(d, "cancelado"),
                Motor = Json.B(d, "motor"),
            };
            switch (Json.S(d, "rol", "usuario"))
            {
                case "asistente": m.Rol = Rol.Asistente; break;
                case "sistema": m.Rol = Rol.Sistema; break;
                default: m.Rol = Rol.Usuario; break;
            }
            return m;
        }
    }

    /// <summary>Una transmisión: el hilo completo con su título, su persona y su historia.</summary>
    internal sealed class Conversacion
    {
        public string Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        public string Titulo = "";
        public DateTime Creada = DateTime.Now;
        public DateTime Tocada = DateTime.Now;
        public string Persona = "capcom";
        public string Modelo = "";
        public bool Fijada;
        public List<Mensaje> Mensajes = new List<Mensaje>();

        public int Palabras => Mensajes.Sum(m => Contar(m.Texto));
        public int TokensTotales => Mensajes.Sum(m => m.Tokens);
        static int Contar(string s) => string.IsNullOrWhiteSpace(s) ? 0 : s.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>El título sale del primer mensaje del user, recortado en una frontera de palabra.</summary>
        public void TitularSiHaceFalta()
        {
            if (!string.IsNullOrWhiteSpace(Titulo)) return;
            var primero = Mensajes.FirstOrDefault(m => m.Rol == Rol.Usuario && m.Texto.Trim().Length > 0);
            if (primero == null) return;
            string t = primero.Texto.Trim().Replace('\n', ' ').Replace('\r', ' ');
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            if (t.Length > 52)
            {
                int corte = t.LastIndexOf(' ', Math.Min(52, t.Length - 1));
                t = (corte > 18 ? t.Substring(0, corte) : t.Substring(0, 52)).TrimEnd(',', '.', ';', ':') + "…";
            }
            Titulo = t;
        }

        public string NombreVisible => string.IsNullOrWhiteSpace(Titulo) ? "transmisión nueva" : Titulo;

        public Dictionary<string, object> AJson() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["titulo"] = Titulo,
            ["creada"] = Creada,
            ["tocada"] = Tocada,
            ["persona"] = Persona,
            ["modelo"] = Modelo,
            ["fijada"] = Fijada,
            ["mensajes"] = Mensajes.Select(m => m.AJson()).ToList(),
        };

        public static Conversacion DeJson(Dictionary<string, object> d)
        {
            var c = new Conversacion
            {
                Id = Json.S(d, "id", Guid.NewGuid().ToString("N")),
                Titulo = Json.S(d, "titulo"),
                Creada = Json.F(d, "creada") ?? DateTime.Now,
                Tocada = Json.F(d, "tocada") ?? DateTime.Now,
                Persona = Json.S(d, "persona", "capcom"),
                Modelo = Json.S(d, "modelo"),
                Fijada = Json.B(d, "fijada"),
            };
            foreach (var o in Json.Lista(d, "mensajes")) c.Mensajes.Add(Mensaje.DeJson(o));
            return c;
        }
    }

    /// <summary>
    /// El archivo de transmisiones: una conversación = un .json en `datos\transmisiones\`. Sin índice aparte,
    /// porque un índice que se desincroniza es peor que leer veinte archivos chicos al arrancar.
    /// </summary>
    internal sealed class Archivo
    {
        readonly string carpeta;
        readonly Logger log;
        public List<Conversacion> Lista = new List<Conversacion>();

        public Archivo(string carpetaDatos, Logger l)
        {
            log = l;
            carpeta = Path.Combine(carpetaDatos, "transmisiones");
            try { Directory.CreateDirectory(carpeta); } catch { }
            Cargar();
        }

        public void Cargar()
        {
            Lista.Clear();
            try
            {
                foreach (var f in Directory.GetFiles(carpeta, "*.json").OrderByDescending(File.GetLastWriteTime))
                {
                    var d = Json.LeerObjeto(f);
                    if (d == null) continue;
                    var c = Conversacion.DeJson(d);
                    if (c.Mensajes.Count > 0 || !string.IsNullOrWhiteSpace(c.Titulo)) Lista.Add(c);
                }
            }
            catch (Exception ex) { log?.Error("No pude leer el archivo de transmisiones: " + ex.Message); }
            Ordenar();
        }

        public void Ordenar() => Lista = Lista.OrderByDescending(c => c.Fijada).ThenByDescending(c => c.Tocada).ToList();

        public void Guardar(Conversacion c)
        {
            if (c == null) return;
            try { Json.Escribir(Path.Combine(carpeta, c.Id + ".json"), c.AJson()); }
            catch (Exception ex) { log?.Error("No pude guardar la transmisión: " + ex.Message); }
        }

        public void Borrar(Conversacion c)
        {
            if (c == null) return;
            try { var f = Path.Combine(carpeta, c.Id + ".json"); if (File.Exists(f)) File.Delete(f); } catch { }
            Lista.Remove(c);
        }

        public Conversacion Nueva(string persona)
        {
            var c = new Conversacion { Persona = persona };
            Lista.Insert(0, c);
            return c;
        }

        /// <summary>Busca en todos los hilos. Devuelve la conversación y el mensaje donde pegó.</summary>
        public List<KeyValuePair<Conversacion, Mensaje>> Buscar(string q, int tope = 120)
        {
            var res = new List<KeyValuePair<Conversacion, Mensaje>>();
            if (string.IsNullOrWhiteSpace(q)) return res;
            foreach (var c in Lista)
                foreach (var m in c.Mensajes)
                {
                    if (m.Texto.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        res.Add(new KeyValuePair<Conversacion, Mensaje>(c, m));
                        if (res.Count >= tope) return res;
                    }
                }
            return res;
        }

        public string Carpeta => carpeta;
    }
}
