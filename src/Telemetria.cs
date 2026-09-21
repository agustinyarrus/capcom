using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// Serie temporal con área: el instrumento que muestra cómo viene algo, no cuánto vale ahora. Guarda su
    /// propio anillo de muestras, así el que la usa sólo tiene que empujar números.
    /// </summary>
    internal sealed class Serie : Control
    {
        readonly Queue<double> datos = new Queue<double>();
        public int Limite = 180;
        public string Etiqueta = "";
        public Color Color = Tema.Teal;
        public Func<double, string> Formato = v => v.ToString("0.0");
        public double Piso = 0, Techo = double.NaN;    // NaN = automático
        public bool Rellenar = true;
        public string Vacio = "sin muestras";

        public Serie()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public void Empujar(double v)
        {
            datos.Enqueue(v);
            while (datos.Count > Limite) datos.Dequeue();
            Invalidate();
        }
        public void Limpiar() { datos.Clear(); Invalidate(); }
        public double Ultimo => datos.Count > 0 ? datos.Last() : 0;
        public double Pico => datos.Count > 0 ? datos.Max() : 0;
        public double Promedio => datos.Count > 0 ? datos.Average() : 0;
        public int Muestras => datos.Count;

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(BackColor);
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            int top = Etiqueta.Length > 0 ? S(15) : S(2);
            var r = new Rectangle(0, top, Width, Math.Max(S(10), Height - top - S(2)));

            if (Etiqueta.Length > 0)
                Tema.Tracking(g, Etiqueta.ToUpperInvariant(), Tema.Media(7f), Tema.Apagado, 0, 0, S(12), S(2));

            var v = datos.ToArray();
            if (v.Length < 2)
            {
                Tema.Texto_(g, Vacio, Tema.Fina(8f), Tema.Fantasma, r);
                return;
            }
            double max = double.IsNaN(Techo) ? Math.Max(v.Max(), 1e-6) : Techo;
            double min = Piso;
            if (max <= min) max = min + 1;

            var pts = new PointF[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float x = r.X + (float)i / (v.Length - 1) * (r.Width - 1);
                float t = (float)((v[i] - min) / (max - min));
                t = Math.Max(0, Math.Min(1, t));
                pts[i] = new PointF(x, r.Bottom - t * (r.Height - 1));
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Rellenar)
            {
                var poly = new List<PointF>(pts) { new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) };
                using (var b = new LinearGradientBrush(r, Tema.Alpha(Color, 58), Tema.Alpha(Color, 4), LinearGradientMode.Vertical))
                    g.FillPolygon(b, poly.ToArray());
            }
            using (var p = new Pen(Tema.Alpha(Color, 215), Math.Max(1f, S(1)))) g.DrawLines(p, pts);
            var ult = pts[pts.Length - 1];
            using (var b = new SolidBrush(Color)) g.FillEllipse(b, ult.X - S(2), ult.Y - S(2), S(4), S(4));
            using (var b = new SolidBrush(Tema.Alpha(Color, 45))) g.FillEllipse(b, ult.X - S(5), ult.Y - S(5), S(10), S(10));

            string sv = Formato(v[v.Length - 1]);
            var f = Tema.MonoMedia(8.5f);
            int w = Tema.Medir(g, sv, f).Width;
            Tema.Texto_(g, sv, f, Color, new Rectangle(Width - w - S(2), top, w + S(3), S(14)), TextFormatFlags.Right | TextFormatFlags.Top);
        }
    }

    /// <summary>
    /// El anillo orbital animado: gira mientras hay transmisión y se apaga cuando no. Es el único elemento de la
    /// interfaz con movimiento propio, y sólo se mueve cuando de verdad está pasando algo.
    /// </summary>
    internal sealed class Orbita : Control
    {
        readonly Timer reloj = new Timer { Interval = 40 };
        float fase;
        float actividad;          // 0..1, se va apagando sola
        public Color Acento = Tema.Malva;
        public bool Activa;
        public string Centro = "";

        public Orbita()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
            reloj.Tick += (s, e) =>
            {
                float objetivo = Activa ? 1f : 0f;
                actividad += (objetivo - actividad) * 0.08f;
                if (Activa || actividad > 0.02f) { fase += Activa ? 0.011f : 0.004f; if (fase > 1) fase -= 1; Invalidate(); }
                else if (actividad <= 0.02f && actividad != 0) { actividad = 0; Invalidate(); }
            };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); reloj.Start(); }
        protected override void Dispose(bool disposing) { if (disposing) reloj.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(BackColor);
            float e = g.DpiX / 96f;
            int lado = Math.Min(Width, Height);
            var r = new RectangleF((Width - lado) / 2f + 1, (Height - lado) / 2f + 1, lado - 2, lado - 2);
            Tema.Orbital(g, r, fase, Acento, Math.Max(actividad, Activa ? 1f : 0f));
            if (Centro.Length > 0)
            {
                var f = Tema.Mono(7f);
                Tema.Texto_(g, Centro, f, Tema.Alpha(Tema.Texto, 200), ClientRectangle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>La cinta de eventos: hora, riel vertical, punto de color y texto. El diario de vuelo.</summary>
    internal sealed class LineaTiempo : Control, IRueda
    {
        readonly List<LineaLog> lineas = new List<LineaLog>();
        int desplaz;
        public int Capacidad = 800;
        public string Vacio = "todavía no pasó nada";

        public LineaTiempo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Panel;
            Font = Tema.Fina(9f);
            TabStop = false;
        }

        public void Cargar(IEnumerable<LineaLog> ls) { lineas.Clear(); lineas.AddRange(ls); Recortar(); desplaz = 0; Invalidate(); }
        public void Agregar(LineaLog l)
        {
            lineas.Add(l);
            Recortar();
            if (desplaz > 0) desplaz = Math.Min(desplaz + 1, Math.Max(0, lineas.Count - 1));
            Invalidate();
        }
        void Recortar() { while (lineas.Count > Capacidad) lineas.RemoveAt(0); }

        public void Rueda(int delta)
        {
            float e = Dpi.Escala(this);
            int fila = Dpi.S(e, 18);
            int visibles = Math.Max(1, (Height - Dpi.S(e, 12)) / fila);
            int max = Math.Max(0, lineas.Count - visibles);
            desplaz = Math.Max(0, Math.Min(max, desplaz + (delta > 0 ? 3 : -3)));
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        public static Color ColorDe(Nivel n)
        {
            switch (n)
            {
                case Nivel.Debug: return Tema.Fantasma;
                case Nivel.Ok: return Tema.Salvia;
                case Nivel.Aviso: return Tema.Crema;
                case Nivel.Alerta: return Tema.Ambar;
                case Nivel.Error: return Tema.Rosa;
                default: return Tema.Cielo;
            }
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(BackColor);
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            int fila = S(18), pad = S(7);
            int visibles = Math.Max(1, (Height - pad * 2) / fila);
            int fin = Math.Max(0, lineas.Count - desplaz);
            int ini = Math.Max(0, fin - visibles);
            var fHora = Tema.Mono(8f);
            // 🚨 el ancho de la hora se MIDE: con un valor fijo «00:39:28» salía «00:39:2…»
            int xHora = S(10), wHora = Tema.Medir(g, "00:00:00", fHora).Width + S(4);
            int xRiel = xHora + wHora + S(9), xTexto = xRiel + S(14);
            var fFuerte = Tema.Media(9f);

            using (var pr = new Pen(Tema.Alpha(Tema.Filete, 190), 1f)) g.DrawLine(pr, xRiel, pad, xRiel, Height - pad);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (lineas.Count == 0)
            {
                Tema.Texto_(g, Vacio, Tema.Fina(8.5f), Tema.Fantasma, new Rectangle(xTexto, 0, Width - xTexto, Height));
                return;
            }
            int y = pad;
            for (int i = ini; i < fin; i++, y += fila)
            {
                var l = lineas[i];
                var c = ColorDe(l.Nivel);
                Tema.Texto_(g, l.Hora.ToString("HH:mm:ss"), fHora, Tema.Alpha(Tema.Apagado, 190), new Rectangle(xHora, y, wHora, fila));
                int d = S(l.Nivel >= Nivel.Aviso ? 6 : 4);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, xRiel - d / 2f, y + (fila - d) / 2f, d, d);
                if (l.Nivel >= Nivel.Alerta)
                    using (var p = new Pen(Tema.Alpha(c, 45), S(3))) g.DrawEllipse(p, xRiel - d / 2f - 2, y + (fila - d) / 2f - 2, d + 4, d + 4);
                var font = l.Nivel >= Nivel.Aviso ? fFuerte : Font;
                var ct = l.Nivel == Nivel.Debug ? Tema.Apagado : l.Nivel == Nivel.Info ? Tema.Texto : c;
                Tema.Texto_(g, l.Texto, font, ct, new Rectangle(xTexto, y, Width - xTexto - S(10), fila),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            if (desplaz > 0)
            {
                string s = "▾ " + desplaz + " más abajo";
                var f = Tema.Media(8f);
                var sz = Tema.Medir(g, s, f);
                var r = new RectangleF(Width - sz.Width - S(28), Height - sz.Height - S(16), sz.Width + S(16), sz.Height + S(8));
                Tema.Placa(g, r, S(2), Tema.Mezcla(Tema.Consola, Tema.Malva, 0.15f), Tema.Alpha(Tema.Malva, 150));
                Tema.Texto_(g, s, f, Tema.Malva, Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>Etiqueta → valor, en columnas, con punto de color. Para las fichas de estado.</summary>
    internal sealed class Ficha : Control, IRueda
    {
        public sealed class Item { public string Clave = "", Valor = ""; public Color Color = Tema.Suave; public bool Mono = true; }
        public List<Item> Items = new List<Item>();
        public string Etiqueta = "";
        public string Indice = "";
        public Color Acento = Tema.Cielo;
        public string Vacio = "sin datos";
        int desplaz;

        public Ficha()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public void Poner(params Item[] items) { Items = items.ToList(); Invalidate(); }
        public static Item I(string k, string v, Color? c = null) => new Item { Clave = k, Valor = v, Color = c ?? Tema.Texto };

        public void Rueda(int delta)
        {
            float e = Dpi.Escala(this);
            int fila = Dpi.S(e, 17);
            int visibles = Math.Max(1, (Height - Dpi.S(e, 22)) / fila);
            int max = Math.Max(0, Items.Count - visibles);
            desplaz = Math.Max(0, Math.Min(max, desplaz - Math.Sign(delta)));
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            int y = 0;
            if (Etiqueta.Length > 0)
            {
                Tema.Rotulo(g, Indice, Etiqueta, new Rectangle(0, 0, Width, S(14)), Acento, e);
                y = S(20);
            }
            if (Items.Count == 0) { Tema.Texto_(g, Vacio, Tema.Fina(8.5f), Tema.Fantasma, new Rectangle(0, y, Width, S(18))); return; }
            var fk = Tema.Fina(8.5f);
            int fila = S(17);
            int anchoClave = 0;
            foreach (var it in Items) anchoClave = Math.Max(anchoClave, Tema.Medir(g, it.Clave, fk).Width);
            anchoClave = Math.Min(anchoClave, Width / 2);
            for (int i = desplaz; i < Items.Count && y < Height; i++, y += fila)
            {
                var it = Items[i];
                Tema.Texto_(g, it.Clave, fk, Tema.Apagado, new Rectangle(0, y, anchoClave + S(3), fila));
                var fv = it.Mono ? Tema.MonoMedia(8.5f) : Tema.Media(8.5f);
                Tema.Texto_(g, it.Valor, fv, it.Color, new Rectangle(anchoClave + S(10), y, Width - anchoClave - S(10), fila),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
