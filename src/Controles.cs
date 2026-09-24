using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>Un control que sabe desplazarse con la rueda sin robarle el foco al teclado.</summary>
    internal interface IRueda { void Rueda(int delta); }

    /// <summary>
    /// La pastilla de la casa: una PLACA recta con un riel de acento pegado al canto izquierdo. El riel es la
    /// identidad del control y, en los toggles, también el estado. Nada de botones redondos con relleno saturado.
    /// </summary>
    internal sealed class Chip : Control
    {
        public enum Modo { Boton, Toggle, Valor, Radio }
        public Modo Tipo = Modo.Boton;
        public bool Activo;
        public Color Acento = Tema.Malva;
        public string Sub = "";
        // La pintura las lee aunque hoy ningún llamador las setee: con el valor explícito no salta el CS0649.
        public bool Armado = false;     // "¿seguro?" para lo que no tiene vuelta atrás
        public bool Destacado = false;  // borde siempre con el acento
        /// <summary>Color de lo que hay pintado DETRÁS: sin esto las esquinas de la placa quedan del color equivocado.</summary>
        public Color Superficie = Tema.Fondo;
        bool hover, presionado;
        public event EventHandler Accion;
        public event EventHandler AccionDerecha;

        public Chip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Tema.Fondo;
            Font = Tema.Fina(9.5f);
            TabStop = false;
        }

        public void Poner(string texto, string sub = null)
        {
            Text = texto;
            if (sub != null) Sub = sub;
            Ajustar();
            Invalidate();
        }

        public void Ajustar()
        {
            using (var g = CreateGraphics())
            {
                float e = g.DpiX / 96f;
                int w = Tema.Medir(g, Text, Font).Width + Dpi.S(e, 12) + Dpi.S(e, 12);
                if (Tipo == Modo.Toggle || Tipo == Modo.Radio) w += Dpi.S(e, 7) + Dpi.S(e, 8);
                if (Sub.Length > 0) w += Tema.Medir(g, Sub, Tema.Media(9.5f)).Width + Dpi.S(e, 8);
                Width = w;
                Height = Dpi.S(e, 26);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; presionado = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { presionado = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool adentro = presionado && ClientRectangle.Contains(e.Location);
            presionado = false; Invalidate();
            base.OnMouseUp(e);
            if (!adentro) return;
            if (e.Button == MouseButtons.Left) Accion?.Invoke(this, EventArgs.Empty);
            else if (e.Button == MouseButtons.Right) AccionDerecha?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            g.Clear(Superficie);

            bool marcado = (Tipo == Modo.Toggle || Tipo == Modo.Radio) && Activo;
            bool vivo = hover || presionado;
            Color acento = Armado ? Tema.Rosa : Acento;
            float radio = S(2);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);

            Color fondo;
            if (Armado) fondo = Tema.Mezcla(Superficie, Tema.Rosa, 0.20f);
            else if (presionado) fondo = Tema.Mezcla(Superficie, acento, 0.24f);
            else if (marcado) fondo = Tema.Mezcla(Superficie, acento, hover ? 0.16f : 0.11f);
            else if (hover) fondo = Tema.Mezcla(Superficie, Tema.Texto, 0.075f);
            else fondo = Tema.Mezcla(Superficie, Tema.Texto, 0.025f);

            Color borde = Armado ? Tema.Alpha(Tema.Rosa, 190)
                        : marcado ? Tema.Alpha(acento, 145)
                        : Destacado ? Tema.Alpha(acento, 125)
                        : vivo ? Tema.Alpha(Tema.Suave, 78)
                        : Tema.Alpha(Tema.Suave, 28);
            Tema.Placa(g, r, radio, fondo, borde);

            int alfaRiel = Armado ? 245 : marcado ? 255 : Destacado ? 225 : vivo ? 205 : (Tipo == Modo.Boton ? 150 : 80);
            using (var placa = Tema.Redondeado(r, radio))
            {
                var recorte = g.Clip;
                g.SetClip(placa, CombineMode.Intersect);
                using (var b = new SolidBrush(Tema.Alpha(acento, alfaRiel))) g.FillRectangle(b, 0f, 0f, S(3), Height);
                g.Clip = recorte;
            }

            int x = S(12);
            if (Tipo == Modo.Toggle || Tipo == Modo.Radio)
            {
                int d = S(7);
                var rm = new RectangleF(x, (float)Math.Round((Height - d) / 2f), d, d);
                if (Tipo == Modo.Radio)
                {
                    using (var p = new Pen(Tema.Alpha(Tema.Suave, vivo ? 165 : 120), 1f)) g.DrawEllipse(p, rm);
                    if (Activo) using (var b = new SolidBrush(acento)) g.FillEllipse(b, rm.X + 2, rm.Y + 2, rm.Width - 4, rm.Height - 4);
                }
                else
                {
                    if (Activo) { using (var b = new SolidBrush(acento)) g.FillRectangle(b, rm); }
                    else { using (var p = new Pen(Tema.Alpha(Tema.Suave, vivo ? 165 : 120), 1f)) g.DrawRectangle(p, rm.X, rm.Y, rm.Width, rm.Height); }
                }
                x += d + S(8);
            }

            var fSub = Tema.Media(9.5f);
            int anchoSub = Sub.Length > 0 && !Armado ? Tema.Medir(g, Sub, fSub).Width : 0;
            int reservaDerecha = anchoSub > 0 ? anchoSub + S(20) : S(12);

            Color ct = Armado ? Tema.Rosa : marcado ? Tema.Texto : Destacado ? acento : vivo ? Tema.Texto : Tema.Suave;
            string texto = Armado ? "¿seguro? clic de nuevo" : Text;
            var rt = new Rectangle(x, 0, Math.Max(S(10), Width - x - reservaDerecha), Height);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (!Armado) flags |= TextFormatFlags.EndEllipsis;
            Tema.Texto_(g, texto, Font, ct, rt, flags);

            if (anchoSub > 0)
            {
                int sx = Width - anchoSub - S(12);
                using (var p = new Pen(Tema.Alpha(Tema.Suave, vivo ? 62 : 38), 1f)) g.DrawLine(p, sx - S(8), S(7), sx - S(8), Height - S(7));
                Tema.Texto_(g, Sub, fSub, acento, new Rectangle(sx, 0, anchoSub + 2, Height));
            }
        }
    }

    /// <summary>
    /// El botón grande de acción: placa, glifo dibujado a mano y rótulo en versalita. Cambia de cara según el
    /// estado (enviar / cortar) sin cambiar de control, para que no salte la maquetación.
    /// </summary>
    internal sealed class Boton : Control
    {
        public enum Glifo { Ninguno, Enviar, Cortar, Mas, Cruz, Copiar, Rehacer, Lapiz, Basura, Flecha, Buscar, Adjuntar, Play, Stop, Llave, Afuera }
        public Glifo Icono = Glifo.Ninguno;
        public Color Acento = Tema.Malva;
        public bool Primario;
        public string Atajo = "";
        bool hover, presionado;
        public event EventHandler Accion;

        public Boton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Tema.Fondo;
            Font = Tema.Media(9f);
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; presionado = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { presionado = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool adentro = presionado && ClientRectangle.Contains(e.Location);
            presionado = false; Invalidate();
            base.OnMouseUp(e);
            if (adentro && e.Button == MouseButtons.Left && Enabled) Accion?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// El ancho que el botón necesita para que NO se corte su texto: relleno + glifo + rótulo con su tracking
        /// + el atajo. Un botón con el rótulo cortado («COPIAR EL COMA») es peor que uno más ancho.
        /// </summary>
        public int AnchoDeseado
        {
            get
            {
                float e = Dpi.Escala(this);
                int S(int px) => Dpi.S(e, px);
                var g = Maquetador.Medidor;
                int w = S(Primario ? 12 : 9) * 2;
                if (Icono != Glifo.Ninguno) w += S(11) + (Text.Length > 0 ? S(8) : 0);
                if (Text.Length > 0) w += Tema.MedirTracking(g, Text, Font, S(1));
                if (Atajo.Length > 0) w += Tema.Medir(g, Atajo, Tema.Mono(7.5f)).Width + S(14);
                return w;
            }
        }

        /// <summary>Acomoda una fila de botones de izquierda a derecha, cada uno con SU ancho. Devuelve el borde derecho.</summary>
        public static int Fila(int x, int y, int alto, int separacion, params Boton[] botones)
        {
            foreach (var b in botones)
            {
                if (b == null || !b.Visible) continue;
                b.SetBounds(x, y, b.AnchoDeseado, alto);
                x += b.Width + separacion;
            }
            return x - separacion;
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            g.Clear(BackColor);
            bool vivo = (hover || presionado) && Enabled;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fondo = !Enabled ? Tema.Mezcla(BackColor, Tema.Texto, 0.02f)
                        : presionado ? Tema.Mezcla(BackColor, Acento, 0.30f)
                        : Primario ? Tema.Mezcla(BackColor, Acento, hover ? 0.22f : 0.15f)
                        : hover ? Tema.Mezcla(BackColor, Tema.Texto, 0.08f)
                        : Tema.Mezcla(BackColor, Tema.Texto, 0.03f);
            Color borde = !Enabled ? Tema.Alpha(Tema.Suave, 22)
                        : Primario ? Tema.Alpha(Acento, vivo ? 190 : 140)
                        : vivo ? Tema.Alpha(Tema.Suave, 90) : Tema.Alpha(Tema.Suave, 32);
            Tema.Placa(g, r, S(2), fondo, borde);
            if (Primario && Enabled)
                using (var placa = Tema.Redondeado(r, S(2)))
                {
                    var recorte = g.Clip;
                    g.SetClip(placa, CombineMode.Intersect);
                    using (var b = new SolidBrush(Tema.Alpha(Acento, 255))) g.FillRectangle(b, 0f, 0f, S(3), Height);
                    g.Clip = recorte;
                }

            Color ct = !Enabled ? Tema.Fantasma : Primario ? Tema.Texto : vivo ? Tema.Texto : Tema.Suave;
            Color ci = !Enabled ? Tema.Fantasma : Primario || vivo ? Acento : Tema.Suave;
            int x = S(Primario ? 12 : 9);
            if (Icono != Glifo.Ninguno)
            {
                DibujarGlifo(g, Icono, new RectangleF(x, (Height - S(11)) / 2f, S(11), S(11)), ci, e);
                x += S(11) + (Text.Length > 0 ? S(8) : 0);
            }
            if (Text.Length > 0)
            {
                int wt = Tema.MedirTracking(g, Text, Font, S(1));
                Tema.Tracking(g, Text, Font, ct, x, 0, Height, S(1));
                x += wt;
            }
            if (Atajo.Length > 0 && Enabled && Width > x + S(40))
            {
                var fa = Tema.Mono(7.5f);
                int wa = Tema.Medir(g, Atajo, fa).Width;
                Tema.Texto_(g, Atajo, fa, Tema.Alpha(Tema.Apagado, vivo ? 220 : 150), new Rectangle(Width - wa - S(10), 0, wa + S(3), Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>Los íconos se dibujan con líneas: nada de fuentes de íconos ni PNGs que se ven borrosos al escalar.</summary>
        public static void DibujarGlifo(Graphics g, Glifo gl, RectangleF r, Color c, float esc)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float gr = Math.Max(1f, Dpi.S(esc, 1) * 1.2f);
            using (var p = new Pen(c, gr) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(c))
            {
                float x = r.X, y = r.Y, w = r.Width, h = r.Height, cx = x + w / 2, cy = y + h / 2;
                switch (gl)
                {
                    case Glifo.Enviar:
                        // un triángulo de transmisión, como el play de una consola de radio
                        using (var path = new GraphicsPath())
                        {
                            path.AddPolygon(new[] { new PointF(x + w * 0.08f, y), new PointF(x + w, cy), new PointF(x + w * 0.08f, y + h) });
                            g.FillPath(b, path);
                        }
                        break;
                    case Glifo.Play:
                        using (var path = new GraphicsPath())
                        {
                            path.AddPolygon(new[] { new PointF(x + w * 0.15f, y), new PointF(x + w * 0.95f, cy), new PointF(x + w * 0.15f, y + h) });
                            g.DrawPath(p, path);
                        }
                        break;
                    case Glifo.Cortar:
                    case Glifo.Stop:
                        g.FillRectangle(b, x + w * 0.12f, y + h * 0.12f, w * 0.76f, h * 0.76f);
                        break;
                    case Glifo.Mas:
                        g.DrawLine(p, cx, y, cx, y + h);
                        g.DrawLine(p, x, cy, x + w, cy);
                        break;
                    case Glifo.Cruz:
                        g.DrawLine(p, x, y, x + w, y + h);
                        g.DrawLine(p, x + w, y, x, y + h);
                        break;
                    case Glifo.Copiar:
                        g.DrawRectangle(p, x, y + h * 0.26f, w * 0.7f, h * 0.74f);
                        g.DrawLine(p, x + w * 0.3f, y, x + w, y);
                        g.DrawLine(p, x + w, y, x + w, y + h * 0.7f);
                        break;
                    case Glifo.Rehacer:
                        g.DrawArc(p, x, y, w, h, 40, 280);
                        g.DrawLine(p, x + w * 0.98f, y + h * 0.08f, x + w * 0.62f, y + h * 0.12f);
                        g.DrawLine(p, x + w * 0.98f, y + h * 0.08f, x + w * 0.92f, y + h * 0.45f);
                        break;
                    case Glifo.Lapiz:
                        g.DrawLine(p, x, y + h, x + w * 0.22f, y + h * 0.78f);
                        g.DrawLine(p, x + w * 0.22f, y + h * 0.78f, x + w, y);
                        g.DrawLine(p, x + w * 0.72f, y - h * 0.02f, x + w * 0.95f, y + h * 0.22f);
                        break;
                    case Glifo.Basura:
                        g.DrawLine(p, x, y + h * 0.2f, x + w, y + h * 0.2f);
                        g.DrawRectangle(p, x + w * 0.16f, y + h * 0.2f, w * 0.68f, h * 0.8f);
                        g.DrawLine(p, x + w * 0.36f, y, x + w * 0.64f, y);
                        break;
                    case Glifo.Flecha:
                        g.DrawLine(p, x, cy, x + w, cy);
                        g.DrawLine(p, x + w * 0.55f, y + h * 0.1f, x + w, cy);
                        g.DrawLine(p, x + w * 0.55f, y + h * 0.9f, x + w, cy);
                        break;
                    case Glifo.Buscar:
                        g.DrawEllipse(p, x, y, w * 0.74f, h * 0.74f);
                        g.DrawLine(p, x + w * 0.66f, y + h * 0.66f, x + w, y + h);
                        break;
                    case Glifo.Adjuntar:
                        g.DrawArc(p, x + w * 0.16f, y, w * 0.68f, h * 0.7f, 180, 180);
                        g.DrawLine(p, x + w * 0.16f, y + h * 0.35f, x + w * 0.16f, y + h * 0.78f);
                        g.DrawLine(p, x + w * 0.84f, y + h * 0.35f, x + w * 0.84f, y + h * 0.6f);
                        g.DrawArc(p, x + w * 0.16f, y + h * 0.5f, w * 0.68f, h * 0.55f, 0, 180);
                        break;
                    case Glifo.Llave:
                        // la sesión: el ojo redondo de la llave y la paleta con dos dientes
                        g.DrawEllipse(p, x, y + h * 0.28f, w * 0.44f, h * 0.44f);
                        g.DrawLine(p, x + w * 0.44f, cy, x + w, cy);
                        g.DrawLine(p, x + w * 0.76f, cy, x + w * 0.76f, y + h * 0.8f);
                        g.DrawLine(p, x + w * 0.97f, cy, x + w * 0.97f, y + h * 0.7f);
                        break;
                    case Glifo.Afuera:
                        // abrir afuera, en el navegador: la caja abierta y la flecha que sale por la esquina
                        g.DrawLine(p, x + w * 0.4f, y + h * 0.1f, x, y + h * 0.1f);
                        g.DrawLine(p, x, y + h * 0.1f, x, y + h);
                        g.DrawLine(p, x, y + h, x + w * 0.9f, y + h);
                        g.DrawLine(p, x + w * 0.9f, y + h, x + w * 0.9f, y + h * 0.6f);
                        g.DrawLine(p, x + w * 0.38f, y + h * 0.62f, x + w, y);
                        g.DrawLine(p, x + w * 0.58f, y, x + w, y);
                        g.DrawLine(p, x + w, y, x + w, y + h * 0.42f);
                        break;
                }
            }
            g.SmoothingMode = old;
        }
    }

    /// <summary>Campo de una línea sobre una placa, con rótulo y pista nativa.</summary>
    internal sealed class Campo : Control
    {
        public readonly TextBox Caja = new TextBox();
        public string Etiqueta = "";
        public Color Acento = Tema.Malva;
        string pista = "";
        bool foco;
        public event EventHandler Cambio;
        public event KeyEventHandler Tecla;

        public Campo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Caja.BorderStyle = BorderStyle.None;
            Caja.BackColor = Tema.Consola;
            Caja.ForeColor = Tema.Texto;
            Caja.Font = Tema.Fina(10f);
            Caja.TextChanged += (s, e) => Cambio?.Invoke(this, EventArgs.Empty);
            Caja.GotFocus += (s, e) => { foco = true; Invalidate(); };
            Caja.LostFocus += (s, e) => { foco = false; Invalidate(); };
            Caja.KeyDown += (s, e) => Tecla?.Invoke(this, e);
            Caja.HandleCreated += (s, e) => { if (pista.Length > 0) Win32.SendMessage(Caja.Handle, Win32.EM_SETCUEBANNER, (IntPtr)1, pista); };
            Controls.Add(Caja);
        }

        public string Pista { get { return pista; } set { pista = value ?? ""; if (Caja.IsHandleCreated) Win32.SendMessage(Caja.Handle, Win32.EM_SETCUEBANNER, (IntPtr)1, pista); } }
        public string Texto { get { return Caja.Text; } set { Caja.Text = value ?? ""; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            float esc = Dpi.Escala(this);
            int top = Etiqueta.Length > 0 ? Dpi.S(esc, 22) : Math.Max(Dpi.S(esc, 6), (Height - Caja.PreferredHeight) / 2);
            Caja.SetBounds(Dpi.S(esc, 11), top, Math.Max(10, Width - Dpi.S(esc, 22)), Math.Max(Dpi.S(esc, 14), Caja.PreferredHeight));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            Tema.Placa(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Dpi.S(esc, 2), Tema.Consola, foco ? Tema.Alpha(Acento, 140) : Tema.Alpha(Tema.Suave, 30));
            if (foco)
                using (var b = new SolidBrush(Tema.Alpha(Acento, 220))) g.FillRectangle(b, 0, 1, Dpi.S(esc, 2), Height - 2);
            if (Etiqueta.Length > 0)
                Tema.Tracking(g, Etiqueta.ToUpperInvariant(), Tema.Media(7f), Tema.Apagado, Dpi.S(esc, 11), Dpi.S(esc, 6), Dpi.S(esc, 12), Dpi.S(esc, 2));
        }
        protected override void OnMouseClick(MouseEventArgs e) { Caja.Focus(); base.OnMouseClick(e); }
    }

    /// <summary>Interruptor con etiqueta y ayuda. El switch va PEGADO al texto, no en el borde derecho.</summary>
    internal sealed class Interruptor : Control
    {
        public bool Activo;
        public string Ayuda = "";
        public Color Acento = Tema.Malva;
        public int AnchoEtiqueta;         // para alinear un grupo entero
        bool hover;
        public event EventHandler Cambio;

        public Interruptor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Cursor = Cursors.Hand;
            Font = Tema.Fina(9.5f);
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Activo = !Activo;
            Invalidate();
            Cambio?.Invoke(this, EventArgs.Empty);
            base.OnMouseClick(e);
        }

        /// <summary>Deja a todo un grupo con el switch en la misma columna: pegados al texto y parejos.</summary>
        public static void Alinear(params Interruptor[] grupo)
        {
            if (grupo == null || grupo.Length == 0) return;
            int max = 0;
            using (var g = grupo[0].CreateGraphics())
                foreach (var i in grupo) max = Math.Max(max, Tema.Medir(g, i.Text, i.Font).Width);
            foreach (var i in grupo) { i.AnchoEtiqueta = max; i.Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            bool conAyuda = Ayuda.Length > 0;
            int yEt = conAyuda ? S(1) : 0;
            int hEt = conAyuda ? Height / 2 : Height;

            var ct = Activo ? Tema.Texto : hover ? Tema.Suave : Tema.Alpha(Tema.Suave, 200);
            int wt = Tema.Medir(g, Text, Font).Width;
            Tema.Texto_(g, Text, Font, ct, new Rectangle(0, yEt, wt + S(4), hEt));
            if (conAyuda)
                Tema.Texto_(g, Ayuda, Tema.Fina(8.5f), Tema.Apagado, new Rectangle(0, Height / 2, Width - S(10), Height / 2), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int x = Math.Max(wt, AnchoEtiqueta) + S(12);
            int w = S(24), h = S(12);
            int y = yEt + (hEt - h) / 2;
            var rr = new RectangleF(x, y, w, h);
            Color fondo = Activo ? Tema.Mezcla(BackColor, Acento, 0.30f) : Tema.Mezcla(BackColor, Tema.Texto, hover ? 0.08f : 0.04f);
            Color borde = Activo ? Tema.Alpha(Acento, 170) : Tema.Alpha(Tema.Suave, hover ? 80 : 45);
            Tema.Placa(g, rr, S(1), fondo, borde);
            int d = h - S(4);
            float px = Activo ? x + w - d - S(2) : x + S(2);
            using (var b = new SolidBrush(Activo ? Acento : Tema.Alpha(Tema.Suave, hover ? 190 : 140)))
                g.FillRectangle(b, px, y + S(2), d, d);
        }
    }

    /// <summary>Deslizador con lectura numérica en mono y marcas: un potenciómetro, no una barra de progreso.</summary>
    internal sealed class Deslizador : Control
    {
        public double Minimo = 0, Maximo = 1, Valor = 0.5, Paso = 0.01;
        public string Formato = "0.00";
        public string Etiqueta = "";
        public string Nota = "";
        public Color Acento = Tema.Ambar;
        public int Marcas = 8;
        bool arrastrando, hover;
        public event EventHandler Cambio;

        /// <summary>Lo que ocupa la lectura numérica, medida con el valor más largo del rango.</summary>
        int AnchoLectura
        {
            get
            {
                var f = Tema.MonoMedia(9f);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                int w = Tema.Medir(Maquetador.Medidor, Maximo.ToString(Formato, ci), f).Width;
                w = Math.Max(w, Tema.Medir(Maquetador.Medidor, Minimo.ToString(Formato, ci), f).Width);
                return w + Dpi.S(Dpi.Escala(this), 6);
            }
        }

        public Deslizador()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        Rectangle Pista
        {
            get
            {
                float e = Dpi.Escala(this);
                int y = Etiqueta.Length > 0 ? Dpi.S(e, 22) : Height / 2 - Dpi.S(e, 3);
                // 🚨 el hueco de la derecha tiene que alcanzar para el número MÁS ANCHO posible: con 46 px
                //    «512» salía «51» y «20» salía «2», que es peor que no mostrar nada.
                return new Rectangle(0, y, Math.Max(10, Width - AnchoLectura - Dpi.S(e, 10)), Dpi.S(e, 6));
            }
        }

        void Poner(int mouseX)
        {
            var p = Pista;
            double t = Math.Max(0, Math.Min(1, (mouseX - p.X) / (double)Math.Max(1, p.Width)));
            double v = Minimo + t * (Maximo - Minimo);
            if (Paso > 0) v = Math.Round(v / Paso) * Paso;
            v = Math.Max(Minimo, Math.Min(Maximo, v));
            if (Math.Abs(v - Valor) < 1e-9) return;
            Valor = v;
            Invalidate();
            Cambio?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { arrastrando = true; Poner(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (arrastrando) Poner(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { arrastrando = false; base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            var p = Pista;
            if (Etiqueta.Length > 0)
                Tema.Tracking(g, Etiqueta.ToUpperInvariant(), Tema.Media(7f), Tema.Apagado, 0, S(3), S(12), S(2));

            float t = (float)((Valor - Minimo) / Math.Max(1e-9, Maximo - Minimo));
            // marcas
            using (var pm = new Pen(Tema.Alpha(Tema.Fantasma, 200), 1f))
                for (int i = 0; i <= Marcas; i++)
                {
                    float x = p.X + p.Width * i / (float)Marcas;
                    g.DrawLine(pm, x, p.Bottom + S(2), x, p.Bottom + S(5));
                }
            using (var b = new SolidBrush(Tema.Alpha(Tema.Fantasma, 150))) g.FillRectangle(b, p);
            using (var b = new SolidBrush(Tema.Alpha(Acento, 200))) g.FillRectangle(b, p.X, p.Y, p.Width * t, p.Height);
            float cx = p.X + p.Width * t;
            using (var b = new SolidBrush(hover || arrastrando ? Tema.Texto : Tema.Mezcla(Tema.Texto, Acento, 0.5f)))
                g.FillRectangle(b, cx - S(2), p.Y - S(3), S(4), p.Height + S(6));

            var fv = Tema.MonoMedia(9f);
            string sv = Valor.ToString(Formato, System.Globalization.CultureInfo.InvariantCulture);
            Tema.Texto_(g, sv, fv, hover || arrastrando ? Tema.Texto : Acento,
                new Rectangle(p.Right + S(8), p.Y - S(6), Math.Max(S(10), Width - p.Right - S(8)), p.Height + S(12)),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            if (Nota.Length > 0)
                Tema.Texto_(g, Nota, Tema.Fina(8f), Tema.Apagado, new Rectangle(0, p.Bottom + S(6), Width, S(14)));
        }
    }

    /// <summary>Tira de pestañas con índice numérico: el índice es lo que la vuelve una consola.</summary>
    internal sealed class Pestanas : Control
    {
        public string[] Nombres = new string[0];
        public string[] Insignias = new string[0];
        public int Activa;
        public Color Acento = Tema.Malva;
        int hover = -1;
        readonly List<Rectangle> rects = new List<Rectangle>();
        public event EventHandler Cambio;

        public Pestanas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = rects.FindIndex(r => r.Contains(e.Location)); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = rects.FindIndex(r => r.Contains(e.Location));
            if (i >= 0 && i != Activa) { Activa = i; Invalidate(); Cambio?.Invoke(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }

        public void Ir(int i)
        {
            if (i < 0 || i >= Nombres.Length || i == Activa) return;
            Activa = i; Invalidate(); Cambio?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = g.DpiX / 96f;
            int S(int px) => Dpi.S(esc, px);
            rects.Clear();
            int x = 0;
            var f = Tema.Media(8.5f);
            var fi = Tema.Mono(7f);
            var fn = Tema.Mono(7f);
            for (int i = 0; i < Nombres.Length; i++)
            {
                string ins = i < Insignias.Length ? Insignias[i] ?? "" : "";
                int wTexto = Tema.MedirTracking(g, Nombres[i].ToUpperInvariant(), f, S(2));
                int wNum = Tema.Medir(g, (i + 1).ToString("00"), fn).Width;
                int w = wTexto + wNum + S(26) + (ins.Length > 0 ? Tema.Medir(g, ins, fi).Width + S(14) : 0);
                var r = new Rectangle(x, 0, w, Height);
                rects.Add(r);
                bool act = i == Activa, hov = i == hover;
                var c = act ? Tema.Texto : hov ? Tema.Suave : Tema.Apagado;
                Tema.Texto_(g, (i + 1).ToString("00"), fn, act ? Tema.Alpha(Acento, 220) : Tema.Fantasma, new Rectangle(r.X + S(9), 0, wNum + S(3), Height - S(3)));
                Tema.Tracking(g, Nombres[i].ToUpperInvariant(), f, c, r.X + S(9) + wNum + S(7), 0, Height - S(3), S(2));
                if (ins.Length > 0)
                {
                    int wIns = Tema.Medir(g, ins, fi).Width;
                    var ri = new RectangleF(r.X + S(9) + wNum + S(7) + wTexto + S(6), (Height - S(13) - S(3)) / 2f, wIns + S(9), S(13));
                    Tema.Placa(g, ri, S(1), Tema.Mezcla(BackColor, Acento, 0.16f), Tema.Alpha(Acento, 90));
                    Tema.Texto_(g, ins, fi, act ? Acento : Tema.Suave, Rectangle.Round(ri), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (act)
                {
                    using (var p = new Pen(Acento, S(2))) g.DrawLine(p, r.X + S(9), Height - S(2), r.Right - S(12), Height - S(2));
                }
                else if (hov)
                {
                    using (var p = new Pen(Tema.Alpha(Tema.Suave, 70), S(1))) g.DrawLine(p, r.X + S(9), Height - S(2), r.Right - S(12), Height - S(2));
                }
                x += w;
            }
        }
    }
}
