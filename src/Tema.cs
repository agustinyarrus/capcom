using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// El lenguaje visual de CAPCOM: negro de consola + acentos pastel, y las primitivas de dibujo que le dan
    /// el aire de sala de control — retícula de fondo, corchetes de esquina, rótulos en versalita con tracking,
    /// barras segmentadas, diodos de estado y el anillo orbital.
    ///
    /// Regla de oro de la casa: la jerarquía la dan TAMAÑO + COLOR + PESO, nunca un borde grueso ni un relleno
    /// saturado. Todo lo que parece una caja es en realidad un tinte del fondo con un filete de 1 px.
    /// </summary>
    internal static class Tema
    {
        // --- cromo: negro de consola, un punto más frío que el gris habitual
        public static readonly Color Fondo = Hex("#07080b");
        public static readonly Color Panel = Hex("#0a0b10");
        public static readonly Color Consola = Hex("#0d0e14");     // superficie de tarjeta
        public static readonly Color Alza = Hex("#13151d");        // hover
        public static readonly Color Filete = Hex("#171a23");
        public static readonly Color Reticulado = Hex("#0f1118");
        public static readonly Color Texto = Hex("#d5d8e2");
        public static readonly Color Suave = Hex("#8b91a3");
        public static readonly Color Apagado = Hex("#565b6c");
        public static readonly Color Fantasma = Hex("#2c3040");

        // --- acentos pastel (Catppuccin desaturado: los saturados le resultaron demasiado)
        public static readonly Color Ambar = Hex("#f6c0a0");    // el color Apollo: valores vivos
        public static readonly Color Teal = Hex("#8fd6cc");     // NOMINAL
        public static readonly Color Malva = Hex("#c4b5fd");    // la identidad de CAPCOM
        public static readonly Color Cielo = Hex("#a8cff2");    // datos y enlaces
        public static readonly Color Crema = Hex("#eedfb8");    // código y avisos
        public static readonly Color Salvia = Hex("#b5dfa8");   // GO
        public static readonly Color Rosa = Hex("#f3b9d2");     // NO-GO
        public static readonly Color Rojo = Hex("#eba0ac");

        public static Color Hex(string h) => ColorTranslator.FromHtml(h);
        public static Color Alpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);
        public static Color Mezcla(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        /// <summary>DwmSetWindowAttribute quiere COLORREF, que es BGR.</summary>
        public static int Bgr(Color c) => (c.B << 16) | (c.G << 8) | c.R;

        // ------------------------------------------------------------------ tipografía

        /// <summary>Factor global sobre TODOS los tamaños de fuente (config "escalaFuente").</summary>
        public static float FactorFuente = 0.8f;
        /// <summary>Densidad de la interfaz: multiplica toda la métrica y ya viene aplicada en FactorFuente.</summary>
        public static float FactorUI = 0.8f;

        // Escalones de peso, del más fino al más sólido. Los cuatro cortes están instalados en esta máquina.
        static readonly string[] PesosCode =
        {
            Elegir("Cascadia Code ExtraLight", "Cascadia Code Light", "Cascadia Code", "Consolas"),
            Elegir("Cascadia Code Light", "Cascadia Code", "Consolas"),
            Elegir("Cascadia Code SemiLight", "Cascadia Code", "Consolas"),
            Elegir("Cascadia Code", "Consolas"),
        };
        static readonly string[] PesosMono =
        {
            Elegir("Cascadia Mono ExtraLight", "Cascadia Mono Light", "Cascadia Mono", "Consolas"),
            Elegir("Cascadia Mono Light", "Cascadia Mono", "Consolas"),
            Elegir("Cascadia Mono SemiLight", "Cascadia Mono", "Consolas"),
            Elegir("Cascadia Mono", "Consolas"),
        };
        public static bool CascadiaInstalada => PesosCode[0].StartsWith("Cascadia", StringComparison.OrdinalIgnoreCase);

        const float PisoPt = 5.0f;

        /// <summary>
        /// 🚨 El peso se elige por el TAMAÑO FINAL, no por el rol. La Cascadia ExtraLight es preciosa en grande y
        /// se deshace en chico: por debajo de ~6 pt el trazo queda más fino que un píxel y ClearType lo pinta gris
        /// sucio — no se ve delgado, se ve borroso. Cuanto más chica la letra, más cuerpo necesita.
        /// </summary>
        static int Escalon(float ptFinal) => ptFinal >= 10f ? 0 : ptFinal >= 7.5f ? 1 : ptFinal >= 6f ? 2 : 3;

        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();
        static Font F(bool mono, float pt, int masCuerpo, FontStyle estilo = FontStyle.Regular)
        {
            float real = Math.Max(PisoPt, pt * FactorFuente);
            string fam = (mono ? PesosMono : PesosCode)[Math.Min(3, Math.Max(0, Escalon(real) + masCuerpo))];
            string k = fam + "|" + real.ToString("0.##") + "|" + (int)estilo;
            lock (cache)
            {
                Font f;
                if (!cache.TryGetValue(k, out f)) { f = new Font(fam, real, estilo, GraphicsUnit.Point); cache[k] = f; }
                return f;
            }
        }
        /// <summary>La voz normal de la interfaz.</summary>
        public static Font Fina(float pt) => F(false, pt, 0);
        /// <summary>Un escalón más de cuerpo: es lo que separa una etiqueta de su valor.</summary>
        public static Font Media(float pt) => F(false, pt, 1);
        /// <summary>Dos escalones: para los números que tienen que leerse de lejos.</summary>
        public static Font Solida(float pt) => F(false, pt, 2);
        /// <summary>Código y dígitos de telemetría: conserva "Mono" en el nombre de la familia.</summary>
        public static Font Mono(float pt) => F(true, pt, 0);
        public static Font MonoMedia(float pt) => F(true, pt, 1);
        public static Font Italica(float pt) => F(false, pt, 0, FontStyle.Italic);
        public static Font MonoItalica(float pt) => F(true, pt, 0, FontStyle.Italic);

        static string Elegir(params string[] candidatas)
        {
            try
            {
                var instaladas = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidatas) if (instaladas.Contains(c)) return c;
            }
            catch { }
            return "Consolas";
        }

        // ------------------------------------------------------------------ dibujo base

        public static void Texto_(Graphics g, string s, Font f, Color c, Rectangle r,
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        public static Size Medir(Graphics g, string s, Font f) =>
            TextRenderer.MeasureText(g, s ?? "", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        public static GraphicsPath Redondeado(RectangleF r, float radio)
        {
            var p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(new RectangleF(r.X, r.Y, Math.Max(r.Width, 1), Math.Max(r.Height, 1))); return p; }
            float d = Math.Max(1f, radio * 2);
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Superficie de consola: relleno + filete de 1 px. Nada de bordes gruesos.</summary>
        public static void Placa(Graphics g, RectangleF r, float radio, Color fondo, Color borde)
        {
            using (var p = Redondeado(r, radio))
            using (var b = new SolidBrush(fondo))
            using (var pen = new Pen(borde, 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }

        // ------------------------------------------------------------------ primitivas de sala de control

        /// <summary>
        /// Retícula de papel milimetrado, apenas visible. Es lo que convierte un rectángulo negro en una consola:
        /// el ojo registra la trama sin llegar a leerla.
        /// </summary>
        public static void Reticula(Graphics g, Rectangle r, int paso, Color color, int cadaGruesa = 5)
        {
            if (paso < 3) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using (var fino = new Pen(color, 1f))
            using (var grueso = new Pen(Alpha(color, Math.Min(255, color.A + 26)), 1f))
            {
                int i = 0;
                for (int x = r.Left; x <= r.Right; x += paso, i++)
                    g.DrawLine(cadaGruesa > 0 && i % cadaGruesa == 0 ? grueso : fino, x, r.Top, x, r.Bottom);
                i = 0;
                for (int y = r.Top; y <= r.Bottom; y += paso, i++)
                    g.DrawLine(cadaGruesa > 0 && i % cadaGruesa == 0 ? grueso : fino, r.Left, y, r.Right, y);
            }
            g.SmoothingMode = old;
        }

        /// <summary>Corchetes de esquina tipo visor: cuatro ángulos y nada más. Enmarca sin encajonar.</summary>
        public static void Esquinas(Graphics g, RectangleF r, float largo, Color color, float grosor = 1f)
        {
            using (var p = new Pen(color, grosor))
            {
                g.DrawLine(p, r.Left, r.Top, r.Left + largo, r.Top);
                g.DrawLine(p, r.Left, r.Top, r.Left, r.Top + largo);
                g.DrawLine(p, r.Right - largo, r.Top, r.Right, r.Top);
                g.DrawLine(p, r.Right, r.Top, r.Right, r.Top + largo);
                g.DrawLine(p, r.Left, r.Bottom - largo, r.Left, r.Bottom);
                g.DrawLine(p, r.Left, r.Bottom, r.Left + largo, r.Bottom);
                g.DrawLine(p, r.Right - largo, r.Bottom, r.Right, r.Bottom);
                g.DrawLine(p, r.Right, r.Bottom - largo, r.Right, r.Bottom);
            }
        }

        /// <summary>
        /// Texto con tracking (espaciado entre letras). GDI no lo hace solo, así que se dibuja letra por letra.
        /// Es la diferencia entre "TELEMETRIA" y «T E L E M E T R Í A»: el segundo se lee como rótulo, no como palabra.
        /// </summary>
        public static int Tracking(Graphics g, string s, Font f, Color c, int x, int y, int alto, float espacio)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int x0 = x;
            using (var br = new SolidBrush(c))
            {
                foreach (char ch in s)
                {
                    string t = ch.ToString();
                    int w = Medir(g, t, f).Width;
                    if (ch != ' ') Texto_(g, t, f, c, new Rectangle(x, y, w + 2, alto));
                    x += (int)Math.Round(w + espacio);
                }
            }
            return x - x0;
        }
        public static int MedirTracking(Graphics g, string s, Font f, float espacio)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int x = 0;
            foreach (char ch in s) x += (int)Math.Round(Medir(g, ch.ToString(), f).Width + espacio);
            return x;
        }

        /// <summary>
        /// Rótulo de sección: número de índice, nombre en versalita con tracking y un filete que se va al borde.
        /// Es el encabezado de toda la app — nunca un h2 en negrita.
        /// </summary>
        public static void Rotulo(Graphics g, string indice, string nombre, Rectangle r, Color acento, float esc, string derecha = "")
        {
            int S(int px) => Dpi.S(esc, px);
            var fIdx = MonoMedia(7.5f);
            var fNom = Media(8f);
            int x = r.X;
            if (!string.IsNullOrEmpty(indice))
            {
                int w = Medir(g, indice, fIdx).Width;
                Texto_(g, indice, fIdx, Alpha(acento, 190), new Rectangle(x, r.Y, w + S(4), r.Height));
                x += w + S(9);
            }
            int wNom = Tracking(g, (nombre ?? "").ToUpperInvariant(), fNom, Suave, x, r.Y, r.Height, S(2));
            x += wNom + S(10);
            int xFin = r.Right;
            if (!string.IsNullOrEmpty(derecha))
            {
                var fd = Mono(7.5f);
                int wd = Medir(g, derecha, fd).Width;
                Texto_(g, derecha, fd, Apagado, new Rectangle(r.Right - wd, r.Y, wd + S(3), r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                xFin = r.Right - wd - S(10);
            }
            if (xFin > x)
            {
                using (var p = new Pen(Filete, 1f)) g.DrawLine(p, x, r.Y + r.Height / 2f, xFin, r.Y + r.Height / 2f);
            }
        }

        /// <summary>Filete con marcas de regla: el detalle que hace que una línea parezca instrumental.</summary>
        public static void Regla(Graphics g, int x1, int x2, int y, Color color, float esc, int paso = 12, int alto = 3)
        {
            using (var p = new Pen(color, 1f))
            {
                g.DrawLine(p, x1, y, x2, y);
                int d = Dpi.S(esc, paso), h = Dpi.S(esc, alto);
                for (int x = x1; x <= x2; x += d) g.DrawLine(p, x, y - h, x, y);
            }
        }

        /// <summary>Diodo de estado con halo. Encendido = lleno + halo; apagado = solo el aro.</summary>
        public static void Diodo(Graphics g, float cx, float cy, float d, Color c, bool encendido, float halo = 1f)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (encendido)
            {
                if (halo > 0)
                    using (var b = new SolidBrush(Alpha(c, (int)(38 * halo))))
                        g.FillEllipse(b, cx - d * 1.6f, cy - d * 1.6f, d * 3.2f, d * 3.2f);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            }
            else
            {
                using (var p = new Pen(Alpha(c, 110), 1f)) g.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);
            }
            g.SmoothingMode = old;
        }

        /// <summary>
        /// Barra de segmentos: no es una barra de progreso lisa, son celdas discretas que se encienden.
        /// Un instrumento cuenta, no interpola.
        /// </summary>
        public static void Segmentos(Graphics g, RectangleF r, float fraccion, Color c, float esc, int celdas = 24)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            fraccion = Math.Max(0f, Math.Min(1f, fraccion));
            float hueco = Math.Max(1f, Dpi.S(esc, 2));
            float w = (r.Width - hueco * (celdas - 1)) / celdas;
            int encendidas = (int)Math.Round(fraccion * celdas);
            for (int i = 0; i < celdas; i++)
            {
                var rc = new RectangleF(r.X + i * (w + hueco), r.Y, w, r.Height);
                bool on = i < encendidas;
                using (var b = new SolidBrush(on ? Alpha(c, 225) : Alpha(Fantasma, 120))) g.FillRectangle(b, rc);
            }
            g.SmoothingMode = old;
        }

        /// <summary>Anillo de progreso con punta redondeada.</summary>
        public static void Anillo(Graphics g, RectangleF r, float grosor, float fraccion, Color pista, Color color, float desde = -90f)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pp = new Pen(pista, grosor))
            using (var pc = new Pen(color, grosor) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(pp, r);
                if (fraccion > 0.0005f) g.DrawArc(pc, r, desde, Math.Min(1f, fraccion) * 360f);
            }
            g.SmoothingMode = old;
        }

        /// <summary>
        /// El ornamento firma de la app: un planeta, una órbita inclinada y un satélite que la recorre.
        /// `fase` va de 0 a 1. Con `actividad` en 0 queda quieto y apagado — no miente sobre que algo está pasando.
        /// </summary>
        public static void Orbital(Graphics g, RectangleF r, float fase, Color color, float actividad = 1f)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float rx = r.Width / 2f, ry = r.Height / 2f * 0.42f;
            int alfa = (int)(70 + 120 * actividad);
            using (var p = new Pen(Alpha(color, alfa), 1f))
            {
                var st = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(-22f);
                g.DrawEllipse(p, -rx, -ry, rx * 2, ry * 2);
                // el satélite
                double a = fase * Math.PI * 2;
                float sx = (float)(Math.Cos(a) * rx), sy = (float)(Math.Sin(a) * ry);
                float d = Math.Max(2.5f, r.Width * 0.075f);
                using (var b = new SolidBrush(Alpha(color, (int)(120 + 135 * actividad))))
                    g.FillEllipse(b, sx - d / 2, sy - d / 2, d, d);
                if (actividad > 0.05f)
                    using (var b = new SolidBrush(Alpha(color, (int)(40 * actividad))))
                        g.FillEllipse(b, sx - d * 1.7f, sy - d * 1.7f, d * 3.4f, d * 3.4f);
                g.Restore(st);
            }
            // el cuerpo central
            float dc = Math.Min(r.Width, r.Height) * 0.26f;
            using (var b = new SolidBrush(Alpha(color, (int)(45 + 90 * actividad))))
                g.FillEllipse(b, cx - dc / 2, cy - dc / 2, dc, dc);
            using (var p = new Pen(Alpha(color, (int)(90 + 120 * actividad)), 1f))
                g.DrawEllipse(p, cx - dc / 2, cy - dc / 2, dc, dc);
            g.SmoothingMode = old;
        }

        /// <summary>
        /// Lectura de telemetría: rótulo chiquito arriba, número grande en mono abajo y unidad al costado.
        /// Los dígitos van en monoespaciada para que no bailen cuando cambian.
        /// </summary>
        public static void Lectura(Graphics g, Rectangle r, string etiqueta, string valor, string unidad, Color acento, float esc, float ptValor = 15f)
        {
            int S(int px) => Dpi.S(esc, px);
            var fEt = Media(7f);
            Tracking(g, (etiqueta ?? "").ToUpperInvariant(), fEt, Apagado, r.X, r.Y, S(12), S(2));
            var fVal = MonoMedia(ptValor);
            int wv = Medir(g, valor ?? "", fVal).Width;
            int yv = r.Y + S(13);
            Texto_(g, valor, fVal, acento, new Rectangle(r.X, yv, wv + S(6), r.Height - S(13)), TextFormatFlags.Left | TextFormatFlags.Top);
            if (!string.IsNullOrEmpty(unidad))
            {
                var fu = Mono(7.5f);
                var szv = Medir(g, valor ?? "", fVal);
                Texto_(g, unidad, fu, Apagado, new Rectangle(r.X + wv + S(4), yv + szv.Height - Medir(g, unidad, fu).Height - S(1), S(60), S(14)), TextFormatFlags.Left | TextFormatFlags.Top);
            }
        }

        /// <summary>Pastilla chica: estado en versalita sobre un tinte del acento.</summary>
        public static Rectangle Insignia(Graphics g, string s, Font f, Color c, float x, float y, float esc, bool derecha = false)
        {
            int S(int px) => Dpi.S(esc, px);
            int w = MedirTracking(g, s, f, S(1)) + S(14);
            int h = Medir(g, "X", f).Height + S(7);
            if (derecha) x -= w;
            var r = new RectangleF(x, y, w, h);
            Placa(g, r, S(2), Mezcla(Fondo, c, 0.13f), Alpha(c, 70));
            Tracking(g, s, f, c, (int)x + S(7), (int)y, h, S(1));
            return Rectangle.Round(r);
        }

        /// <summary>Un degradado vertical suave para el borde de un scroller (dice "sigue" sin una barra).</summary>
        public static void Desvanecido(Graphics g, Rectangle r, Color c, bool haciaAbajo)
        {
            if (r.Height <= 0 || r.Width <= 0) return;
            using (var b = new LinearGradientBrush(r, haciaAbajo ? Alpha(c, 0) : c, haciaAbajo ? c : Alpha(c, 0), LinearGradientMode.Vertical))
                g.FillRectangle(b, r);
        }

        /// <summary>
        /// ⭐ La regla de columnas de TODA la app: cada columna se mide por su texto más ancho y el espacio
        /// disponible se reparte EN PROPORCIÓN a esas medidas, llenando el ancho completo. Ni columnas fijas
        /// que cortan con «…», ni una tabla apretada a la izquierda con medio panel vacío a la derecha.
        /// Si no entra, se encoge también en proporción, respetando los mínimos.
        /// La suma del resultado es exactamente `disponible`: el resto de la división entera va a la más ancha.
        /// </summary>
        public static int[] Repartir(int[] natural, int disponible, int[] minimo = null)
        {
            int n = natural.Length;
            var res = new int[n];
            if (n == 0 || disponible <= 0) return res;
            double suma = 0;
            foreach (var v in natural) suma += Math.Max(1, v);
            int mayor = 0;
            for (int i = 1; i < n; i++) if (natural[i] > natural[mayor]) mayor = i;
            int asignado = 0;
            for (int i = 0; i < n; i++)
            {
                res[i] = (int)Math.Floor(Math.Max(1, natural[i]) * disponible / suma);
                asignado += res[i];
            }
            res[mayor] += disponible - asignado;
            if (minimo != null)
            {
                // si al encoger alguna quedó por debajo de su mínimo, se le repone y se le saca a la mayor
                for (int i = 0; i < n && i < minimo.Length; i++)
                {
                    if (i == mayor || res[i] >= minimo[i]) continue;
                    int falta = minimo[i] - res[i];
                    int pisoMayor = mayor < minimo.Length ? minimo[mayor] : 10;
                    int puedo = Math.Max(0, Math.Min(falta, res[mayor] - pisoMayor));
                    res[i] += puedo;
                    res[mayor] -= puedo;
                }
            }
            return res;
        }

        public static string Relativo(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 45) return "recién";
            if (d.TotalMinutes < 60) return $"hace {(int)d.TotalMinutes} min";
            if (d.TotalHours < 24) return $"hace {(int)d.TotalHours} h";
            if (d.TotalDays < 7) return $"hace {(int)d.TotalDays} d";
            return t.ToString("dd/MM/yy");
        }

        /// <summary>Formato T+ de tiempo de misión: el reloj de la NASA cuenta desde el despegue.</summary>
        public static string TMas(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            return $"T+{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        public static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return b.ToString(i == 0 ? "0" : b < 10 ? "0.00" : "0.0") + " " + u[i];
        }
    }

    /// <summary>
    /// Escala por DPI del monitor donde vive el control, multiplicada por <see cref="Tema.FactorUI"/>.
    /// ⭐ `S` es el único punto por donde pasa toda la métrica de la app: tocar el factor encoge o agranda
    /// la interfaz entera de forma pareja. Nunca devuelve 0 para algo que pedía al menos 1 px.
    /// </summary>
    internal static class Dpi
    {
        public static float Escala(Control c)
        {
            try { uint d = Win32.GetDpiForWindow(c.Handle); if (d > 0) return d / 96f; } catch { }
            try { using (var g = c.CreateGraphics()) return g.DpiX / 96f; } catch { return 1f; }
        }
        public static int S(this Control c, int px) => S(Escala(c), px);
        public static int S(float escala, int px)
        {
            int v = (int)Math.Round(px * escala * Tema.FactorUI);
            return px > 0 && v < 1 ? 1 : v;
        }
        /// <summary>Escala SIN el factor de densidad: para lo que no tiene que encoger, como el tamaño de la ventana.</summary>
        public static int Bruto(float escala, int px) => (int)Math.Round(px * escala);
    }
}
