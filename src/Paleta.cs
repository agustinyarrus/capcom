using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    internal sealed class Comando
    {
        public string Titulo, Nota, Atajo;
        public Action Hacer;
        public int Puntaje;
        public Comando(string t, string n, string a, Action h) { Titulo = t; Nota = n; Atajo = a; Hacer = h; }
    }

    /// <summary>
    /// La paleta de comandos (Ctrl+K): un velo sobre toda la ventana y una caja centrada. Filtra por
    /// subsecuencia — escribir «ntr» encuentra «Transmisión nueva» — y premia que las letras caigan al
    /// principio de una palabra, que es como uno tipea cuando tiene apuro.
    /// </summary>
    internal sealed class Paleta : Control
    {
        readonly Nucleo N;
        readonly CajaTexto caja = new CajaTexto { Multiline = false, Pista = "" };
        List<Comando> todos = new List<Comando>();
        List<Comando> vistos = new List<Comando>();
        int sel, hover = -1;
        float esc = 1f;
        readonly List<Rectangle> rects = new List<Rectangle>();

        public event Action<Comando> Elegida;

        public Paleta(Nucleo n)
        {
            N = n;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
            caja.BorderStyle = BorderStyle.None;
            caja.Multiline = false;
            caja.ScrollBars = ScrollBars.None;
            caja.BackColor = Tema.Consola;
            caja.ForeColor = Tema.Texto;
            caja.Font = Tema.Fina(11.5f);
            caja.TextChanged += (s, e) => { Filtrar(); Invalidate(); };
            caja.KeyDown += Tecla;
            Controls.Add(caja);
        }

        int S(int px) => Dpi.S(esc, px);

        public void Abrir(List<Comando> comandos)
        {
            todos = comandos;
            caja.Text = "";
            sel = 0;
            Filtrar();
            Visible = true;
            BringToFront();
            Acomodar();
            caja.Focus();
            Invalidate();
        }

        public void Cerrar()
        {
            Visible = false;
            var f = FindForm();
            if (f != null) f.Invalidate(true);
        }

        void Tecla(object s, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Down: sel = Math.Min(vistos.Count - 1, sel + 1); Invalidate(); e.Handled = e.SuppressKeyPress = true; break;
                case Keys.Up: sel = Math.Max(0, sel - 1); Invalidate(); e.Handled = e.SuppressKeyPress = true; break;
                case Keys.Enter:
                    if (sel >= 0 && sel < vistos.Count) Elegida?.Invoke(vistos[sel]);
                    e.Handled = e.SuppressKeyPress = true;
                    break;
                case Keys.Escape: Cerrar(); e.Handled = e.SuppressKeyPress = true; break;
            }
        }

        void Filtrar()
        {
            string q = caja.Text.Trim();
            if (q.Length == 0) { vistos = todos.Take(14).ToList(); sel = 0; return; }
            foreach (var c in todos) c.Puntaje = Puntaje(c.Titulo + " " + c.Nota, q);
            vistos = todos.Where(c => c.Puntaje > 0).OrderByDescending(c => c.Puntaje).Take(14).ToList();
            sel = 0;
        }

        /// <summary>Coincidencia por subsecuencia, con premio a los golpes al principio de palabra y seguidos.</summary>
        static int Puntaje(string texto, string q)
        {
            if (string.IsNullOrEmpty(q)) return 1;
            string t = texto.ToLowerInvariant();
            q = q.ToLowerInvariant();
            int p = 0, puntos = 0, seguidos = 0;
            for (int i = 0; i < q.Length; i++)
            {
                if (q[i] == ' ') { seguidos = 0; continue; }
                int k = t.IndexOf(q[i], p);
                if (k < 0) return 0;
                puntos += 10;
                if (k == 0 || t[k - 1] == ' ' || t[k - 1] == ':') puntos += 22;    // arranque de palabra
                if (k == p) { seguidos++; puntos += 6 * seguidos; } else seguidos = 0;
                p = k + 1;
            }
            return puntos + Math.Max(0, 30 - texto.Length / 3);
        }

        public void Acomodar()
        {
            esc = Dpi.Escala(this);
            int w = Math.Min(S(620), Width - S(80));
            int x = (Width - w) / 2, y = Math.Max(S(40), Height / 6);
            caja.SetBounds(x + S(40), y + S(13), w - S(56), Math.Max(S(18), caja.PreferredHeight));
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Acomodar(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = rects.FindIndex(r => r.Contains(e.Location));
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = rects.FindIndex(r => r.Contains(e.Location));
            if (i >= 0 && i < vistos.Count) { Elegida?.Invoke(vistos[i]); return; }
            int w = Math.Min(S(620), Width - S(80));
            var caw = new Rectangle((Width - w) / 2, Math.Max(S(40), Height / 6), w, S(46) + vistos.Count * S(34) + S(10));
            if (!caw.Contains(e.Location)) Cerrar();    // clic afuera = cerrar
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            esc = Dpi.Escala(this);
            rects.Clear();

            // velo
            using (var b = new SolidBrush(Color.FromArgb(190, 4, 5, 7))) g.FillRectangle(b, ClientRectangle);

            int w = Math.Min(S(620), Width - S(80));
            int x = (Width - w) / 2, y = Math.Max(S(40), Height / 6);
            int alto = S(46) + vistos.Count * S(34) + S(12);
            var r = new RectangleF(x, y, w, alto);
            Tema.Placa(g, r, S(3), Tema.Panel, Tema.Alpha(Tema.Malva, 110));
            Tema.Esquinas(g, new RectangleF(x - S(5), y - S(5), w + S(10), alto + S(10)), S(12), Tema.Alpha(Tema.Malva, 90));

            // prompt
            Tema.Texto_(g, "›", Tema.MonoMedia(13f), Tema.Malva, new Rectangle(x + S(18), y + S(10), S(20), S(24)));
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 235), 1f)) g.DrawLine(p, x + S(12), y + S(44), x + w - S(12), y + S(44));
            if (caja.TextLength == 0)
                Tema.Texto_(g, "buscá un comando, una persona o una transmisión…", Tema.Fina(10.5f), Tema.Fantasma,
                    new Rectangle(x + S(42), y + S(12), w - S(60), S(22)));

            int yy = y + S(52);
            var fT = Tema.Fina(10f);
            var fN = Tema.Fina(8.5f);
            var fA = Tema.Mono(8f);
            for (int i = 0; i < vistos.Count; i++, yy += S(34))
            {
                var c = vistos[i];
                var rf = new Rectangle(x + S(8), yy - S(4), w - S(16), S(32));
                rects.Add(rf);
                bool act = i == sel, hov = i == hover;
                if (act || hov)
                {
                    using (var b = new SolidBrush(act ? Tema.Alpha(Tema.Malva, 26) : Tema.Alpha(Tema.Texto, 12)))
                        g.FillRectangle(b, rf);
                    using (var b = new SolidBrush(Tema.Alpha(Tema.Malva, act ? 235 : 110))) g.FillRectangle(b, rf.X, rf.Y, S(2), rf.Height);
                }
                Tema.Texto_(g, c.Titulo, fT, act ? Tema.Texto : Tema.Suave, new Rectangle(rf.X + S(12), rf.Y + S(2), rf.Width - S(90), S(15)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (!string.IsNullOrEmpty(c.Nota))
                    Tema.Texto_(g, c.Nota, fN, Tema.Apagado, new Rectangle(rf.X + S(12), rf.Y + S(16), rf.Width - S(90), S(14)),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (!string.IsNullOrEmpty(c.Atajo))
                {
                    int wa = Tema.Medir(g, c.Atajo, fA).Width;
                    var ra = new RectangleF(rf.Right - wa - S(18), rf.Y + S(8), wa + S(12), S(16));
                    Tema.Placa(g, ra, S(2), Tema.Alpha(Tema.Consola, 220), Tema.Alpha(Tema.Filete, 230));
                    Tema.Texto_(g, c.Atajo, fA, Tema.Apagado, Rectangle.Round(ra), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            if (vistos.Count == 0)
                Tema.Texto_(g, "nada con eso", Tema.Fina(9.5f), Tema.Fantasma, new Rectangle(x + S(20), y + S(52), w - S(40), S(26)));

            // pie de ayuda
            var fp = Tema.Mono(7.5f);
            Tema.Texto_(g, "↑↓ para moverse   ⏎ para ejecutar   Esc para cerrar", fp, Tema.Fantasma,
                new Rectangle(x, (int)r.Bottom + S(8), w, S(14)), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }
}
