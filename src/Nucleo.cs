using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>Todo lo compartido entre pantallas: configuración, archivo, cliente, servidor y el transmisor.</summary>
    internal sealed class Nucleo
    {
        public Config Cfg;
        public Logger Log;
        public Cliente Cli;
        public Servidor Srv;
        public Archivo Arch;
        public Banco Banco;
        public Descargas Bajadas;
        public List<Persona> Personas = new List<Persona>();
        public Transmisor Tx;
        public DateTime Arranque = DateTime.Now;
        public string CarpetaDatos = "";

        public Conversacion Actual;
        public Action<Action> EnUi = a => a();
        public Action<string, string> Aviso = (t, x) => { };
        /// <summary>Algo cambió y todas las pantallas deberían mirarse de nuevo.</summary>
        public event Action Cambio;
        public void Avisar() { try { Cambio?.Invoke(); } catch { } }

        public Persona PersonaDe(string clave) =>
            Personas.FirstOrDefault(p => string.Equals(p.Clave, clave, StringComparison.OrdinalIgnoreCase)) ?? Personas.FirstOrDefault();

        public Persona PersonaActual => PersonaDe(Actual != null ? Actual.Persona : Cfg.PersonaActiva);

        public Color ColorPersona(Persona p)
        {
            if (p == null) return Tema.Malva;
            switch ((p.Color ?? "").ToLowerInvariant())
            {
                case "teal": return Tema.Teal;
                case "ambar": return Tema.Ambar;
                case "cielo": return Tema.Cielo;
                case "salvia": return Tema.Salvia;
                case "crema": return Tema.Crema;
                case "rosa": return Tema.Rosa;
                case "apagado": return Tema.Suave;
                default: return Tema.Malva;
            }
        }

        public MotorABordo.Contexto CtxABordo() => new MotorABordo.Contexto
        {
            Servidor = Cli.Estado,
            Arranque = Arranque,
            Transmisiones = Arch != null ? Arch.Lista.Count : 0,
            MensajesEnHilo = Actual != null ? Actual.Mensajes.Count : 0,
            Persona = PersonaActual != null ? PersonaActual.Nombre : "",
            Modelo = Cli.Estado.ModeloCorto,
        };
    }

    /// <summary>
    /// El transmisor: orquesta una respuesta de principio a fin en un hilo aparte y deja el texto entrando en
    /// el mensaje a medida que llega.
    ///
    /// El hilo de fondo ESCRIBE `Mensaje.Texto` y la UI lo LEE; la asignación de un string es atómica, así que
    /// no hace falta un candado por token — lo que sí hace falta es avisar que hubo novedad, y que la UI decida
    /// cada cuánto repintar. Marshalear cada token a la UI con BeginInvoke inunda la cola de mensajes y hace que
    /// la ventana se sienta pesada justo cuando más tiene que responder.
    /// </summary>
    internal sealed class Transmisor
    {
        readonly Nucleo n;
        Thread hilo;
        volatile bool ocupado;
        volatile bool hayNovedad;
        public Mensaje Escribiendo;
        public Conversacion Hilo;
        public DateTime Comenzo;
        public string Fase = "";

        public bool Ocupado => ocupado;
        public bool Novedad { get { bool v = hayNovedad; hayNovedad = false; return v; } }

        public event Action Empezo;
        public event Action<Resultado> Termino;

        public Transmisor(Nucleo nucleo) { n = nucleo; }

        /// <summary>Manda el texto del usuario y arranca la respuesta. Devuelve false si ya hay una en curso.</summary>
        public bool Enviar(Conversacion c, string texto)
        {
            if (ocupado || c == null) return false;
            texto = (texto ?? "").TrimEnd();
            if (texto.Length == 0) return false;
            var mu = new Mensaje { Rol = Rol.Usuario, Texto = texto };
            c.Mensajes.Add(mu);
            c.Tocada = DateTime.Now;
            c.TitularSiHaceFalta();
            return Arrancar(c);
        }

        /// <summary>Rehace la última respuesta: tira la que había y vuelve a pedir desde el mismo punto.</summary>
        public bool Regenerar(Conversacion c)
        {
            if (ocupado || c == null || c.Mensajes.Count == 0) return false;
            int i = c.Mensajes.FindLastIndex(m => m.Rol == Rol.Asistente);
            if (i < 0) return false;
            c.Mensajes.RemoveRange(i, c.Mensajes.Count - i);
            return Arrancar(c);
        }

        /// <summary>Edita un mensaje del usuario y vuelve a pedir desde ahí (se descarta lo que venía después).</summary>
        public bool Reenviar(Conversacion c, Mensaje m, string nuevoTexto)
        {
            if (ocupado || c == null || m == null) return false;
            int i = c.Mensajes.IndexOf(m);
            if (i < 0) return false;
            m.Texto = (nuevoTexto ?? "").TrimEnd();
            m.Cambio();
            if (i + 1 < c.Mensajes.Count) c.Mensajes.RemoveRange(i + 1, c.Mensajes.Count - i - 1);
            return Arrancar(c);
        }

        bool Arrancar(Conversacion c)
        {
            var persona = n.PersonaDe(c.Persona);
            var ma = new Mensaje
            {
                Rol = Rol.Asistente,
                Texto = "",
                Persona = persona != null ? persona.Clave : "",
                Modelo = n.Cli.Estado.ModeloCorto,
            };
            c.Mensajes.Add(ma);
            Escribiendo = ma;
            Hilo = c;
            Comenzo = DateTime.Now;
            ocupado = true;
            hayNovedad = true;
            Fase = "conectando";
            try { Empezo?.Invoke(); } catch { }
            if (n.Cfg.Quindar) Sonidos.Abrir();

            var ajustes = Ajustes.De(n.Cfg);
            string sistema = persona != null ? persona.Sistema : "";
            var historia = c.Mensajes.Take(c.Mensajes.Count - 1).ToList();

            hilo = new Thread(() => Correr(c, ma, sistema, historia, ajustes)) { IsBackground = true, Name = "capcom-tx" };
            hilo.Start();
            return true;
        }

        void Correr(Conversacion c, Mensaje ma, string sistema, List<Mensaje> historia, Ajustes a)
        {
            var reloj = Stopwatch.StartNew();
            Resultado r = null;
            try
            {
                // 1. ¿el enlace está vivo? si no, contesta el motor de a bordo, y lo dice.
                var est = n.Cli.Sondear(1200);
                if (est.Señal != Señal.Nominal)
                {
                    Fase = "motor de a bordo";
                    string local = MotorABordo.Responder(historia.LastOrDefault(m => m.Rol == Rol.Usuario)?.Texto ?? "", n.CtxABordo());
                    r = new Resultado { Ms = reloj.ElapsedMilliseconds };
                    if (local != null)
                    {
                        ma.Motor = true;
                        Escribir(ma, local);
                        r.Texto = local;
                    }
                    else
                    {
                        ma.Motor = true;
                        ma.Error = true;
                        string porque = est.Detalle;
                        string txt = "> [!IMPORTANT]\n> **Sin enlace con el modelo.** " + porque + "\n\n" +
                                     "Puedo contestar igual lo que se pueda verificar sin él: cuentas, fechas, unidades, codificación y el estado de la máquina. Escribí `ayuda` para la lista.\n\n" +
                                     "Para levantarlo, andá a la pestaña **MODELOS** y elegí uno.";
                        Escribir(ma, txt);
                        r.Error = porque;
                    }
                    return;
                }

                // 2. transmisión real
                Fase = "esperando el primer token";
                r = n.Cli.Generar(sistema, historia, a,
                    trozo =>
                    {
                        if (Fase != "escribiendo") Fase = "escribiendo";
                        Escribir(ma, ma.Texto + trozo);
                    },
                    piensa => { ma.Pensamiento += piensa; Fase = "pensando"; hayNovedad = true; });

                ma.Tokens = r.Tokens;
                ma.TokensPrompt = r.TokensPrompt;
                ma.Ms = r.Ms;
                ma.MsPrimerToken = r.MsPrimerToken;
                ma.TokPorSeg = r.TokPorSeg;
                ma.Modelo = r.Modelo;
                ma.Cancelado = r.Cancelado;
                if (r.Error.Length > 0 && r.Texto.Length == 0)
                {
                    ma.Error = true;
                    Escribir(ma, "> [!CAUTION]\n> " + r.Error);
                }
                else if (r.Error.Length > 0)
                {
                    // 🚨 un error DESPUÉS de que llegó algo de texto no se puede tragar: la respuesta queda
                    //    a medias y sin una palabra parece que el modelo contestó así. Se conserva lo que
                    //    llegó (es tuyo) y se dice en la misma burbuja que se cortó y por qué.
                    Escribir(ma, ma.Texto + "\n\n> [!CAUTION]\n> se cortó a mitad de camino: " + r.Error);
                }
                else if (r.Fin == "length")
                {
                    Escribir(ma, ma.Texto + "\n\n_(hasta acá llegó el presupuesto de tokens · subí «tokens máximos» en AJUSTES)_");
                }
                else if (r.Cancelado && r.Texto.Length == 0)
                {
                    Escribir(ma, "_(cortado antes de empezar)_");
                }
            }
            catch (Exception ex)
            {
                ma.Error = true;
                Escribir(ma, "> [!CAUTION]\n> " + ex.Message);
                r = new Resultado { Error = ex.Message };
            }
            finally
            {
                ocupado = false;
                hayNovedad = true;
                Fase = "";
                ma.Hora = DateTime.Now;
                c.Tocada = DateTime.Now;
                if (c.Modelo.Length == 0) c.Modelo = ma.Modelo;
                try { n.EnUi(() => Cerrar(c, r ?? new Resultado())); } catch { }
            }
        }

        void Cerrar(Conversacion c, Resultado r)
        {
            if (n.Cfg.Quindar) Sonidos.Cerrar();
            if (n.Cfg.GuardarHistorial) n.Arch.Guardar(c);
            n.Arch.Ordenar();
            Escribiendo = null;
            try { Termino?.Invoke(r); } catch { }
            n.Avisar();
        }

        void Escribir(Mensaje m, string texto)
        {
            m.Texto = texto;
            m.Cambio();
            hayNovedad = true;
        }

        /// <summary>Corta la transmisión en curso. Lo que ya llegó se conserva: es tuyo.</summary>
        public void Cortar()
        {
            if (!ocupado) return;
            Fase = "cortando";
            n.Cli.Cortar();
        }
    }

    /// <summary>
    /// Base de todas las pantallas. El gancho `Modo` es lo que permite fotografiar un estado puntual desde la
    /// línea de comandos sin tocar el mouse.
    /// </summary>
    internal abstract class Pantalla : Control
    {
        protected readonly Nucleo N;
        protected float esc = 1f;

        protected Pantalla(Nucleo n)
        {
            N = n;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        protected int S(int px) => Dpi.S(esc, px);

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); esc = Dpi.Escala(this); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); esc = Dpi.Escala(this); Acomodar(); Invalidate(); }

        /// <summary>Se llama cuando cambia el tamaño o al mostrarse: ubicar los hijos.</summary>
        public virtual void Acomodar() { }
        /// <summary>Se llama al entrar a la pestaña y cada tanto: releer datos.</summary>
        public virtual void Refrescar() { }
        /// <summary>Gancho para `--foto --modo &lt;palabra&gt;`: cada pantalla lo interpreta como quiera.</summary>
        public virtual void Modo(string que) { }
        /// <summary>Nombre corto para la pestaña.</summary>
        public abstract string Nombre { get; }
    }
}
