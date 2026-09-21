using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// La transcripción: el hilo de mensajes dibujado a mano, con Markdown maquetado y resaltado de sintaxis.
    ///
    /// Está armado como un transcripto de misión y no como globitos de chat: rótulo del que habla en versalita,
    /// reloj a la derecha, riel de color al costado y el cuerpo alineado en una sola columna. Así se lee de
    /// arriba abajo como un diario de vuelo, que es exactamente lo que es.
    ///
    /// ⭐ El maquetado se cachea por (ancho × versión del texto). Sin eso, cada token que entra durante el
    /// streaming re-mediría el hilo entero y la ventana se arrastraría a los cincuenta mensajes.
    /// </summary>
    internal sealed class Hilo : Control, IRueda
    {
        sealed class Item
        {
            public Mensaje M;
            public Maqueta Maq;
            public int Y, AltoCab, AltoCuerpo, AltoPie;
            public int Alto => AltoCab + AltoCuerpo + AltoPie;
            public int AnchoCache = -1, VersionCache = -1;
            public Rectangle[] Acciones = new Rectangle[0];
        }

        readonly Nucleo N;
        readonly List<Item> items = new List<Item>();
        Conversacion conv;
        int desplaz, altoTotal;
        bool pegadoAbajo = true;
        int hoverItem = -1, hoverAccion = -1, hoverCodigo = -1;
        float esc = 1f;
        readonly Timer parpadeo = new Timer { Interval = 530 };
        bool caretVisible = true;

        public event Action<Mensaje, string> Accion;      // (mensaje, "copiar" | "rehacer" | "editar" | "borrar")
        public event Action<string> Sugerencia;

        static readonly string[] NombreAccion = { "copiar", "rehacer", "editar", "borrar" };

        public Hilo(Nucleo n)
        {
            N = n;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
            parpadeo.Tick += (s, e) => { if (N.Tx != null && N.Tx.Ocupado) { caretVisible = !caretVisible; Invalidate(); } else if (!caretVisible) { caretVisible = true; Invalidate(); } };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); esc = Dpi.Escala(this); parpadeo.Start(); }
        protected override void Dispose(bool disposing) { if (disposing) parpadeo.Dispose(); base.Dispose(disposing); }

        int S(int px) => Dpi.S(esc, px);

        public void Poner(Conversacion c)
        {
            conv = c;
            items.Clear();
            desplaz = 0;
            pegadoAbajo = true;
            Recalcular();
            AlFinal();
            Invalidate();
        }

        public Conversacion Conv => conv;

        /// <summary>Hay texto nuevo: re-maquetar sólo lo que cambió y seguir pegado al final si ya lo estaba.</summary>
        public void Novedad()
        {
            Recalcular();
            if (pegadoAbajo) AlFinal();
            Invalidate();
        }

        public void AlFinal()
        {
            desplaz = Math.Max(0, altoTotal - AltoVisible);
            pegadoAbajo = true;
        }

        int AltoVisible => Math.Max(10, Height - S(10));
        /// <summary>
        /// ⭐ La transcripción usa un PORCENTAJE del ancho disponible (90 % por defecto), no una columna de
        /// lectura con tope fijo: el user quiere el chat bien ancho, sin margen desperdiciado a los costados.
        /// Se ajusta desde AJUSTES con «ancho del chat» y vive en config como `anchoChat`.
        /// </summary>
        int AnchoTexto
        {
            get
            {
                int pct = Math.Max(40, Math.Min(100, N.Cfg.AnchoChat));
                int util = Math.Max(S(160), Width - S(24));      // el canalón de la barrita de posición
                return Math.Max(S(220), (int)(util * pct / 100.0));
            }
        }
        int MargenX => Math.Max(S(8), (Width - AnchoTexto) / 2);
        /// <summary>Dónde arranca y qué ancho tiene la banda de texto: el compositor se alinea con esto.</summary>
        public Rectangle Banda => new Rectangle(MargenX, 0, AnchoTexto, Height);

        // ------------------------------------------------------------------ maquetado

        void Recalcular()
        {
            if (conv == null) { items.Clear(); altoTotal = 0; return; }
            int ancho = AnchoTexto;
            // sincronizar la lista de items con la de mensajes (sin perder las cachés)
            var porId = items.ToDictionary(i => i.M.Id, i => i);
            items.Clear();
            foreach (var m in conv.Mensajes)
            {
                Item it;
                if (!porId.TryGetValue(m.Id, out it)) it = new Item { M = m };
                items.Add(it);
            }

            var op = new Maquetador.Opciones { Esc = esc, Acento = N.ColorPersona(N.PersonaActual) };
            int y = S(14);
            foreach (var it in items)
            {
                bool usuario = it.M.Rol == Rol.Usuario;
                int anchoCuerpo = ancho - (usuario ? S(26) : S(20));
                if (it.AnchoCache != anchoCuerpo || it.VersionCache != it.M.Version || it.Maq == null)
                {
                    var bloques = Markdown.Parsear(it.M.Texto.Length > 0 ? it.M.Texto : (N.Tx != null && N.Tx.Escribiendo == it.M ? "" : "…"));
                    op.Acento = usuario ? Tema.Teal : ColorDe(it.M);
                    op.Compacto = usuario;
                    it.Maq = Maquetador.Maquetar(bloques, anchoCuerpo, op);
                    it.AnchoCache = anchoCuerpo;
                    it.VersionCache = it.M.Version;
                }
                it.AltoCab = S(20);
                it.AltoCuerpo = Math.Max(S(16), it.Maq.Alto);
                it.AltoPie = usuario ? S(12) : S(20);
                it.Y = y;
                y += it.Alto + S(16);
            }
            altoTotal = y + S(10);
            if (desplaz > Math.Max(0, altoTotal - AltoVisible)) desplaz = Math.Max(0, altoTotal - AltoVisible);
        }

        Color ColorDe(Mensaje m)
        {
            if (m.Error) return Tema.Rosa;
            if (m.Motor) return Tema.Ambar;
            var p = N.PersonaDe(m.Persona.Length > 0 ? m.Persona : (conv != null ? conv.Persona : ""));
            return N.ColorPersona(p);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            esc = Dpi.Escala(this);
            foreach (var i in items) i.AnchoCache = -1;
            Recalcular();
            if (pegadoAbajo) AlFinal();
        }

        // ------------------------------------------------------------------ rueda y mouse

        public void Rueda(int delta)
        {
            int max = Math.Max(0, altoTotal - AltoVisible);
            desplaz = Math.Max(0, Math.Min(max, desplaz - Math.Sign(delta) * S(64)));
            pegadoAbajo = desplaz >= max - S(4);
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        public void Desplazar(int paginas)
        {
            int max = Math.Max(0, altoTotal - AltoVisible);
            desplaz = Math.Max(0, Math.Min(max, desplaz + paginas * AltoVisible));
            pegadoAbajo = desplaz >= max - S(4);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int it = -1, ac = -1, cd = -1;
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rectangle(MargenX, items[i].Y - desplaz, AnchoTexto, items[i].Alto);
                if (!r.Contains(e.Location)) continue;
                it = i;
                for (int k = 0; k < items[i].Acciones.Length; k++)
                    if (items[i].Acciones[k].Contains(e.Location)) ac = k;
                if (ac < 0 && items[i].Maq != null)
                {
                    int x0 = MargenX + (items[i].M.Rol == Rol.Usuario ? S(26) : S(20));
                    int y0 = items[i].Y - desplaz + items[i].AltoCab;
                    for (int k = 0; k < items[i].Maq.Codigos.Count; k++)
                    {
                        var z = items[i].Maq.Codigos[k].R;
                        if (new Rectangle(x0 + z.X, y0 + z.Y, z.Width, z.Height).Contains(e.Location)) cd = k;
                    }
                }
                break;
            }
            if (it != hoverItem || ac != hoverAccion || cd != hoverCodigo)
            {
                hoverItem = it; hoverAccion = ac; hoverCodigo = cd;
                Cursor = ac >= 0 || cd >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverItem >= 0 || hoverAccion >= 0 || hoverCodigo >= 0) { hoverItem = hoverAccion = hoverCodigo = -1; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            // sugerencias del estado vacío
            if (items.Count == 0)
            {
                for (int i = 0; i < zonasSugerencia.Count; i++)
                    if (zonasSugerencia[i].Key.Contains(e.Location)) { Sugerencia?.Invoke(zonasSugerencia[i].Value); return; }
                return;
            }
            if (hoverItem < 0) return;
            var it = items[hoverItem];
            if (hoverAccion >= 0 && hoverAccion < NombreAccion.Length) { Accion?.Invoke(it.M, NombreAccion[hoverAccion]); return; }
            if (hoverCodigo >= 0 && it.Maq != null && hoverCodigo < it.Maq.Codigos.Count)
            {
                try { Clipboard.SetText(it.Maq.Codigos[hoverCodigo].Texto); N.Log.Ok("Bloque de código copiado"); Sonidos.Tic(); } catch { }
                return;
            }
            // un enlace
            if (it.Maq != null)
            {
                int x0 = MargenX + (it.M.Rol == Rol.Usuario ? S(26) : S(20));
                int y0 = it.Y - desplaz + it.AltoCab;
                foreach (var z in it.Maq.Enlaces)
                    if (new Rectangle(x0 + z.R.X, y0 + z.R.Y, z.R.Width, z.R.Height).Contains(e.Location))
                    {
                        try { System.Diagnostics.Process.Start(z.Url); } catch { }
                        return;
                    }
            }
        }

        // ------------------------------------------------------------------ pintura

        readonly List<KeyValuePair<Rectangle, string>> zonasSugerencia = new List<KeyValuePair<Rectangle, string>>();

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(BackColor);
            if (N.Cfg.Reticula) Tema.Reticula(g, ClientRectangle, S(22), Tema.Alpha(Tema.Reticulado, 200));

            if (conv == null || items.Count == 0) { Vacio(g); return; }
            zonasSugerencia.Clear();

            int x0 = MargenX;
            int ancho = AnchoTexto;
            var fRol = Tema.Media(8f);
            var fMeta = Tema.Mono(7.5f);

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int y = it.Y - desplaz;
                if (y + it.Alto < -S(40) || y > Height + S(40)) { it.Acciones = new Rectangle[0]; continue; }
                bool usuario = it.M.Rol == Rol.Usuario;
                var c = usuario ? Tema.Teal : ColorDe(it.M);
                int sangria = usuario ? S(26) : S(20);
                bool activo = hoverItem == i;

                // --- placa del mensaje del operador: un tinte, nunca un globo
                if (usuario)
                {
                    var rp = new RectangleF(x0, y - S(5), ancho, it.Alto + S(8));
                    using (var b = new SolidBrush(Tema.Alpha(Tema.Consola, 210))) g.FillRectangle(b, rp);
                    using (var p = new Pen(Tema.Alpha(Tema.Filete, 190), 1f)) g.DrawRectangle(p, rp.X, rp.Y, rp.Width, rp.Height);
                }

                // --- riel del que habla
                using (var b = new SolidBrush(Tema.Alpha(c, usuario ? 200 : 235)))
                    g.FillRectangle(b, x0 + (usuario ? S(6) : 0), y - (usuario ? S(2) : 0), S(2), it.Alto + (usuario ? S(2) : 0));

                // --- encabezado: QUIÉN · de dónde · reloj
                string rol = usuario ? "OPERADOR" : it.M.Motor ? "A BORDO" : NombrePersona(it.M);
                int wr = Tema.Tracking(g, rol, fRol, activo ? Tema.Texto : Tema.Alpha(c, 235), x0 + sangria, y, S(14), S(2));
                int xm = x0 + sangria + wr + S(10);
                string meta = Meta(it.M);
                if (meta.Length > 0)
                    Tema.Texto_(g, meta, fMeta, Tema.Apagado, new Rectangle(xm, y, Math.Max(0, ancho - sangria - wr - S(70)), S(14)));
                string hora = it.M.Hora.ToString("HH:mm:ss");
                int wh = Tema.Medir(g, hora, fMeta).Width;
                Tema.Texto_(g, hora, fMeta, Tema.Alpha(Tema.Apagado, activo ? 220 : 150), new Rectangle(x0 + ancho - wh - S(2), y, wh + S(3), S(14)), TextFormatFlags.Right | TextFormatFlags.Top);

                // --- cuerpo
                int yc = y + it.AltoCab;
                if (it.Maq != null)
                {
                    g.SetClip(new Rectangle(0, 0, Width, Height));
                    it.Maq.Dibujar(g, x0 + sangria, yc, desplaz - it.Y - it.AltoCab - S(40), desplaz - it.Y - it.AltoCab + Height + S(40));
                    g.ResetClip();
                    // resaltar el bloque de código bajo el mouse y ofrecer copiarlo
                    if (activo && hoverCodigo >= 0 && hoverCodigo < it.Maq.Codigos.Count)
                    {
                        var z = it.Maq.Codigos[hoverCodigo].R;
                        var rz = new Rectangle(x0 + sangria + z.X, yc + z.Y, z.Width, z.Height);
                        using (var p = new Pen(Tema.Alpha(Tema.Crema, 110), 1f)) g.DrawRectangle(p, rz);
                        var f = Tema.Media(7.5f);
                        string s = "CLIC PARA COPIAR";
                        int w = Tema.MedirTracking(g, s, f, S(1)) + S(14);
                        var rb = new RectangleF(rz.Right - w - S(6), rz.Y + S(4), w, S(15));
                        Tema.Placa(g, rb, S(2), Tema.Mezcla(Tema.Consola, Tema.Crema, 0.18f), Tema.Alpha(Tema.Crema, 120));
                        Tema.Tracking(g, s, f, Tema.Crema, (int)rb.X + S(7), (int)rb.Y, S(15), S(1));
                    }
                }

                // --- cursor de transmisión
                if (N.Tx != null && N.Tx.Escribiendo == it.M && caretVisible)
                {
                    var ult = it.Maq != null ? it.Maq.Piezas.OfType<PTexto>().LastOrDefault() : null;
                    int cx = ult != null ? x0 + sangria + ult.R.Right - S(2) : x0 + sangria;
                    int cy = ult != null ? yc + ult.R.Y + S(2) : yc + S(2);
                    using (var b = new SolidBrush(Tema.Alpha(c, 230))) g.FillRectangle(b, cx, cy, S(6), S(12));
                }

                // --- pie con acciones (sólo cuando el mouse está encima: la interfaz no se llena de botones)
                if (!usuario && activo && !(N.Tx != null && N.Tx.Escribiendo == it.M))
                {
                    it.Acciones = Acciones(g, x0 + sangria, y + it.AltoCab + it.AltoCuerpo + S(3), c);
                }
                else if (usuario && activo)
                {
                    it.Acciones = AccionesUsuario(g, x0 + sangria, y + it.AltoCab + it.AltoCuerpo + S(1), c);
                }
                else it.Acciones = new Rectangle[0];
            }

            // --- sombras de borde: dicen "sigue" sin una barra de desplazamiento
            if (desplaz > S(6)) Tema.Desvanecido(g, new Rectangle(0, 0, Width, S(22)), Tema.Fondo, false);
            int max = Math.Max(0, altoTotal - AltoVisible);
            if (desplaz < max - S(6))
            {
                Tema.Desvanecido(g, new Rectangle(0, Height - S(26), Width, S(26)), Tema.Fondo, true);
                var f = Tema.Media(7.5f);
                string s = "▾ HAY MÁS ABAJO";
                int w = Tema.MedirTracking(g, s, f, S(1)) + S(16);
                var r = new RectangleF(Width - w - S(20), Height - S(22), w, S(16));
                Tema.Placa(g, r, S(2), Tema.Mezcla(Tema.Fondo, Tema.Malva, 0.16f), Tema.Alpha(Tema.Malva, 130));
                Tema.Tracking(g, s, f, Tema.Malva, (int)r.X + S(8), (int)r.Y, S(16), S(1));
            }
            // barrita de posición: 2 px, del lado derecho, sin flechas ni caja
            if (max > 0)
            {
                int hb = Math.Max(S(24), (int)((double)AltoVisible / altoTotal * Height));
                int yb = (int)((double)desplaz / max * (Height - hb));
                using (var b = new SolidBrush(Tema.Alpha(Tema.Suave, 60))) g.FillRectangle(b, Width - S(3), yb, S(2), hb);
            }
        }

        string NombrePersona(Mensaje m)
        {
            var p = N.PersonaDe(m.Persona.Length > 0 ? m.Persona : (conv != null ? conv.Persona : ""));
            return p != null ? p.Nombre.ToUpperInvariant() : "CAPCOM";
        }

        string Meta(Mensaje m)
        {
            if (m.Rol == Rol.Usuario) return "";
            var partes = new List<string>();
            if (N.Tx != null && N.Tx.Escribiendo == m)
            {
                var t = DateTime.Now - N.Tx.Comenzo;
                partes.Add("· " + N.Tx.Fase);
                partes.Add(t.TotalSeconds.ToString("0.0") + " s");
                if (m.Texto.Length > 0) partes.Add("~" + Cliente.Estimar(m.Texto) + " tok");
                return string.Join("  ", partes);
            }
            if (m.Motor) return "· motor de a bordo";
            if (m.Modelo.Length > 0) partes.Add("· " + Corto(m.Modelo, 28));
            if (m.Ms > 0) partes.Add((m.Ms / 1000.0).ToString("0.0") + " s");
            if (m.MsPrimerToken > 0) partes.Add("1er " + (m.MsPrimerToken / 1000.0).ToString("0.0") + " s");
            if (m.Tokens > 0) partes.Add(m.Tokens + " tok");
            if (m.TokPorSeg > 0) partes.Add(m.TokPorSeg.ToString("0.0") + " tok/s");
            if (m.Cancelado) partes.Add("· CORTADO");
            return string.Join("  ", partes);
        }

        static string Corto(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        Rectangle[] Acciones(Graphics g, int x, int y, Color c)
        {
            string[] etq = { "COPIAR", "REHACER", "EDITAR", "BORRAR" };
            var f = Tema.Media(7f);
            var res = new List<Rectangle>();
            foreach (var e in etq)
            {
                int w = Tema.MedirTracking(g, e, f, S(1)) + S(14);
                var r = new Rectangle(x, y, w, S(15));
                bool hov = hoverAccion == res.Count && hoverItem >= 0 && items[hoverItem].Acciones.Length > res.Count;
                Tema.Placa(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(2),
                    hov ? Tema.Mezcla(Tema.Fondo, c, 0.18f) : Tema.Alpha(Tema.Consola, 170),
                    hov ? Tema.Alpha(c, 150) : Tema.Alpha(Tema.Filete, 220));
                Tema.Tracking(g, e, f, hov ? c : Tema.Apagado, r.X + S(7), r.Y, r.Height, S(1));
                res.Add(r);
                x += w + S(5);
            }
            return res.ToArray();
        }

        Rectangle[] AccionesUsuario(Graphics g, int x, int y, Color c)
        {
            // el operador sólo puede copiar y editar lo suyo; rehacer y borrar no aplican de la misma forma
            string[] etq = { "COPIAR", "", "EDITAR", "BORRAR" };
            var f = Tema.Media(7f);
            var res = new List<Rectangle>();
            for (int i = 0; i < etq.Length; i++)
            {
                if (etq[i].Length == 0) { res.Add(Rectangle.Empty); continue; }
                int w = Tema.MedirTracking(g, etq[i], f, S(1)) + S(14);
                var r = new Rectangle(x, y, w, S(15));
                bool hov = hoverAccion == i;
                Tema.Placa(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(2),
                    hov ? Tema.Mezcla(Tema.Fondo, c, 0.18f) : Color.Transparent,
                    hov ? Tema.Alpha(c, 150) : Color.Transparent);
                Tema.Tracking(g, etq[i], f, hov ? c : Tema.Fantasma, r.X + S(7), r.Y, r.Height, S(1));
                res.Add(r);
                x += w + S(5);
            }
            return res.ToArray();
        }

        // ------------------------------------------------------------------ estado vacío

        static readonly string[][] Semillas =
        {
            new[] { "explicame cómo funciona el streaming de tokens por SSE", "SSE" },
            new[] { "escribime una función en C# que normalice una MAC", "MAC" },
            new[] { "(2^16 - 1) / 3", "cuenta" },
            new[] { "ayuda", "qué sabe sin el modelo" },
        };

        void Vacio(Graphics g)
        {
            zonasSugerencia.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int ancho = Math.Min(S(560), Width - S(60));
            int x = (Width - ancho) / 2;
            int y = Math.Max(S(30), Height / 2 - S(160));

            var c = N.ColorPersona(N.PersonaActual);
            int d = S(110);
            Tema.Orbital(g, new RectangleF(Width / 2f - d / 2f, y, d, d), 0.14f, c, 0.55f);
            y += d + S(14);

            var fT = Tema.Fina(17f);
            string titulo = "CAPCOM";
            int wt = Tema.MedirTracking(g, titulo, fT, S(7));
            Tema.Tracking(g, titulo, fT, Tema.Texto, Width / 2 - wt / 2, y, S(26), S(7));
            y += S(30);

            var fs = Tema.Fina(9.5f);
            bool vivo = N.Cli.Estado.Señal == Señal.Nominal;
            string sub = vivo
                ? "enlace nominal con " + N.Cli.Estado.ModeloCorto
                : "sin enlace · contesta el motor de a bordo";
            int ws = Tema.Medir(g, sub, fs).Width;
            Tema.Texto_(g, sub, fs, vivo ? Tema.Teal : Tema.Ambar, new Rectangle(Width / 2 - ws / 2, y, ws + S(4), S(16)));
            y += S(30);

            Tema.Esquinas(g, new RectangleF(x, y, ancho, S(112)), S(11), Tema.Alpha(Tema.Filete, 230));
            y += S(14);
            var fe = Tema.Media(7.5f);
            Tema.Tracking(g, "PARA EMPEZAR", fe, Tema.Apagado, x + S(16), y, S(13), S(2));
            y += S(20);

            var f = Tema.Fina(9.5f);
            foreach (var s in Semillas)
            {
                int w = Tema.Medir(g, s[0], f).Width + S(24);
                w = Math.Min(w, ancho - S(32));
                var r = new Rectangle(x + S(16), y, w, S(21));
                bool hov = r.Contains(PointToClient(MousePosition));
                Tema.Placa(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(2),
                    hov ? Tema.Mezcla(Tema.Fondo, c, 0.16f) : Tema.Alpha(Tema.Consola, 190),
                    hov ? Tema.Alpha(c, 150) : Tema.Alpha(Tema.Filete, 210));
                using (var b = new SolidBrush(Tema.Alpha(c, hov ? 255 : 150))) g.FillRectangle(b, r.X, r.Y, S(2), r.Height);
                Tema.Texto_(g, s[0], f, hov ? Tema.Texto : Tema.Suave, new Rectangle(r.X + S(10), r.Y, r.Width - S(14), r.Height),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                zonasSugerencia.Add(new KeyValuePair<Rectangle, string>(r, s[0]));
                y += S(25);
            }
        }
    }
}
