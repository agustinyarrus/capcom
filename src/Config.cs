using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Capcom
{
    /// <summary>
    /// config.json en `datos\`. Todo lo que el user puede tocar desde la pestaña AJUSTES vive acá, y se guarda
    /// solo cuando cambia algo (guardar en cada repintado desgastaría el disco sin motivo).
    /// </summary>
    internal sealed class Config
    {
        public string Ruta = "";

        // --- enlace con el servidor local
        public string ServidorUrl = "http://127.0.0.1:8080";
        public int Puerto = 8080;
        public string LlamaServer = BuscarLlamaServer();
        public List<string> CarpetasModelos = new List<string> { JuntoAlExe("modelos") };
        public string ModeloPreferido = "";
        public int Hilos = 4;
        public int Contexto = 8192;
        public string FlagsExtra = "";
        public bool LevantarSolo = false;      // ¿arrancar el servidor si no está? (nunca por sorpresa)

        // --- muestreo
        public double Temperatura = 0.7;
        public double TopP = 0.8;
        public int TopK = 20;
        public int MaxTokens = 512;
        public double Presencia = 0.0;
        public double Repeticion = 1.0;
        public bool Pensar = false;            // chat_template_kwargs.enable_thinking

        // --- persona
        public string PersonaActiva = "capcom";

        // --- interfaz
        public double EscalaFuente = 1.0;
        public double EscalaUI = 0.8;
        public string Acento = "malva";
        public int VentanaX, VentanaY, VentanaAncho, VentanaAlto;
        public bool RielIzquierdo = true;
        public bool RielDerecho = true;
        /// <summary>Ancho de cada riel en píxeles. 0 = que lo decida el contenido.</summary>
        public int AnchoIzq, AnchoDer;
        /// <summary>Qué porcentaje de la columna del medio ocupa la transcripción (40-100). Bien ancho por defecto.</summary>
        public int AnchoChat = 90;
        public bool Reticula = true;
        public bool Grano = true;

        // --- comportamiento
        public bool MinimizarABandeja = true;
        public bool CerrarABandeja = true;
        public bool IniciarMinimizado = false;
        public bool IniciarConWindows = false;
        public bool AtajoGlobal = true;
        public bool Sonido = true;
        public bool Quindar = true;            // el bip de radio de Apollo al abrir y cerrar la transmisión
        public bool Notificaciones = true;
        public bool EnterEnvia = true;
        public bool LogDebug = false;
        public bool GuardarHistorial = true;

        static string JuntoAlExe(string nombre)
        {
            try { return Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", nombre); }
            catch { return nombre; }
        }

        /// <summary>
        /// Valor de fábrica de la ruta del servidor: al lado del exe, en sus subcarpetas de siempre o en el PATH. Si no
        /// aparece, queda apuntando al lado del exe: el cartel de «no encuentro llama-server en …» dice dónde ponerlo.
        /// </summary>
        static string BuscarLlamaServer()
        {
            const string exe = "llama-server.exe";
            try
            {
                foreach (var sub in new[] { "", "bin", "llama", "llama.cpp" })
                {
                    string p = JuntoAlExe(Path.Combine(sub, exe));
                    if (File.Exists(p)) return p;
                }
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim().Trim('"'), exe);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return JuntoAlExe(exe);
        }

        public static Config Cargar(string ruta)
        {
            var c = new Config { Ruta = ruta };
            var d = Json.LeerObjeto(ruta);
            if (d == null) { c.Guardar(); return c; }
            c.ServidorUrl = Json.S(d, "servidorUrl", c.ServidorUrl);
            c.Puerto = Json.I(d, "puerto", c.Puerto);
            c.LlamaServer = Json.S(d, "llamaServer", c.LlamaServer);
            var carp = Json.Cadenas(d, "carpetasModelos");
            if (carp.Length > 0) c.CarpetasModelos = carp.ToList();
            c.ModeloPreferido = Json.S(d, "modeloPreferido", "");
            c.Hilos = Math.Max(1, Json.I(d, "hilos", c.Hilos));
            c.Contexto = Math.Max(512, Json.I(d, "contexto", c.Contexto));
            c.FlagsExtra = Json.S(d, "flagsExtra", "");
            c.LevantarSolo = Json.B(d, "levantarSolo", c.LevantarSolo);

            c.Temperatura = Json.D(d, "temperatura", c.Temperatura);
            c.TopP = Json.D(d, "topP", c.TopP);
            c.TopK = Json.I(d, "topK", c.TopK);
            c.MaxTokens = Math.Max(16, Json.I(d, "maxTokens", c.MaxTokens));
            c.Presencia = Json.D(d, "presencia", c.Presencia);
            c.Repeticion = Json.D(d, "repeticion", c.Repeticion);
            c.Pensar = Json.B(d, "pensar", c.Pensar);

            c.PersonaActiva = Json.S(d, "personaActiva", c.PersonaActiva);

            c.EscalaFuente = Json.D(d, "escalaFuente", c.EscalaFuente);
            c.EscalaUI = Json.D(d, "escalaUI", c.EscalaUI);
            c.Acento = Json.S(d, "acento", c.Acento);
            c.VentanaX = Json.I(d, "ventanaX"); c.VentanaY = Json.I(d, "ventanaY");
            c.VentanaAncho = Json.I(d, "ventanaAncho"); c.VentanaAlto = Json.I(d, "ventanaAlto");
            c.RielIzquierdo = Json.B(d, "rielIzquierdo", c.RielIzquierdo);
            c.RielDerecho = Json.B(d, "rielDerecho", c.RielDerecho);
            c.AnchoIzq = Json.I(d, "anchoIzq"); c.AnchoDer = Json.I(d, "anchoDer");
            c.AnchoChat = Math.Max(40, Math.Min(100, Json.I(d, "anchoChat", c.AnchoChat)));
            c.Reticula = Json.B(d, "reticula", c.Reticula);
            c.Grano = Json.B(d, "grano", c.Grano);

            c.MinimizarABandeja = Json.B(d, "minimizarABandeja", c.MinimizarABandeja);
            c.CerrarABandeja = Json.B(d, "cerrarABandeja", c.CerrarABandeja);
            c.IniciarMinimizado = Json.B(d, "iniciarMinimizado", c.IniciarMinimizado);
            c.IniciarConWindows = Json.B(d, "iniciarConWindows", c.IniciarConWindows);
            c.AtajoGlobal = Json.B(d, "atajoGlobal", c.AtajoGlobal);
            c.Sonido = Json.B(d, "sonido", c.Sonido);
            c.Quindar = Json.B(d, "quindar", c.Quindar);
            c.Notificaciones = Json.B(d, "notificaciones", c.Notificaciones);
            c.EnterEnvia = Json.B(d, "enterEnvia", c.EnterEnvia);
            c.LogDebug = Json.B(d, "logDebug", c.LogDebug);
            c.GuardarHistorial = Json.B(d, "guardarHistorial", c.GuardarHistorial);
            return c;
        }

        public void Guardar()
        {
            try
            {
                Json.Escribir(Ruta, new Dictionary<string, object>
                {
                    ["servidorUrl"] = ServidorUrl,
                    ["puerto"] = Puerto,
                    ["llamaServer"] = LlamaServer,
                    ["carpetasModelos"] = CarpetasModelos,
                    ["modeloPreferido"] = ModeloPreferido,
                    ["hilos"] = Hilos,
                    ["contexto"] = Contexto,
                    ["flagsExtra"] = FlagsExtra,
                    ["levantarSolo"] = LevantarSolo,
                    ["temperatura"] = Temperatura,
                    ["topP"] = TopP,
                    ["topK"] = TopK,
                    ["maxTokens"] = MaxTokens,
                    ["presencia"] = Presencia,
                    ["repeticion"] = Repeticion,
                    ["pensar"] = Pensar,
                    ["personaActiva"] = PersonaActiva,
                    ["escalaFuente"] = EscalaFuente,
                    ["escalaUI"] = EscalaUI,
                    ["acento"] = Acento,
                    ["ventanaX"] = VentanaX,
                    ["ventanaY"] = VentanaY,
                    ["ventanaAncho"] = VentanaAncho,
                    ["ventanaAlto"] = VentanaAlto,
                    ["rielIzquierdo"] = RielIzquierdo,
                    ["rielDerecho"] = RielDerecho,
                    ["anchoIzq"] = AnchoIzq,
                    ["anchoDer"] = AnchoDer,
                    ["anchoChat"] = AnchoChat,
                    ["reticula"] = Reticula,
                    ["grano"] = Grano,
                    ["minimizarABandeja"] = MinimizarABandeja,
                    ["cerrarABandeja"] = CerrarABandeja,
                    ["iniciarMinimizado"] = IniciarMinimizado,
                    ["iniciarConWindows"] = IniciarConWindows,
                    ["atajoGlobal"] = AtajoGlobal,
                    ["sonido"] = Sonido,
                    ["quindar"] = Quindar,
                    ["notificaciones"] = Notificaciones,
                    ["enterEnvia"] = EnterEnvia,
                    ["logDebug"] = LogDebug,
                    ["guardarHistorial"] = GuardarHistorial,
                });
            }
            catch { }
        }

        public System.Drawing.Color ColorAcento
        {
            get
            {
                switch ((Acento ?? "").ToLowerInvariant())
                {
                    case "teal": return Tema.Teal;
                    case "ambar": case "ámbar": return Tema.Ambar;
                    case "cielo": return Tema.Cielo;
                    case "salvia": return Tema.Salvia;
                    case "crema": return Tema.Crema;
                    case "rosa": return Tema.Rosa;
                    default: return Tema.Malva;
                }
            }
        }
    }

    /// <summary>Arranque con Windows por la clave Run del usuario (sin tocar el registro de máquina, sin UAC).</summary>
    internal static class Autoarranque
    {
        const string Clave = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Nombre = "capcom";

        public static bool Activo()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Clave))
                    return k != null && k.GetValue(Nombre) != null;
            }
            catch { return false; }
        }

        public static bool Poner(bool si)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Clave, true))
                {
                    if (k == null) return false;
                    if (si) k.SetValue(Nombre, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --min");
                    else k.DeleteValue(Nombre, false);
                    return true;
                }
            }
            catch { return false; }
        }
    }

    /// <summary>Una persona = un prompt de sistema con nombre. Se guardan en personas.json.</summary>
    internal sealed class Persona
    {
        public string Clave = "";
        public string Nombre = "";
        public string Sistema = "";
        public string Color = "malva";
        public string Nota = "";

        public static List<Persona> Cargar(string ruta)
        {
            var d = Json.LeerObjeto(ruta);
            var lista = new List<Persona>();
            foreach (var o in Json.Lista(d, "personas"))
                lista.Add(new Persona
                {
                    Clave = Json.S(o, "clave"),
                    Nombre = Json.S(o, "nombre"),
                    Sistema = Json.S(o, "sistema"),
                    Color = Json.S(o, "color", "malva"),
                    Nota = Json.S(o, "nota"),
                });
            if (lista.Count == 0) { lista = PorDefecto(); Guardar(ruta, lista); }
            return lista;
        }

        public static void Guardar(string ruta, List<Persona> lista)
        {
            try { Json.Escribir(ruta, new Dictionary<string, object> { ["personas"] = lista }); } catch { }
        }

        public static List<Persona> PorDefecto() => new List<Persona>
        {
            new Persona
            {
                Clave = "capcom", Nombre = "CAPCOM", Color = "malva",
                Nota = "la voz de la sala: directa, corta, sin vueltas",
                Sistema = "Sos CAPCOM, la voz de la sala de control que le habla al operador. Contestás en castellano rioplatense, " +
                          "directo y corto: primero la conclusión, después el detalle. Nada de saludos largos, nada de disculpas, nada de emojis. " +
                          "Si no sabés algo, lo decís en una línea en vez de inventarlo. Si la respuesta tiene pasos, van numerados. " +
                          "Cuando hay código, va en un bloque con el lenguaje declarado."
            },
            new Persona
            {
                Clave = "ingeniero", Nombre = "INGENIERO", Color = "cielo",
                Nota = "explica el mecanismo, no el titular",
                Sistema = "Sos un ingeniero de sistemas que explica cómo funcionan las cosas por dentro, en castellano rioplatense. " +
                          "Preferís el mecanismo real antes que la analogía: qué llama a qué, qué se guarda dónde, qué falla y por qué. " +
                          "Usás tablas cuando hay más de tres cosas que comparar y bloques de código cuando el código es la respuesta. " +
                          "No adornás: si algo es una suposición tuya, lo marcás como suposición."
            },
            new Persona
            {
                Clave = "revisor", Nombre = "REVISOR", Color = "ambar",
                Nota = "busca el bug, no el elogio",
                Sistema = "Sos un revisor de código exigente y en castellano rioplatense. Te dan un fragmento y buscás lo que está mal: " +
                          "casos borde, condiciones de carrera, recursos sin cerrar, errores tragados, off-by-one, y lo que el autor claramente " +
                          "no probó. Cada hallazgo va con el escenario concreto que lo rompe. No elogiás por cortesía; si está bien, lo decís en una línea."
            },
            new Persona
            {
                Clave = "traductor", Nombre = "TRADUCTOR", Color = "salvia",
                Nota = "de un idioma a otro, sin comentarios",
                Sistema = "Traducís lo que te mandan. Si viene en castellano lo pasás a inglés, y si viene en cualquier otro idioma lo pasás " +
                          "a castellano rioplatense. Devolvés SOLO la traducción, sin comillas, sin explicaciones y sin notas, conservando el " +
                          "formato original (saltos de línea, listas, código)."
            },
            new Persona
            {
                Clave = "libre", Nombre = "SIN PERSONA", Color = "apagado",
                Nota = "sin prompt de sistema: el modelo crudo",
                Sistema = ""
            },
        };
    }
}
