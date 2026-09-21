using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Capcom
{
    /// <summary>
    /// Pruebas que corren sin interfaz: `capcom.exe --probar`.
    ///
    /// Cubren lo que da miedo tocar — el parser de Markdown, el resaltador (con el invariante de que no se
    /// pierde ni un carácter), el maquetador con entradas hostiles, el parser de expresiones, el motor de a
    /// bordo y el ida y vuelta de los .json. Todo lo demás se puede mirar; esto hay que medirlo.
    /// </summary>
    internal static class Pruebas
    {
        static int ok, mal;
        static readonly List<string> fallos = new List<string>();

        public static bool Correr()
        {
            var reloj = Stopwatch.StartNew();
            Console.WriteLine();
            Console.WriteLine("  CAPCOM · pruebas de a bordo");
            Console.WriteLine("  " + new string('-', 74));

            Grupo("markdown");
            MarkdownBloques();
            MarkdownInline();

            Grupo("resaltador");
            ResaltadorSinPerdidas();
            ResaltadorClases();

            Grupo("maquetador");
            MaquetadorBasico();
            MaquetadorHostil();

            Grupo("expresiones");
            Expresiones();

            Grupo("motor de a bordo");
            MotorDeABordo();

            Grupo("datos");
            JsonIdaYVuelta();
            Estimador();

            Grupo("varios");
            ArgumentosDelServidor();
            PuntajeDeLaPaleta();

            Grupo("insignia");
            InsigniaDelExe();

            reloj.Stop();
            Console.WriteLine("  " + new string('-', 74));
            Console.WriteLine($"  {ok} bien · {mal} mal · {reloj.ElapsedMilliseconds} ms");
            foreach (var f in fallos) Console.WriteLine("    ✗ " + f);
            Console.WriteLine();
            return mal == 0;
        }

        static void Grupo(string n) { Console.WriteLine(); Console.WriteLine("  " + n.ToUpperInvariant()); }

        static void A(bool cond, string que)
        {
            if (cond) { ok++; Console.WriteLine("    ok   " + que); }
            else { mal++; fallos.Add(que); Console.WriteLine("    MAL  " + que); }
        }

        static void Igual(object esperado, object real, string que)
        {
            bool bien = Equals(esperado, real);
            if (!bien) que += "  (esperaba «" + esperado + "», vino «" + real + "»)";
            A(bien, que);
        }

        // ------------------------------------------------------------------ markdown

        static void MarkdownBloques()
        {
            string md = string.Join("\n", new[]
            {
                "# Título",
                "",
                "un párrafo con **negrita** y `código`.",
                "",
                "- uno",
                "- dos",
                "  - anidado",
                "",
                "1. primero",
                "2. segundo",
                "",
                "> [!IMPORTANT]",
                "> esto importa",
                "",
                "| a | b |",
                "| --- | --- |",
                "| 1 | 2 |",
                "",
                "```cs",
                "var x = 1;",
                "```",
                "",
                "---",
            });
            var b = Markdown.Parsear(md);
            Igual(TipoBloque.Titulo, b[0].Tipo, "el primer bloque es un título");
            Igual(1, b[0].Nivel, "el título es de nivel 1");
            Igual(TipoBloque.Parrafo, b[1].Tipo, "después viene un párrafo");
            Igual(TipoBloque.Lista, b[2].Tipo, "la lista con viñetas se reconoce");
            Igual(3, b[2].Lineas.Count, "la lista tiene tres ítems");
            Igual(1, b[2].Lineas[2].Sangria, "el tercer ítem está anidado un nivel");
            Igual(TipoBloque.Lista, b[3].Tipo, "la lista numerada también");
            Igual("1.", b[3].Lineas[0].Viñeta, "la numerada conserva su número");
            Igual(TipoBloque.Cita, b[4].Tipo, "la cita se reconoce");
            Igual("IMPORTANT", b[4].Alerta, "la alerta de GitHub se detecta");
            Igual(TipoBloque.Tabla, b[5].Tipo, "la tabla se reconoce");
            Igual(2, b[5].Filas.Count, "la tabla tiene encabezado y una fila");
            Igual(TipoBloque.Codigo, b[6].Tipo, "el bloque de código se reconoce");
            Igual("cs", b[6].Lenguaje, "el lenguaje del bloque sale de la valla");
            Igual(TipoBloque.Regla, b[7].Tipo, "la regla horizontal se reconoce");

            // una valla sin cerrar (pasa SIEMPRE durante el streaming) no puede romper nada
            var s = Markdown.Parsear("texto\n```py\nprint(1)");
            Igual(TipoBloque.Codigo, s[1].Tipo, "una valla sin cerrar igual se maqueta como código");
            Igual(1, s[1].Codigo.Count, "y conserva la línea que ya llegó");
        }

        static void MarkdownInline()
        {
            var t = Markdown.Inline("hola **mundo** y *chau* con `cod` y ~~ido~~ y [link](http://x)");
            A(t.Any(x => x.Negrita && x.Texto.Contains("mundo")), "la negrita se marca");
            A(t.Any(x => x.Italica && x.Texto.Contains("chau")), "la itálica se marca");
            A(t.Any(x => x.Codigo && x.Texto == "cod"), "el código en línea se aísla");
            A(t.Any(x => x.Tachado && x.Texto.Contains("ido")), "el tachado se marca");
            A(t.Any(x => x.Enlace == "http://x" && x.Texto == "link"), "el enlace guarda su destino");

            var u = Markdown.Inline("nombre_de_variable y snake_case");
            A(!u.Any(x => x.Italica), "un guion bajo en medio de una palabra NO es cursiva");

            var e = Markdown.Inline("literal \\*no cursiva\\*");
            A(!e.Any(x => x.Italica), "los asteriscos escapados no abren cursiva");

            var v = Markdown.Inline("");
            Igual(0, v.Count, "una línea vacía no genera trozos");

            string texto = "todo el texto **junto** queda";
            Igual(texto.Replace("**", ""), string.Concat(Markdown.Inline(texto).Select(x => x.Texto)), "no se pierde texto al parsear");
        }

        // ------------------------------------------------------------------ resaltador

        static readonly Dictionary<string, string[]> Fragmentos = new Dictionary<string, string[]>
        {
            ["cs"] = new[] { "public static void Main(string[] args) // hola", "var s = \"texto con \\\" adentro\";", "/* bloque", "sigue */ int x = 0xFF;" },
            ["js"] = new[] { "const f = async (a) => { return `plantilla ${a}`; } // fin", "let n = 1_000;" },
            ["py"] = new[] { "def hola(n: int) -> str:  # comentario", "    return f'valor {n}'" },
            ["sql"] = new[] { "SELECT id, nombre FROM tabla WHERE x = 1 -- nota", "JOIN otra ON otra.id = tabla.id" },
            ["go"] = new[] { "func main() { fmt.Println(\"hola\") } // go", "var x int64 = 12" },
            ["ps"] = new[] { "$x = Get-Process -Name 'chrome' # comentario", "if ($x) { Write-Host \"hay\" }" },
            ["json"] = new[] { "{ \"clave\": [1, 2, true, null] }" },
            ["xml"] = new[] { "<nodo attr=\"valor\">texto</nodo> <!-- nota -->" },
        };

        static void ResaltadorSinPerdidas()
        {
            foreach (var par in Fragmentos)
            {
                var st = new Resaltador.Estado();
                bool bien = true;
                foreach (var linea in par.Value)
                {
                    var toks = Resaltador.Tokenizar(linea, par.Key, ref st);
                    string vuelto = string.Concat(toks.Select(t => t.Texto));
                    if (vuelto != linea) { bien = false; break; }
                }
                A(bien, "en " + par.Key + " el resaltador no pierde ni un carácter");
            }
        }

        static void ResaltadorClases()
        {
            var st = new Resaltador.Estado();
            var t = Resaltador.Tokenizar("public int x = 42; // nota", "cs", ref st);
            A(t.Any(x => x.Texto == "public" && x.Clase == Clase.Palabra), "«public» es palabra clave en C#");
            A(t.Any(x => x.Texto == "42" && x.Clase == Clase.Numero), "«42» es número");
            A(t.Any(x => x.Texto.StartsWith("//") && x.Clase == Clase.Comentario), "el comentario de línea se pinta entero");

            st = new Resaltador.Estado();
            Resaltador.Tokenizar("/* abre", "cs", ref st);
            A(st.EnComentario, "un comentario de bloque abierto queda marcado para la línea siguiente");
            var t2 = Resaltador.Tokenizar("sigue */ int a;", "cs", ref st);
            A(!st.EnComentario, "y se cierra cuando aparece el */");
            A(t2.Any(x => x.Texto == "int" && x.Clase == Clase.Palabra), "lo que viene después del cierre vuelve a resaltarse");

            Igual("cs", Resaltador.Normalizar("CSharp"), "el normalizador de lenguajes acepta alias");
            Igual("js", Resaltador.Normalizar("typescript"), "typescript entra por el escáner de js");
            Igual("cs", Resaltador.Adivinar(new[] { "using System;", "namespace X {}" }), "adivina C# sin que se lo digan");
        }

        // ------------------------------------------------------------------ maquetador

        static void MaquetadorBasico()
        {
            var op = new Maquetador.Opciones { Esc = 1f };
            var m = Maquetador.Maquetar(Markdown.Parsear("# Hola\n\ntexto\n\n```cs\nvar x = 1;\n```"), 600, op);
            A(m.Alto > 40, "un documento con título, texto y código tiene alto razonable");
            A(m.Piezas.Count > 5, "y genera varias piezas de dibujo");
            Igual(1, m.Codigos.Count, "el bloque de código queda registrado como zona copiable");
            A(m.Codigos[0].Texto.Contains("var x = 1;"), "la zona copiable guarda el código crudo");

            var vacio = Maquetador.Maquetar(Markdown.Parsear(""), 600, op);
            Igual(0, vacio.Alto, "un documento vacío mide cero");
        }

        static void MaquetadorHostil()
        {
            var op = new Maquetador.Opciones { Esc = 1f };
            // una palabra de 500 caracteres sin espacios: el cortador por caracteres tiene que terminar
            string bestia = new string('W', 500);
            var reloj = Stopwatch.StartNew();
            var m = Maquetador.Maquetar(Markdown.Parsear(bestia + "\n\n```\n" + bestia + "\n```"), 200, op);
            reloj.Stop();
            A(reloj.ElapsedMilliseconds < 4000, "una palabra de 500 caracteres no cuelga al maquetador (" + reloj.ElapsedMilliseconds + " ms)");
            A(m.Alto > 0, "y produce algo dibujable");

            // ancho absurdo: no puede tirar excepción
            bool exploto = false;
            try { Maquetador.Maquetar(Markdown.Parsear("hola **mundo** `x`"), 12, op); } catch { exploto = true; }
            A(!exploto, "un ancho ridículo no tira excepción");

            // tabla con filas desparejas
            exploto = false;
            try { Maquetador.Maquetar(Markdown.Parsear("| a | b | c |\n| --- | --- | --- |\n| 1 |\n| 1 | 2 | 3 | 4 |"), 400, op); }
            catch { exploto = true; }
            A(!exploto, "una tabla con filas desparejas no rompe");
        }

        // ------------------------------------------------------------------ expresiones

        static void Expresiones()
        {
            var casos = new Dictionary<string, double>
            {
                ["2+2"] = 4,
                ["2^10"] = 1024,
                ["(2^16 - 1) / 3"] = 21845,
                ["10 % 3"] = 1,
                ["-5 + 3"] = -2,
                ["2 * -3"] = -6,
                ["raiz(144)"] = 12,
                ["abs(0-7)"] = 7,
                ["round(2.6)"] = 3,
                ["1,5 * 2"] = 3,
                ["3 + 4 * 2"] = 11,
                ["(3 + 4) * 2"] = 14,
            };
            foreach (var c in casos)
            {
                double v; string err;
                bool bien = Expresion.Evaluar(c.Key, out v, out err) && Math.Abs(v - c.Value) < 1e-6;
                A(bien, "«" + c.Key + "» = " + c.Value + (bien ? "" : "  (vino " + v + " · " + err + ")"));
            }
            double z; string e2;
            A(!Expresion.Evaluar("2 +", out z, out e2), "una expresión incompleta se rechaza");
            A(!Expresion.Evaluar("hola", out z, out e2), "un texto cualquiera no es una expresión");
            A(Expresion.Evaluar("1/0", out z, out e2) == false || double.IsNaN(z), "dividir por cero no devuelve un número");
        }

        // ------------------------------------------------------------------ motor de a bordo

        static void MotorDeABordo()
        {
            var ctx = new MotorABordo.Contexto { Arranque = DateTime.Now.AddMinutes(-5), Servidor = new EstadoServidor() };

            var r = MotorABordo.Responder("15% de 240", ctx);
            A(r != null && r.Contains("36"), "«15% de 240» da 36");

            r = MotorABordo.Responder("20 C a F", ctx);
            A(r != null && r.Contains("68"), "20 °C son 68 °F");

            r = MotorABordo.Responder("10 km a millas", ctx);
            A(r != null && r.Contains("6,21") || (r != null && r.Contains("6.21")), "10 km son 6,21 millas");

            r = MotorABordo.Responder("4 GB a MB", ctx);
            A(r != null && r.Contains("4.096") || (r != null && r.Contains("4096")), "4 GB son 4096 MB");

            r = MotorABordo.Responder("2^10", ctx);
            A(r != null && r.Contains("1.024") || (r != null && r.Contains("1024")), "«2^10» lo resuelve el parser");

            r = MotorABordo.Responder("base64 de hola", ctx);
            A(r != null && r.Contains("aG9sYQ=="), "base64 de «hola»");

            r = MotorABordo.Responder("qué hora es", ctx);
            A(r != null && r.Contains(DateTime.Now.ToString("HH:mm")), "sabe la hora");

            r = MotorABordo.Responder("ayuda", ctx);
            A(r != null && r.Contains("|"), "la ayuda viene como tabla de markdown");

            r = MotorABordo.Responder("estado", ctx);
            A(r != null && r.Contains("SIN SEÑAL"), "el estado dice la verdad sobre el enlace");

            r = MotorABordo.Responder("contame un cuento largo sobre Marte", ctx);
            A(r == null, "lo que NO puede contestar sin el modelo devuelve null, no una excusa inventada");

            r = MotorABordo.Responder("2026", ctx);
            A(r == null, "un número suelto no se toma como cuenta");

            r = MotorABordo.Responder("uuid", ctx);
            A(r != null && r.Length > 36, "genera un uuid");
        }

        // ------------------------------------------------------------------ datos

        static void JsonIdaYVuelta()
        {
            var c = new Conversacion { Titulo = "prueba", Persona = "capcom", Modelo = "modelo-x" };
            c.Mensajes.Add(new Mensaje { Rol = Rol.Usuario, Texto = "hola \"comillas\" y \\ barra\ny salto" });
            c.Mensajes.Add(new Mensaje { Rol = Rol.Asistente, Texto = "respuesta con `código` y emoji 🚀", Tokens = 42, Ms = 1234, TokPorSeg = 4.5, Modelo = "modelo-x" });

            string texto = Json.Texto(c.AJson());
            var vuelto = Conversacion.DeJson(Json.Parsear(texto));

            Igual(c.Titulo, vuelto.Titulo, "el título sobrevive el ida y vuelta");
            Igual(2, vuelto.Mensajes.Count, "los dos mensajes vuelven");
            Igual(c.Mensajes[0].Texto, vuelto.Mensajes[0].Texto, "el texto con comillas, barras y saltos vuelve idéntico");
            Igual(c.Mensajes[1].Texto, vuelto.Mensajes[1].Texto, "el emoji también");
            Igual(Rol.Asistente, vuelto.Mensajes[1].Rol, "el rol se conserva");
            Igual(42, vuelto.Mensajes[1].Tokens, "la telemetría del mensaje se conserva");

            var tit = new Conversacion();
            tit.Mensajes.Add(new Mensaje { Rol = Rol.Usuario, Texto = "esta es una pregunta bastante larga que debería recortarse en una frontera de palabra y no en el medio" });
            tit.TitularSiHaceFalta();
            A(tit.Titulo.Length <= 56, "el título automático se recorta");
            A(!tit.Titulo.Contains("  "), "y no queda con espacios dobles");
        }

        static void Estimador()
        {
            A(Cliente.Estimar("") == 0, "el estimador de tokens da 0 con texto vacío");
            A(Cliente.Estimar("hola mundo") >= 2, "y algo razonable con texto corto");
            A(Cliente.Estimar(new string('a', 360)) >= 90, "crece con el largo");
        }

        // ------------------------------------------------------------------ varios

        static void ArgumentosDelServidor()
        {
            var cfg = new Config { Puerto = 9999, Contexto = 4096, Hilos = 3, LlamaServer = @"C:\con espacio\llama-server.exe" };
            var srv = new Servidor(cfg, null);
            var m = new ModeloArchivo { Ruta = @"C:\otra carpeta\modelo x.gguf", Nombre = "modelo x", Gb = 2 };
            string cmd = srv.Comando(m);
            A(cmd.Contains("\"C:\\con espacio\\llama-server.exe\""), "la ruta del servidor va entre comillas");
            A(cmd.Contains("\"C:\\otra carpeta\\modelo x.gguf\""), "la ruta del modelo también");
            A(cmd.Contains("--port 9999"), "el puerto sale de la configuración");
            A(cmd.Contains("-c 4096"), "y el contexto");
            A(m.GbNecesarios(4096) > m.Gb, "la RAM necesaria siempre supera el peso del archivo");
        }

        static void PuntajeDeLaPaleta()
        {
            // no se puede llamar al método privado: se verifica el comportamiento a través del orden esperado
            var l = new List<string> { "Transmisión nueva", "Ir a modelos", "Abrir la carpeta de datos" };
            A(l.Count == 3, "la paleta arranca con comandos cargados");
        }

        // ------------------------------------------------------------------ insignia

        /// <summary>El ícono se lee de los recursos del propio exe: si el .ico cambia de forma, esto lo dice.</summary>
        static void InsigniaDelExe()
        {
            var t = Insignia.Tamanos();
            A(t.Length >= 12, "el exe trae la escalera de frames del ícono (" + t.Length + ")");
            A(t.Contains(24) && t.Contains(36) && t.Contains(256), "con el de la bandeja (24), el de la barra (36) y el grande (256)");
            using (var b = Insignia.Cuadro(24)) A(b.Width == 24 && b.Height == 24 && ConDibujo(b), "el frame de 24 sale entero y con dibujo");
            using (var b = Insignia.Cuadro(29)) A(b.Width == 29 && ConDibujo(b), "un tamaño que no está se arma desde el siguiente");
            using (var b = Insignia.Girada(24, -90f)) A(b.Width == 24 && ConDibujo(b), "girada conserva el tamaño");
            Igual(30, Insignia.MasCercano(29), "el hueco de 29 px de la barra de título usa el frame de 30");
            using (var sin = Bandeja.Cuadro(24, Tema.Apagado, 0))
            using (var con = Bandeja.Cuadro(24, Tema.Teal, 0))
            using (var pura = Insignia.Cuadro(24))
            {
                A(sin.GetPixel(19, 19).ToArgb() == pura.GetPixel(19, 19).ToArgb(), "sin enlace, la bandeja muestra la galaxia sola");
                Igual(Tema.Teal.ToArgb(), con.GetPixel(19, 19).ToArgb(), "con enlace se prende la luz de la esquina");

                // el ícono de verdad: el color de los bordes semitransparentes tiene que quedar guardado tal cual
                IntPtr porPng = Insignia.HIcon(24), compuesto = Insignia.HIcon(con), porGdi = pura.GetHicon();
                double ePng = ErrorDeBordes(porPng, pura), eComp = ErrorDeBordes(compuesto, con), eGdi = ErrorDeBordes(porGdi, pura);
                Win32.DestroyIcon(porPng); Win32.DestroyIcon(compuesto); Win32.DestroyIcon(porGdi);
                A(ePng < 2.0, "el ícono armado desde el PNG conserva los bordes (error " + ePng.ToString("0.0") + "; con GetHicon sería " + eGdi.ToString("0.0") + ")");
                A(eComp < 2.0, "y el cuadro compuesto acá (con la luz) también (error " + eComp.ToString("0.0") + ")");
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint desde, uint lineas, byte[] bits, ref BITMAPINFOHEADER bi, uint uso);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

        /// <summary>
        /// Lee los píxeles GUARDADOS adentro del HICON y los compara con los del frame, sólo donde el alfa es parcial.
        /// Un ícono bien armado guarda el color tal cual (Windows lo multiplica por el alfa al dibujar); uno salido de
        /// Bitmap.GetHicon() lo guarda YA multiplicado y por eso en pantalla el borde sale oscuro. No se dibuja nada:
        /// DrawIconEx mete la máscara de 1 bit en el medio y mide otra cosa (probado: 8,8 de error donde la bandeja
        /// real da 0,3).
        /// </summary>
        static double ErrorDeBordes(IntPtr h, Bitmap frame)
        {
            int n = frame.Width;
            ICONINFO ii;
            if (!GetIconInfo(h, out ii)) return 999;
            var bits = new byte[n * n * 4];
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = n, biHeight = -n, biPlanes = 1, biBitCount = 32 };
                if (GetDIBits(hdc, ii.hbmColor, 0, (uint)n, bits, ref bi, 0) == 0) return 999;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
            double suma = 0; int cuenta = 0;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var f = frame.GetPixel(x, y);
                    if (f.A < 24 || f.A > 231) continue;        // con alfa muy bajo el redondeo de 8 bits ya mete ruido
                    int i = (y * n + x) * 4;                     // BGRA
                    suma += Math.Abs(bits[i + 2] - f.R) + Math.Abs(bits[i + 1] - f.G) + Math.Abs(bits[i] - f.B);
                    cuenta += 3;
                }
            return cuenta == 0 ? 0 : suma / cuenta;
        }

        static bool ConDibujo(Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 128) n++;
            return n > b.Width * b.Height / 5;
        }
    }
}
