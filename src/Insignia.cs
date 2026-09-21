using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// La insignia de CAPCOM (la galaxia, `assets\galaxia.png`) leída de los recursos del PROPIO exe: el .ico trae un
    /// frame PNG armado para cada tamaño (16…256, ver `assets\make-icon.py`) y acá se saca tal cual, sin reescalar.
    ///
    /// 🚨 Windows no elige bien por su cuenta: un ícono de otro tamaño lo reescala y queda borroso. La bandeja copia
    /// píxel por píxel lo que se le da (medido el 21-sep-2026 contra la bandeja real: 0,1 de diferencia con el frame
    /// de 24), así que la bandeja y la ventana piden siempre el tamaño que de verdad se va a dibujar.
    /// </summary>
    internal static class Insignia
    {
        const int RT_ICON = 3, RT_GROUP_ICON = 14;

        delegate bool EnumNombres(IntPtr modulo, IntPtr tipo, IntPtr nombre, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string nombre);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool EnumResourceNames(IntPtr modulo, IntPtr tipo, EnumNombres cb, IntPtr param);
        [DllImport("kernel32.dll")] static extern IntPtr FindResource(IntPtr modulo, IntPtr nombre, IntPtr tipo);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindResource(IntPtr modulo, string nombre, IntPtr tipo);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr modulo, IntPtr recurso);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr datos);
        [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr modulo, IntPtr recurso);
        [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] datos, uint largo, bool esIcono, uint version, int cx, int cy, uint flags);

        static Dictionary<int, byte[]> frames;
        static readonly Dictionary<int, Bitmap> fuentesGiro = new Dictionary<int, Bitmap>();

        /// <summary>Los PNG del ícono del exe, por ancho real (el directorio dice 0 para 256: se lee el IHDR).</summary>
        static Dictionary<int, byte[]> Frames()
        {
            if (frames != null) return frames;
            var f = new Dictionary<int, byte[]>();
            try
            {
                IntPtr mod = GetModuleHandle(null);
                // el grupo de íconos: el primero que haya (el compilador lo numera, no hace falta adivinar el id)
                IntPtr id = IntPtr.Zero;
                string nombre = null;
                EnumResourceNames(mod, (IntPtr)RT_GROUP_ICON, (m, t, n, p) =>
                {
                    if (((long)n >> 16) == 0) id = n; else nombre = Marshal.PtrToStringUni(n);
                    return false;
                }, IntPtr.Zero);
                IntPtr grupo = nombre != null ? FindResource(mod, nombre, (IntPtr)RT_GROUP_ICON)
                                              : FindResource(mod, id, (IntPtr)RT_GROUP_ICON);
                byte[] dir = Leer(mod, grupo);
                int cuantos = dir != null && dir.Length >= 6 ? BitConverter.ToUInt16(dir, 4) : 0;
                for (int i = 0; i < cuantos && 6 + 14 * i + 14 <= dir.Length; i++)
                {
                    int nId = BitConverter.ToUInt16(dir, 6 + 14 * i + 12);
                    byte[] png = Leer(mod, FindResource(mod, (IntPtr)nId, (IntPtr)RT_ICON));
                    if (png == null || png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E) continue;
                    int ancho = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
                    if (ancho > 0) f[ancho] = png;
                }
            }
            catch { }
            frames = f;
            return f;
        }

        static byte[] Leer(IntPtr mod, IntPtr recurso)
        {
            if (recurso == IntPtr.Zero) return null;
            IntPtr p = LockResource(LoadResource(mod, recurso));
            int n = (int)SizeofResource(mod, recurso);
            if (p == IntPtr.Zero || n <= 0) return null;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return b;
        }

        /// <summary>Copia píxel por píxel: un Bitmap abierto desde un stream necesita el stream vivo.</summary>
        static Bitmap Decodificar(byte[] png)
        {
            using (var ms = new MemoryStream(png))
            using (var src = new Bitmap(ms))
            {
                var b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(b))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
                }
                return b;
            }
        }

        /// <summary>El frame de n×n: el exacto del .ico si existe; si no, el más cercano por arriba, reducido.</summary>
        public static Bitmap Cuadro(int n)
        {
            var f = Frames();
            byte[] png;
            if (f.TryGetValue(n, out png)) return Decodificar(png);
            if (f.Count == 0) return Respaldo(n);
            int mayor = f.Keys.Where(k => k >= n).DefaultIfEmpty(f.Keys.Max()).Min();
            using (var src = Decodificar(f[mayor])) return Reducir(src, n, 0f);
        }

        /// <summary>
        /// HICON de n×n desde el PNG del frame, por el cargador de íconos de Windows. El handle es de quien llama
        /// (DestroyIcon).
        ///
        /// 🚨 NO usar Bitmap.GetHicon(): entrega el color YA multiplicado por el alfa y Windows lo vuelve a multiplicar
        /// al dibujar, así que todo borde semitransparente sale oscuro. Medido el 21-sep-2026 en la bandeja real: 22
        /// niveles de diferencia media en los píxeles de alfa parcial (el modelo c·a² calza con 0,4); por PNG, exacto.
        /// </summary>
        public static IntPtr HIcon(int n)
        {
            byte[] png;
            if (Frames().TryGetValue(n, out png))
            {
                IntPtr h = DesdePng(png, n);
                if (h != IntPtr.Zero) return h;
            }
            using (var b = Cuadro(n)) return HIcon(b);
        }

        /// <summary>Lo mismo para un cuadro compuesto acá (girado, con la luz): se pasa a PNG en memoria.</summary>
        public static IntPtr HIcon(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                IntPtr h = DesdePng(ms.ToArray(), bmp.Width);
                return h != IntPtr.Zero ? h : bmp.GetHicon();       // antes con el borde oscuro que sin ícono
            }
        }

        static IntPtr DesdePng(byte[] png, int n) => CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, n, n, 0);

        /// <summary>Los tamaños que trae el .ico, de menor a mayor.</summary>
        public static int[] Tamanos() => Frames().Keys.OrderBy(k => k).ToArray();

        /// <summary>El tamaño de frame más parecido a `n` (si empatan, el más grande): para no reescalar por 1 px.</summary>
        public static int MasCercano(int n)
        {
            var t = Tamanos();
            if (t.Length == 0) return n;
            return t.OrderBy(k => Math.Abs(k - n)).ThenByDescending(k => k).First();
        }

        /// <summary>
        /// La galaxia girada `grados` (negativo = antihorario, que es como gira una espiral cuyos brazos se abren en
        /// sentido horario: los brazos se arrastran). Sale de un frame de 4× y se reduce, así el giro no la emborrona.
        /// </summary>
        public static Bitmap Girada(int n, float grados)
        {
            Bitmap src;
            if (!fuentesGiro.TryGetValue(n, out src))
            {
                var f = Frames();
                if (f.Count == 0) src = Respaldo(n * 4);
                else src = Decodificar(f[f.Keys.Where(k => k >= n * 4).DefaultIfEmpty(f.Keys.Max()).Min()]);
                fuentesGiro[n] = src;
            }
            return Reducir(src, n, grados);
        }

        static Bitmap Reducir(Bitmap src, int n, float grados)
        {
            var b = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            using (var ia = new ImageAttributes())
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                if (grados != 0f)
                {
                    g.TranslateTransform(n / 2f, n / 2f);
                    g.RotateTransform(grados);
                    g.TranslateTransform(-n / 2f, -n / 2f);
                }
                ia.SetWrapMode(WrapMode.TileFlipXY);    // sin el filo oscuro que deja el bicúbico en el contorno
                g.DrawImage(src, new Rectangle(0, 0, n, n), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return b;
        }

        /// <summary>Sin recursos legibles (no debería pasar): el ícono asociado del exe, que Windows sabe sacar.</summary>
        static Bitmap Respaldo(int n)
        {
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                using (var b = ic.ToBitmap())
                    return Reducir(b, n, 0f);
            }
            catch { return new Bitmap(n, n, PixelFormat.Format32bppArgb); }
        }
    }

    /// <summary>
    /// La insignia en la barra de título. Quieta mientras no pasa nada; gira mientras hay transmisión y, al terminar,
    /// completa la vuelta frenando hasta quedar derecha. Igual que la de la bandeja: sólo se mueve si algo pasa.
    /// </summary>
    internal sealed class Galaxia : Control
    {
        readonly Timer reloj = new Timer { Interval = 40 };
        float angulo;               // grados ya girados, 0..360; se dibuja en antihorario
        bool activa;
        Bitmap quieta;

        public Galaxia()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Panel;
            TabStop = false;
            reloj.Tick += (s, e) =>
            {
                if (activa) angulo += 4f;                                   // 25 tics/s × 4° = una vuelta cada 3,6 s
                else angulo += Math.Max(1.5f, (360f - angulo) * 0.14f);     // el resto de la vuelta, frenando
                if (angulo >= 360f)
                {
                    angulo = activa ? angulo - 360f : 0f;
                    if (!activa) reloj.Stop();
                }
                Invalidate();
            };
        }

        public bool Activa
        {
            get { return activa; }
            set
            {
                if (activa == value) return;
                activa = value;
                if (activa && IsHandleCreated) reloj.Start();   // al apagarse, el tic termina la vuelta y se detiene solo
            }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (activa) reloj.Start(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { reloj.Dispose(); if (quieta != null) { quieta.Dispose(); quieta = null; } }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(BackColor);
            int lado = Math.Min(Width, Height);
            if (lado < 8) return;
            int x = (Width - lado) / 2, y = (Height - lado) / 2;
            // píxel por píxel: DrawImageUnscaled NO sirve, reescala por la diferencia de dpi entre el bitmap y la ventana
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (angulo <= 0f)
            {
                if (quieta == null || quieta.Width != lado) { if (quieta != null) quieta.Dispose(); quieta = Insignia.Cuadro(lado); }
                g.DrawImage(quieta, new Rectangle(x, y, lado, lado), 0, 0, lado, lado, GraphicsUnit.Pixel);
            }
            else
            {
                using (var b = Insignia.Girada(lado, -angulo))
                    g.DrawImage(b, new Rectangle(x, y, lado, lado), 0, 0, lado, lado, GraphicsUnit.Pixel);
            }
        }
    }
}
