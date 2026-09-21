using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// TextBox multilínea con pista propia.
    ///
    /// 🚨 `EM_SETCUEBANNER` NO funciona en un edit multilínea (está documentado y falla en silencio, que es
    /// peor). La pista se pinta a mano después del WM_PAINT del control, sobre su propio HDC.
    /// </summary>
    internal sealed class CajaTexto : TextBox
    {
        public string Pista = "";
        public event EventHandler AltoCambio;
        int lineasAntes = -1;
        bool fantasma;          // la pista está puesta COMO texto, en gris, porque el control está vacío y sin foco
        bool tocandoTexto;

        public CajaTexto()
        {
            BorderStyle = BorderStyle.None;
            Multiline = true;
            WordWrap = true;
            AcceptsTab = false;
            // 🚨 una barra vertical FIJA se dibuja siempre, aunque el texto entre de sobra, y en un compositor
            //    de tres renglones se come el borde derecho. Se prende sola cuando el texto pasa del tope.
            ScrollBars = ScrollBars.None;
            BackColor = Tema.Consola;
            ForeColor = Tema.Texto;
        }

        /// <summary>
        /// 🚨 El texto REAL, que no es el mismo que `base.Text` cuando está puesta la pista. Todo el que lea
        /// esta caja tiene que usar esta propiedad: si lee `base.Text` se manda la pista como si fuera un mensaje.
        /// </summary>
        public new string Text
        {
            get { return fantasma ? "" : base.Text; }
            set
            {
                QuitarPista();
                base.Text = value ?? "";
                if (base.TextLength == 0) PonerPista();
            }
        }
        public new int TextLength { get { return fantasma ? 0 : base.TextLength; } }
        public bool Vacia { get { return fantasma || base.TextLength == 0; } }
        public new void Clear() { Text = ""; }

        /// <summary>
        /// La pista se escribe COMO TEXTO en gris, no se pinta encima.
        ///
        /// 🚨 `EM_SETCUEBANNER` no funciona en un edit multilínea (está documentado y falla en silencio), y
        /// dibujarla sobre el WM_PAINT del control tampoco se ve: el EDIT nativo vuelve a pintar su fondo
        /// después, así que el texto aparece y desaparece en el mismo frame. Ponerla como contenido es la única
        /// forma que se ve siempre, y el costo es acordarse de no leer `base.Text` en ningún lado.
        /// </summary>
        void PonerPista()
        {
            // se muestra aunque la caja tenga el foco: con el cursor puesto y sin texto, la pista es justo lo
            // que hace falta leer. Se va con la primera tecla, como en cualquier navegador.
            if (fantasma || Pista.Length == 0 || base.TextLength > 0) return;
            fantasma = true;
            tocandoTexto = true;
            ForeColor = Tema.Alpha(Tema.Apagado, 255);
            base.Text = Pista;
            tocandoTexto = false;
        }

        void QuitarPista()
        {
            if (!fantasma) return;
            fantasma = false;
            tocandoTexto = true;
            base.Text = "";
            ForeColor = Tema.Texto;
            tocandoTexto = false;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); PonerPista(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); PonerPista(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (fantasma) Select(0, 0); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (fantasma) Select(0, 0); }
        protected override void OnKeyPress(KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar)) QuitarPista(); base.OnKeyPress(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            // pegar también borra la pista: si no, el texto pegado quedaría a continuación de ella
            if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert)) QuitarPista();
            base.OnKeyDown(e);
        }

        /// <summary>Cuántos renglones VISUALES ocupa el texto (contando los que se envolvieron solos).</summary>
        public int LineasVisuales
        {
            get
            {
                if (Vacia) return 1;
                try { return GetLineFromCharIndex(base.TextLength) + 1; } catch { return 1; }
            }
        }

        /// <summary>A partir de cuántos renglones aparece la barra (el compositor deja de crecer ahí).</summary>
        public int TopeLineas = 8;

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (tocandoTexto) return;          // poner o sacar la pista no es "el usuario escribió"
            int l = LineasVisuales;
            if (l != lineasAntes)
            {
                lineasAntes = l;
                var quiero = l > TopeLineas ? ScrollBars.Vertical : ScrollBars.None;
                if (ScrollBars != quiero)
                {
                    ScrollBars = quiero;
                    // 🚨 cambiar ScrollBars RECREA el handle y se pierde el tema oscuro: la barra salía blanca
                    if (IsHandleCreated) Win32.BarrasOscuras(Handle);
                }
                AltoCambio?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// El compositor: donde se escribe. Crece solo hasta ocho renglones, muestra a quién le estás hablando y
    /// cuánto pesa lo que vas a mandar, y cambia el botón de TRANSMITIR a CORTAR mientras el modelo escribe
    /// (mismo control, misma posición: la maquetación no salta).
    /// </summary>
    internal sealed class Compositor : Control
    {
        readonly Nucleo N;
        public readonly CajaTexto Caja = new CajaTexto();
        readonly Boton btEnviar = new Boton();
        readonly Chip chPersona = new Chip { Tipo = Chip.Modo.Valor };
        readonly Boton btAdjuntar = new Boton { Icono = Boton.Glifo.Adjuntar };
        float esc = 1f;
        bool foco;

        public event Action<string> Enviar;
        public event Action Cortar;
        public event Action CambiarPersona;
        public event Action Adjuntar;
        public event Action SubirAlUltimo;

        public Compositor(Nucleo n)
        {
            N = n;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;

            Caja.Font = Tema.Fina(10.5f);
            Caja.Pista = "escribí y mandá con Enter · Shift+Enter para otra línea";
            Caja.GotFocus += (s, e) => { foco = true; Invalidate(); };
            Caja.LostFocus += (s, e) => { foco = false; Invalidate(); };
            Caja.AltoCambio += (s, e) => { PedirAcomodar(); };
            Caja.KeyDown += Tecla;
            Caja.TextChanged += (s, e) => Invalidate();
            Caja.HandleCreated += (s, e) => Win32.BarrasOscuras(Caja.Handle);
            Controls.Add(Caja);

            btEnviar.Text = "TRANSMITIR";
            btEnviar.Icono = Boton.Glifo.Enviar;
            btEnviar.Primario = true;
            btEnviar.Atajo = "⏎";
            btEnviar.Accion += (s, e) => Disparar();
            Controls.Add(btEnviar);

            chPersona.Acento = Tema.Malva;
            chPersona.Accion += (s, e) => CambiarPersona?.Invoke();
            Controls.Add(chPersona);

            btAdjuntar.Accion += (s, e) => Adjuntar?.Invoke();
            Controls.Add(btAdjuntar);
        }

        public event Action AltoCambio;
        void PedirAcomodar() { AltoCambio?.Invoke(); Acomodar(); Invalidate(); }

        int S(int px) => Dpi.S(esc, px);

        /// <summary>Alto que necesita ahora mismo: crece con el texto y frena a los ocho renglones.</summary>
        public int AltoDeseado
        {
            get
            {
                int lineas = Math.Max(1, Math.Min(8, Caja.LineasVisuales));
                int hLinea = Math.Max(S(14), Caja.Font.Height);
                return S(20) + lineas * hLinea + S(38);
            }
        }

        void Tecla(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                bool manda = e.Control || (N.Cfg.EnterEnvia && !e.Shift);
                if (manda) { e.SuppressKeyPress = true; e.Handled = true; Disparar(); }
                return;
            }
            if (e.KeyCode == Keys.Up && Caja.TextLength == 0) { e.Handled = true; SubirAlUltimo?.Invoke(); return; }
            if (e.KeyCode == Keys.Escape)
            {
                if (N.Tx != null && N.Tx.Ocupado) { Cortar?.Invoke(); e.Handled = true; return; }
                if (Caja.TextLength > 0) { Caja.Clear(); e.Handled = true; }
            }
        }

        void Disparar()
        {
            if (N.Tx != null && N.Tx.Ocupado) { Cortar?.Invoke(); return; }
            string t = Caja.Text.TrimEnd();
            if (t.Length == 0) return;
            Caja.Clear();
            Enviar?.Invoke(t);
        }

        public void Poner(string texto)
        {
            Caja.Focus();                       // primero el foco: así se saca la pista antes de escribir
            Caja.Text = texto ?? "";
            Caja.SelectionStart = Caja.TextLength;
        }

        /// <summary>Agrega al final. 🚨 Nada de AppendText: con la pista puesta escribiría a continuación de ella.</summary>
        public void Agregar(string texto)
        {
            Caja.Focus();
            string actual = Caja.Text;
            Caja.Text = actual.Length > 0 && !actual.EndsWith("\n") ? actual + Environment.NewLine + texto : actual + texto;
            Caja.SelectionStart = Caja.TextLength;
        }
        public new void Focus() { Caja.Focus(); }

        /// <summary>Refleja el estado del transmisor en el botón, sin cambiar de control ni mover nada.</summary>
        public void Estado()
        {
            bool ocupado = N.Tx != null && N.Tx.Ocupado;
            btEnviar.Text = ocupado ? "CORTAR" : "TRANSMITIR";
            btEnviar.Icono = ocupado ? Boton.Glifo.Cortar : Boton.Glifo.Enviar;
            btEnviar.Acento = ocupado ? Tema.Rosa : N.ColorPersona(N.PersonaActual);
            btEnviar.Atajo = ocupado ? "Esc" : "⏎";
            var p = N.PersonaActual;
            chPersona.Acento = N.ColorPersona(p);
            chPersona.Poner("persona", p != null ? p.Nombre : "—");
            chPersona.Ajustar();
            btEnviar.Invalidate();
            Acomodar();
            Invalidate();
        }

        public void Acomodar()
        {
            esc = Dpi.Escala(this);
            int pad = S(12);
            int hFila = S(26);
            int hCaja = Math.Max(S(18), Height - S(20) - S(38));
            Caja.SetBounds(pad + S(4), S(13), Math.Max(S(40), Width - pad * 2 - S(8)), hCaja);
            chPersona.Ajustar();
            chPersona.Superficie = Tema.Panel;
            chPersona.SetBounds(pad, Height - hFila - S(9), chPersona.Width, hFila);
            btAdjuntar.SetBounds(chPersona.Right + S(6), Height - hFila - S(9), S(30), hFila);
            btAdjuntar.BackColor = Tema.Panel;
            int wEnv = S(118);
            btEnviar.SetBounds(Width - pad - wEnv, Height - hFila - S(9), wEnv, hFila);
            btEnviar.BackColor = Tema.Panel;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Acomodar(); }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var c = N.ColorPersona(N.PersonaActual);
            bool ocupado = N.Tx != null && N.Tx.Ocupado;
            var r = new RectangleF(S(4) + 0.5f, 0.5f, Width - S(8) - 1, Height - S(6) - 1);
            Tema.Placa(g, r, S(3), Tema.Panel, foco ? Tema.Alpha(c, 120) : Tema.Alpha(Tema.Filete, 230));
            // riel del compositor: se enciende cuando tenés el foco
            using (var b = new SolidBrush(foco ? Tema.Alpha(c, 230) : Tema.Alpha(Tema.Filete, 255)))
                g.FillRectangle(b, r.X, r.Y + 1, S(2), r.Height - 2);

            // línea divisoria sobre la barra de abajo
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 200), 1f))
                g.DrawLine(p, r.X + S(8), Height - S(42), r.Right - S(8), Height - S(42));

            // peso de lo que vas a mandar
            string txt = Caja.Text;
            var f = Tema.Mono(7.5f);
            var partes = new List<string>();
            if (txt.Length > 0) partes.Add("~" + Cliente.Estimar(txt) + " tok");
            if (N.Actual != null && N.Actual.Mensajes.Count > 0) partes.Add(N.Actual.Mensajes.Count + " en el hilo");
            if (ocupado && N.Tx != null) partes.Add(N.Tx.Fase);
            string s = string.Join("  ·  ", partes);
            if (s.Length > 0)
            {
                int w = Tema.Medir(g, s, f).Width;
                int xd = Width - S(12) - S(118) - S(14) - w;
                if (xd > S(160))
                    Tema.Texto_(g, s, f, ocupado ? Tema.Alpha(c, 210) : Tema.Apagado,
                        new Rectangle(xd, Height - S(35), w + S(4), S(26)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
