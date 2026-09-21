using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Capcom
{
    internal static class Programa
    {
        public static string CarpetaDatos = "";
        public static readonly int MsgMostrar = Win32.RegisterWindowMessage("capcom-mostrar-consola");
        public static readonly int MsgFoto = Win32.RegisterWindowMessage("capcom-foto");
        /// <summary>Órdenes para la instancia que ya está corriendo: decir, levantar, bajar, ir.</summary>
        public static readonly int MsgOrden = Win32.RegisterWindowMessage("capcom-orden");

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool AllocConsole();
        const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        static void Main(string[] args)
        {
            try { Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // los números se escriben como acá: 8.192, no 8,192
            try
            {
                var es = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
                System.Threading.Thread.CurrentThread.CurrentCulture = es;
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = es;
            }
            catch { }

            CarpetaDatos = ElegirCarpetaDatos();
            var cfg = Config.Cargar(Path.Combine(CarpetaDatos, "config.json"));
            var log = new Logger(Path.Combine(CarpetaDatos, "logs")) { IncluirDebug = cfg.LogDebug };

            // la densidad entra en la métrica y además multiplica las fuentes: un número encoge todo parejo
            Tema.FactorUI = (float)cfg.EscalaUI;
            Tema.FactorFuente = (float)cfg.EscalaFuente * Tema.FactorUI;

            bool min = args.Any(a => Es(a, "min", false));
            // --mostrar: la abre una persona (el acceso del menú Inicio). La ventana se ve aunque la config diga
            // «arrancar en la bandeja»: sin esto, abrirla desde Inicio con la app cerrada no mostraba nada.
            bool mostrar = args.Any(a => Es(a, "mostrar"));

            // ---------------------------------------------------------- modos de línea de comando
            if (args.Any(a => Es(a, "version")))
            {
                Consola();
                Console.WriteLine("capcom 1.0 · capsule communicator · datos en " + CarpetaDatos);
                return;
            }
            if (args.Any(a => Es(a, "probar")))
            {
                Consola();
                Environment.ExitCode = Pruebas.Correr() ? 0 : 1;
                return;
            }
            if (args.Any(a => Es(a, "modelos")))
            {
                Consola();
                var srv = new Servidor(cfg, log);
                double libre, total;
                Win32.Memoria(out libre, out total);
                Console.WriteLine($"{libre:0.0} GB libres de {total:0.0}  ·  contexto {cfg.Contexto:N0}");
                Console.WriteLine(new string('-', 96));
                foreach (var m in srv.Inventario())
                {
                    double n = m.GbNecesarios(cfg.Contexto);
                    Console.WriteLine("{0,-44} {1,-9} {2,6:0.00} GB  ram {3,5:0.0} GB  {4}",
                        Corta(m.Nombre, 44), m.Cuant, m.Gb, n, m.EsProyector ? "visión" : n <= libre ? "entra" : "NO ENTRA");
                }
                return;
            }
            if (args.Any(a => Es(a, "preguntar")))
            {
                Consola();
                string q = Siguiente(args, "preguntar");
                if (q.Length == 0) { Console.Error.WriteLine("uso: capcom --preguntar \"tu pregunta\""); return; }
                var cli = new Cliente(log) { Url = cfg.ServidorUrl };
                var est = cli.Sondear();
                if (est.Señal != Señal.Nominal)
                {
                    var ctx = new MotorABordo.Contexto { Servidor = est, Arranque = DateTime.Now };
                    string local = MotorABordo.Responder(q, ctx);
                    Console.Error.WriteLine("sin enlace: " + est.Detalle);
                    Console.WriteLine(local ?? "(el motor de a bordo no sabe contestar eso sin el modelo)");
                    Environment.ExitCode = local != null ? 0 : 2;
                    return;
                }
                Console.Error.WriteLine("modelo: " + est.ModeloCorto);
                var personas = Persona.Cargar(Path.Combine(CarpetaDatos, "personas.json"));
                var per = personas.FirstOrDefault(p => p.Clave == cfg.PersonaActiva) ?? personas.FirstOrDefault();
                var hist = new List<Mensaje> { new Mensaje { Rol = Rol.Usuario, Texto = q } };
                var r = cli.Generar(per != null ? per.Sistema : "", hist, Ajustes.De(cfg), t => Console.Write(t), null);
                Console.WriteLine();
                Console.Error.WriteLine($"— {r.Ms / 1000.0:0.0} s · {r.Tokens} tokens · {r.TokPorSeg:0.0} tok/s" + (r.Error.Length > 0 ? " · " + r.Error : ""));
                Environment.ExitCode = r.Error.Length > 0 ? 1 : 0;
                return;
            }
            if (args.Any(a => Es(a, "examen")))
            {
                // toma el examen SIN interfaz: útil para dejarlo corriendo y mirar el informe después
                Consola();
                string quienes = Siguiente(args, "examen");
                var srvE = new Servidor(cfg, log);
                var inv = srvE.Inventario();
                var elegidos = new List<ModeloArchivo>();
                foreach (var q in quienes.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
                {
                    var m = inv.FirstOrDefault(x => !x.EsProyector && x.Nombre.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (m == null) Console.Error.WriteLine("no encontré ningún modelo que diga «" + q + "»");
                    else if (!elegidos.Any(e => e.Ruta == m.Ruta)) elegidos.Add(m);
                }
                if (elegidos.Count == 0) { Console.Error.WriteLine("uso: capcom --examen \"gemma,qwen3.5-2b\""); Environment.ExitCode = 2; return; }
                var banco = new Banco(cfg, log, CarpetaDatos);
                string porqueE;
                if (!banco.Correr(elegidos, out porqueE)) { Console.Error.WriteLine(porqueE); Environment.ExitCode = 1; return; }
                string ultimaFase = "";
                while (banco.Corriendo)
                {
                    string f = banco.ModeloActual + " · " + banco.PreguntaActual + "/" + banco.PreguntasTotal + " · " + banco.Fase;
                    if (f != ultimaFase) { Console.Error.WriteLine(f); ultimaFase = f; }
                    Thread.Sleep(700);
                }
                Console.WriteLine(banco.Informe());
                return;
            }
            if (args.Any(a => Es(a, "insignia")))
            {
                // los cuadros del ícono de bandeja tal como los arma la app (estados + la vuelta completa), a un PNG:
                // es la forma de MIRAR el ícono sin depender de que haya un modelo transmitiendo
                Consola();
                string salidaI = Siguiente(args, "insignia");
                if (salidaI.Length == 0) salidaI = Path.Combine(CarpetaDatos, "capturas", "insignia.png");
                salidaI = Path.GetFullPath(salidaI);
                Directory.CreateDirectory(Path.GetDirectoryName(salidaI));
                int lado; if (!int.TryParse(Valor(args, "lado"), out lado) || lado < 16) lado = SystemInformation.SmallIconSize.Width;
                Bandeja.Muestra(lado, salidaI);
                Console.WriteLine(salidaI + " · bandeja a " + lado + " px · frames del exe: " + string.Join(" ", Insignia.Tamanos()));
                return;
            }
            string[] verbos = { "decir", "levantar", "examinar", "traer", "bajar", "salir", "nueva", "persona", "ir" };
            string verbo = verbos.FirstOrDefault(v => args.Any(a => Es(a, v)));
            if (verbo != null)
            {
                Consola();
                bool sinValor = verbo == "bajar" || verbo == "salir" || verbo == "nueva";
                string arg = sinValor ? "" : Siguiente(args, verbo);
                if (!sinValor && arg.Length == 0) { Console.Error.WriteLine("uso: capcom --" + verbo + " \"<valor>\""); Environment.ExitCode = 2; return; }
                int esperaSeg; if (!int.TryParse(Valor(args, "esperar"), out esperaSeg)) esperaSeg = verbo == "decir" ? 300 : verbo == "levantar" ? 600 : verbo == "salir" ? 20 : 10;
                if (verbo == "salir" && !HayInstancia()) { Console.WriteLine("capcom no está corriendo"); return; }
                bool hecho = Ordenar(verbo, arg, esperaSeg);
                // «cerrando» es sólo el acuse: recién terminó cuando soltó el mutex (y con él, el exe)
                if (hecho && verbo == "salir") hecho = EsperarQueSalga(esperaSeg);
                Environment.ExitCode = hecho ? 0 : 1;
                return;
            }
            if (args.Any(a => Es(a, "foto")))
            {
                Consola();
                string cual = Siguiente(args, "foto");
                if (cual.Length == 0) cual = "0";
                string[] nombres = { "mision", "consola", "modelos", "registro", "ajustes" };
                int tab;
                if (!int.TryParse(cual, out tab))
                {
                    string c2 = Sinacentos(cual.ToLowerInvariant());
                    tab = Array.FindIndex(nombres, n => n.StartsWith(c2, StringComparison.OrdinalIgnoreCase));
                    if (tab < 0) { Console.Error.WriteLine("no conozco la pantalla «" + cual + "»; son: " + string.Join(", ", nombres)); return; }
                }
                string salida = Valor(args, "salida");
                if (salida.Length == 0) salida = Path.Combine(CarpetaDatos, "capturas", "foto-" + nombres[tab] + ".png");
                salida = Path.GetFullPath(salida);
                string sw = Valor(args, "ancho"), sh = Valor(args, "alto");
                string modo = Valor(args, "modo"), esp = Valor(args, "espera");
                try { if (File.Exists(salida)) File.Delete(salida); } catch { }
                Directory.CreateDirectory(Path.Combine(CarpetaDatos, "capturas"));
                File.WriteAllText(Path.Combine(CarpetaDatos, "foto-pedido.txt"),
                    tab + "|" + salida + "|" + (sw.Length > 0 ? sw : "0") + "|" + (sh.Length > 0 ? sh : "0")
                        + "|" + modo + "|" + (esp.Length > 0 ? esp : "0"), new UTF8Encoding(false));
                Win32.PostMessage(Win32.HWND_BROADCAST, MsgFoto, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 600 && !File.Exists(salida); i++) Thread.Sleep(100);
                if (File.Exists(salida)) Console.WriteLine(salida);
                else { Console.Error.WriteLine("no salió la foto · ¿está corriendo capcom?"); Environment.ExitCode = 1; }
                return;
            }

            // ---------------------------------------------------------- la app
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { log.Error("Excepción no controlada: " + e.ExceptionObject); } catch { } };
            Application.ThreadException += (s, e) => { try { log.Error("Excepción en la interfaz: " + e.Exception); } catch { } };

            bool nuevo;
            using (var unico = new Mutex(true, NombreMutex, out nuevo))
            {
                if (!nuevo)
                {
                    // ya hay una: que muestre la consola y listo. Salvo con --min, que es el arranque con Windows:
                    // llega ~10 min después del login y no tiene por qué destapar una ventana que nadie pidió.
                    if (!min) Win32.PostMessage(Win32.HWND_BROADCAST, MsgMostrar, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                var n = new Nucleo
                {
                    Cfg = cfg,
                    Log = log,
                    CarpetaDatos = CarpetaDatos,
                    Cli = new Cliente(log) { Url = cfg.ServidorUrl },
                    Personas = Persona.Cargar(Path.Combine(CarpetaDatos, "personas.json")),
                };
                n.Srv = new Servidor(cfg, log);
                n.Arch = new Archivo(CarpetaDatos, log);
                n.Banco = new Banco(cfg, log, CarpetaDatos);
                n.Bajadas = new Descargas(log);
                n.Tx = new Transmisor(n);
                n.Actual = n.Arch.Lista.FirstOrDefault() ?? n.Arch.Nueva(cfg.PersonaActiva);

                var form = new MainForm(n, min, mostrar);
                n.EnUi = a => { try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(a); } catch { } };

                log.Info("CAPCOM listo · datos en " + CarpetaDatos + (Tema.CascadiaInstalada ? "" : " · sin Cascadia Code, uso Consolas"));
                if (Autoarranque.Activo() != cfg.IniciarConWindows) { cfg.IniciarConWindows = Autoarranque.Activo(); cfg.Guardar(); }
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    var est = n.Cli.Sondear(2500);
                    n.EnUi(() =>
                    {
                        log.Escribir(est.Señal == Señal.Nominal ? Nivel.Ok : Nivel.Aviso,
                            est.Señal == Señal.Nominal ? "Enlace NOMINAL con " + est.ModeloCorto : "Sin enlace: " + est.Detalle);
                        n.Avisar();
                    });
                });
                Application.Run(form);
            }
        }

        /// <summary>
        /// Le pasa una orden a la instancia que ya está corriendo y espera su respuesta por archivo. Es la misma
        /// vía que `--foto`: un mensaje de ventana registrado y un archivito con los parámetros. Cero puertos,
        /// cero servidores, y además queda el rastro en disco de lo último que se pidió.
        /// </summary>
        static bool Ordenar(string verbo, string arg, int esperaSeg)
        {
            string pedido = Path.Combine(CarpetaDatos, "orden-pedido.txt");
            string respuesta = Path.Combine(CarpetaDatos, "orden-respuesta.txt");
            try { if (File.Exists(respuesta)) File.Delete(respuesta); } catch { }
            File.WriteAllText(pedido, verbo + "|" + arg, new UTF8Encoding(false));
            if (!Win32.PostMessage(Win32.HWND_BROADCAST, MsgOrden, IntPtr.Zero, IntPtr.Zero))
            {
                Console.Error.WriteLine("no pude mandar la orden");
                return false;
            }
            for (int i = 0; i < esperaSeg * 10 && !File.Exists(respuesta); i++) Thread.Sleep(100);
            if (!File.Exists(respuesta))
            {
                Console.Error.WriteLine("capcom no contestó · ¿está corriendo?");
                return false;
            }
            string cuerpo = "";
            for (int i = 0; i < 20; i++)
            {
                try { cuerpo = File.ReadAllText(respuesta, Encoding.UTF8); break; }
                catch { Thread.Sleep(50); }     // puede estar escribiéndose justo ahora
            }
            bool ok = !cuerpo.StartsWith("ERROR|");
            Console.WriteLine(ok ? cuerpo : cuerpo.Substring(6));
            return ok;
        }

        const string NombreMutex = @"Local\capcom-instancia-unica";

        static bool HayInstancia()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(NombreMutex, out m)) return false;
            m.Dispose();
            return true;
        }

        static bool EsperarQueSalga(int seg)
        {
            for (int i = 0; i < seg * 10; i++)
            {
                if (!HayInstancia()) return true;
                Thread.Sleep(100);
            }
            Console.Error.WriteLine("capcom acusó la orden pero sigue corriendo");
            return false;
        }

        static void Consola()
        {
            // una app WinExe no tiene consola: se engancha a la del que la lanzó, o se crea una
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        }

        static string Corta(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        static string Sinacentos(string s) => s.Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u');

        /// <summary>El valor que sigue a una bandera: `--foto equipo` → "equipo".</summary>
        static string Siguiente(string[] args, string nombre)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (Es(args[i], nombre) && !args[i + 1].StartsWith("-")) return args[i + 1];
            return "";
        }

        /// <summary>Valor de una opción: --nombre valor, o --nombre=valor.</summary>
        static string Valor(string[] args, string nombre)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").TrimStart('-', '/');
                int eq = a.IndexOf('=');
                if (eq > 0 && a.Substring(0, eq).Equals(nombre, StringComparison.OrdinalIgnoreCase)) return a.Substring(eq + 1).Trim('"');
                if (a.Equals(nombre, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("-")) return args[i + 1];
            }
            return "";
        }

        static bool Es(string arg, string nombre) => Es(arg, nombre, true);

        /// <summary>
        /// 🚨 Exigir el guion no es un detalle: sin eso el VALOR de una opción se toma como bandera, y
        /// `--foto consola --modo servidor` termina disparando un modo que no se pidió. Sólo `--min` se acepta
        /// pelado porque los accesos directos la pasan así.
        /// </summary>
        static bool Es(string arg, string nombre, bool exigeGuion)
        {
            if (string.IsNullOrEmpty(arg)) return false;
            if (exigeGuion && arg[0] != '-' && arg[0] != '/') return false;
            string a = arg.TrimStart('-', '/').ToLowerInvariant();
            int eq = a.IndexOf('=');
            if (eq > 0) a = a.Substring(0, eq);
            return a == nombre;
        }

        /// <summary>`datos\` al lado del exe si se puede escribir (app portable); si no, %LOCALAPPDATA%.</summary>
        static string ElegirCarpetaDatos()
        {
            try
            {
                string junto = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "datos");
                Directory.CreateDirectory(junto);
                string prueba = Path.Combine(junto, ".escritura");
                File.WriteAllText(prueba, "ok");
                File.Delete(prueba);
                return junto;
            }
            catch { }
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "capcom");
            Directory.CreateDirectory(local);
            return local;
        }
    }
}
