using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// El ícono de bandeja: la galaxia de CAPCOM al tamaño EXACTO de la bandeja (SM_CXSMICON, 24 px al 150 %), sacada
    /// del .ico del exe para que Windows no la reescale. Mientras el modelo escribe, la galaxia gira. El enlace es una
    /// luz en la esquina: teal nominal, malva transmitiendo, ámbar cargando; sin modelo no hay luz y queda la galaxia
    /// sola. La app vive acá — minimizar y cerrar la guardan, no la matan.
    /// </summary>
    internal sealed class Bandeja : IDisposable
    {
        readonly NotifyIcon icono = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem miMostrar, miNueva, miCortar, miServidor, miSilencio, miCarpeta, miSalir;
        readonly Timer animacion = new Timer { Interval = 90 };
        // los cuadros ya armados, por (tamaño | luz | paso): pasada la primera vuelta la animación no crea ni un handle
        readonly Dictionary<string, CuadroListo> cuadros = new Dictionary<string, CuadroListo>();
        sealed class CuadroListo { public IntPtr H; public Icon Icono; }
        string claveActual = "";
        int tamCuadros;
        int paso;               // 0 = derecha; Pasos cuadros por vuelta
        bool generando;

        public event EventHandler Mostrar, Nueva, Cortar, AlternarServidor, AlternarSilencio, AbrirCarpeta, Salir;

        public Bandeja()
        {
            menu.Renderer = new RenderOscuro();
            menu.BackColor = Tema.Panel;
            menu.ForeColor = Tema.Texto;
            menu.Font = Tema.Fina(9.5f);
            menu.ShowImageMargin = true;

            miMostrar = Item("Abrir la consola", (s, e) => Mostrar?.Invoke(this, EventArgs.Empty));
            miMostrar.Font = Tema.Media(9.5f);
            miNueva = Item("Transmisión nueva", (s, e) => Nueva?.Invoke(this, EventArgs.Empty));
            miCortar = Item("Cortar la generación", (s, e) => Cortar?.Invoke(this, EventArgs.Empty));
            miServidor = Item("Levantar / bajar el modelo", (s, e) => AlternarServidor?.Invoke(this, EventArgs.Empty));
            miSilencio = Item("Silenciar los tonos", (s, e) => AlternarSilencio?.Invoke(this, EventArgs.Empty));
            miCarpeta = Item("Abrir la carpeta de datos", (s, e) => AbrirCarpeta?.Invoke(this, EventArgs.Empty));
            miSalir = Item("Salir de CAPCOM", (s, e) => Salir?.Invoke(this, EventArgs.Empty));

            menu.Items.AddRange(new ToolStripItem[]
            {
                miMostrar, new ToolStripSeparator(), miNueva, miCortar,
                new ToolStripSeparator(), miServidor, miSilencio, miCarpeta,
                new ToolStripSeparator(), miSalir
            });
            icono.ContextMenuStrip = menu;
            icono.Text = "CAPCOM";
            icono.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Mostrar?.Invoke(this, EventArgs.Empty); };
            icono.DoubleClick += (s, e) => Mostrar?.Invoke(this, EventArgs.Empty);
            // transmitiendo: un cuadro por tic, una vuelta cada 3,6 s (rápido marea, lento parece trabado). Al terminar
            // no se corta en seco: completa la vuelta al triple de velocidad y se queda derecha.
            animacion.Tick += (s, e) =>
            {
                paso += generando ? 1 : 3;
                if (paso >= Pasos) paso = generando ? paso - Pasos : 0;
                if (!generando && paso == 0) animacion.Stop();
                Pintar();
            };
            Pintar();
            icono.Visible = true;
        }

        static ToolStripMenuItem Item(string texto, EventHandler h)
        {
            var it = new ToolStripMenuItem(texto) { ForeColor = Tema.Texto };
            it.Click += h;
            return it;
        }

        public void Aviso(string titulo, string texto, ToolTipIcon tipo = ToolTipIcon.None)
        {
            try { icono.ShowBalloonTip(5000, titulo, string.IsNullOrEmpty(texto) ? " " : texto, tipo); } catch { }
        }

        Color color = Tema.Apagado;

        /// <summary>Estado visible desde la bandeja: color del enlace + animación si hay transmisión.</summary>
        public void Actualizar(Señal señal, bool escribiendo, string modelo, string tip, bool silencio)
        {
            color = señal == Señal.Nominal ? (escribiendo ? Tema.Malva : Tema.Teal) : señal == Señal.Cargando ? Tema.Ambar : Tema.Apagado;
            miCortar.Enabled = escribiendo;
            miSilencio.Checked = silencio;
            miServidor.Text = señal == Señal.Nominal ? "Bajar el modelo" : "Levantar el modelo";
            if (escribiendo != generando)
            {
                generando = escribiendo;
                if (generando) animacion.Start();       // al terminar, el tic completa la vuelta y se detiene solo
            }
            string t = "CAPCOM · " + (señal == Señal.Nominal ? "NOMINAL" : señal == Señal.Cargando ? "CARGANDO" : "SIN SEÑAL");
            if (modelo.Length > 0) t += "\n" + modelo;
            if (tip.Length > 0) t += "\n" + tip;
            if (t.Length > 62) t = t.Substring(0, 62);
            icono.Text = t;
            Pintar();
        }

        public const int Pasos = 40;       // 9° por cuadro

        void Pintar()
        {
            // el tamaño que de verdad dibuja la bandeja: con otro, Windows lo reescala y se ve borroso
            int n = Math.Max(16, SystemInformation.SmallIconSize.Width);
            bool luz = color.ToArgb() != Tema.Apagado.ToArgb();
            string clave = n + "|" + (luz ? color.ToArgb() : 0) + "|" + paso;
            if (clave == claveActual) return;
            claveActual = clave;
            if (n != tamCuadros || cuadros.Count > 240) { Vaciar(); tamCuadros = n; }
            CuadroListo c;
            if (!cuadros.TryGetValue(clave, out c))
            {
                IntPtr h;
                if (paso == 0 && !luz) h = Insignia.HIcon(n);      // la galaxia sola: el PNG del .ico, sin tocar
                else using (var bmp = Cuadro(n, color, paso)) h = Insignia.HIcon(bmp);
                c = new CuadroListo { H = h, Icono = Icon.FromHandle(h) };      // FromHandle no se adueña: lo destruye Vaciar
                cuadros[clave] = c;
            }
            icono.Icon = c.Icono;
        }

        /// <summary>
        /// Un cuadro del ícono: la galaxia (derecha, o girada `paso` de `Pasos` en antihorario: los brazos se abren en
        /// horario, así que girando al revés se arrastran) y la luz del enlace. Sin enlace no hay luz.
        /// </summary>
        public static Bitmap Cuadro(int n, Color c, int paso)
        {
            var bmp = paso <= 0 ? Insignia.Cuadro(n) : Insignia.Girada(n, -paso * (360f / Pasos));
            if (c.ToArgb() != Tema.Apagado.ToArgb()) Luz(bmp, c);
            return bmp;
        }

        /// <summary>
        /// `--insignia`: los cuadros del ícono tal como salen de acá, sobre el gris de la barra de tareas. Arriba los
        /// cuatro estados (×8 y a tamaño real); abajo la vuelta completa de «transmitiendo». Para mirarlo sin modelo.
        /// </summary>
        public static void Muestra(int n, string salida)
        {
            var estados = new[] { Tema.Apagado, Tema.Teal, Tema.Malva, Tema.Ambar };
            const int z = 8, zg = 2, m = 24, sep = 6;
            int porFila = Pasos / 2;
            int ancho = Math.Max(m + estados.Length * (n * z + m), m + porFila * (n * zg + sep) + m);
            int yGiro = m + n * z + m + n + m;
            int alto = yGiro + 2 * (n * zg + sep) + m;
            using (var hoja = new Bitmap(ancho, alto, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(hoja))
            {
                g.Clear(Color.FromArgb(28, 28, 28));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int i = 0; i < estados.Length; i++)
                    using (var b = Cuadro(n, estados[i], 0))
                    {
                        int x = m + i * (n * z + m);
                        g.DrawImage(b, new Rectangle(x, m, n * z, n * z), 0, 0, n, n, GraphicsUnit.Pixel);
                        g.DrawImage(b, new Rectangle(x, m + n * z + m, n, n), 0, 0, n, n, GraphicsUnit.Pixel);
                    }
                for (int p = 0; p < Pasos; p++)
                    using (var b = Cuadro(n, Tema.Malva, p))
                    {
                        int x = m + (p % porFila) * (n * zg + sep), y = yGiro + (p / porFila) * (n * zg + sep);
                        g.DrawImage(b, new Rectangle(x, y, n * zg, n * zg), 0, 0, n, n, GraphicsUnit.Pixel);
                    }
                hoja.Save(salida, ImageFormat.Png);
            }
        }

        void Vaciar()
        {
            foreach (var c in cuadros.Values) { c.Icono.Dispose(); Win32.DestroyIcon(c.H); }
            cuadros.Clear();
        }

        /// <summary>La luz de estado, abajo a la derecha, con un aro oscuro que la despega de la galaxia.</summary>
        static void Luz(Bitmap bmp, Color c)
        {
            int n = bmp.Width;
            int d = Math.Max(5, (int)Math.Round(n * 0.27));     // 16 → 5, 24 → 6, 32 → 9: avisa sin tapar la galaxia
            int aro = n >= 32 ? 2 : 1;
            int x = n - d - aro, y = n - d - aro;
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var b = new SolidBrush(Tema.Fondo)) g.FillEllipse(b, x - aro, y - aro, d + 2 * aro, d + 2 * aro);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y, d, d);
            }
        }

        public void Dispose()
        {
            animacion.Stop();
            animacion.Dispose();
            icono.Visible = false;
            icono.Dispose();
            menu.Dispose();
            Vaciar();
        }
    }

    /// <summary>Menú contextual oscuro: Windows no sabe pintarlo solo.</summary>
    internal sealed class RenderOscuro : ToolStripProfessionalRenderer
    {
        public RenderOscuro() : base(new Colores()) { RoundedEdges = false; }
        sealed class Colores : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Tema.Alza;
            public override Color MenuItemBorder => Tema.Alpha(Tema.Malva, 120);
            public override Color MenuBorder => Tema.Filete;
            public override Color ToolStripDropDownBackground => Tema.Panel;
            public override Color ImageMarginGradientBegin => Tema.Panel;
            public override Color ImageMarginGradientMiddle => Tema.Panel;
            public override Color ImageMarginGradientEnd => Tema.Panel;
            public override Color SeparatorDark => Tema.Filete;
            public override Color SeparatorLight => Tema.Panel;
            public override Color MenuItemSelectedGradientBegin => Tema.Alza;
            public override Color MenuItemSelectedGradientEnd => Tema.Alza;
            public override Color MenuItemPressedGradientBegin => Tema.Consola;
            public override Color MenuItemPressedGradientEnd => Tema.Consola;
            public override Color CheckBackground => Tema.Panel;
            public override Color CheckSelectedBackground => Tema.Alza;
            public override Color CheckPressedBackground => Tema.Consola;
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? (e.Item.Selected ? Tema.Texto : Tema.Suave) : Tema.Apagado;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            int d = Math.Max(6, r.Height / 3);
            using (var b = new SolidBrush(Tema.Teal)) g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Tema.Filete)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }
}
