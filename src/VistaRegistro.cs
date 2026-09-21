using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// REGISTRO: todo el archivo de una vez. Busca en el texto de cada mensaje de cada transmisión y, al costado,
    /// deja las cuentas de la estación — cuánto se habló, con qué persona, con qué modelo y a qué velocidad.
    /// </summary>
    internal sealed class VistaRegistro : Pantalla
    {
        public override string Nombre => "registro";

        sealed class Golpe { public Conversacion C; public Mensaje M; public string Extracto = ""; public int Pos; }

        readonly Campo busqueda;
        readonly Boton btLimpiar, btExportarTodo;
        List<Golpe> golpes = new List<Golpe>();
        int desplaz, hover = -1;
        readonly List<Rectangle> rects = new List<Rectangle>();

        public event Action<Conversacion> Abrir;

        public VistaRegistro(Nucleo n) : base(n)
        {
            busqueda = new Campo { Pista = "buscar en todas las transmisiones…", Acento = Tema.Cielo };
            busqueda.Cambio += (s, e) => { Buscar(); Invalidate(); };
            Controls.Add(busqueda);

            btLimpiar = new Boton { Text = "LIMPIAR", Icono = Boton.Glifo.Cruz, Acento = Tema.Suave };
            btLimpiar.Accion += (s, e) => { busqueda.Texto = ""; Buscar(); Invalidate(); };
            Controls.Add(btLimpiar);

            btExportarTodo = new Boton { Text = "ABRIR LA CARPETA DEL ARCHIVO", Icono = Boton.Glifo.Adjuntar, Acento = Tema.Suave };
            btExportarTodo.Accion += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", N.Arch.Carpeta); } catch { } };
            Controls.Add(btExportarTodo);
        }

        public void Foco() { busqueda.Caja.Focus(); busqueda.Caja.SelectAll(); }

        public override void Refrescar() { Buscar(); Invalidate(); }

        public override void Modo(string que)
        {
            if (!string.IsNullOrWhiteSpace(que)) { busqueda.Texto = que; Buscar(); }
            Invalidate();
        }

        void Buscar()
        {
            string q = busqueda.Texto.Trim();
            golpes = new List<Golpe>();
            desplaz = 0;
            if (q.Length == 0)
            {
                // sin búsqueda mostramos el último mensaje de cada transmisión: el archivo de un vistazo
                foreach (var c in N.Arch.Lista)
                {
                    var m = c.Mensajes.LastOrDefault();
                    if (m == null) continue;
                    golpes.Add(new Golpe { C = c, M = m, Extracto = Recortar(m.Texto, 0, 150), Pos = -1 });
                }
                return;
            }
            foreach (var par in N.Arch.Buscar(q, 300))
            {
                int i = par.Value.Texto.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                golpes.Add(new Golpe { C = par.Key, M = par.Value, Extracto = Recortar(par.Value.Texto, i, 150), Pos = i });
            }
        }

        static string Recortar(string s, int pos, int largo)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace("  ", " ").Trim();
            if (s.Length <= largo) return s;
            int ini = Math.Max(0, pos - largo / 3);
            string t = s.Substring(ini, Math.Min(largo, s.Length - ini));
            return (ini > 0 ? "…" : "") + t + (ini + largo < s.Length ? "…" : "");
        }

        /// <summary>El panel de estadísticas se mide por su fila más ancha, no por un número fijo.</summary>
        int AnchoDer
        {
            get
            {
                var g = Maquetador.Medidor;
                var fk = Tema.Fina(8.5f);
                var fv = Tema.MonoMedia(8.5f);
                int w = 0;
                foreach (var e in new[] { "tiempo generando", "velocidad media", "tokens generados", "transmisiones" })
                    w = Math.Max(w, Tema.Medir(g, e, fk).Width);
                w += Tema.Medir(g, "99.999 tok/s", fv).Width + S(26);
                foreach (var m in N.Arch.Lista.Select(c => c.Modelo).Distinct().Where(m => m.Length > 0))
                    w = Math.Max(w, Math.Min(S(320), Tema.Medir(g, Corto(m, 26), Tema.Mono(7.5f)).Width + Tema.Medir(g, "99,9 tok/s", Tema.Mono(7.5f)).Width + S(26)));
                w = Math.Max(w, btExportarTodo.AnchoDeseado + S(24));
                return Math.Max(S(230), Math.Min(S(420), w + S(24)));
            }
        }

        public override void Acomodar()
        {
            if (Width < 10) return;
            esc = Dpi.Escala(this);
            int anchoDer = AnchoDer;
            int wLimpiar = btLimpiar.AnchoDeseado;
            busqueda.SetBounds(S(12), S(34), Math.Max(S(120), Width - anchoDer - wLimpiar - S(40)), S(28));
            btLimpiar.SetBounds(busqueda.Right + S(10), S(34), wLimpiar, S(28));
            btExportarTodo.SetBounds(Width - anchoDer + S(12), Height - S(38), Math.Max(btExportarTodo.AnchoDeseado, anchoDer - S(24)), S(26));
        }

        // ------------------------------------------------------------------ interacción

        int AltoFila => S(50);
        int AltoLista => Height - S(76);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = rects.FindIndex(r => r.Contains(e.Location));
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = rects.FindIndex(r => r.Contains(e.Location));
            if (i >= 0 && i < golpes.Count) Abrir?.Invoke(golpes[i].C);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, golpes.Count * AltoFila - AltoLista);
            desplaz = Math.Max(0, Math.Min(max, desplaz - Math.Sign(e.Delta) * AltoFila));
            Invalidate();
            base.OnMouseWheel(e);
        }

        // ------------------------------------------------------------------ pintura

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            esc = Dpi.Escala(this);
            if (N.Cfg.Reticula) Tema.Reticula(g, ClientRectangle, S(22), Tema.Alpha(Tema.Reticulado, 170));
            rects.Clear();

            int anchoDer = AnchoDer;
            int x = S(12), w = Width - anchoDer - S(30);
            string q = busqueda.Texto.Trim();

            Tema.Rotulo(g, "04", "registro", new Rectangle(x, S(12), w, S(14)), Tema.Cielo, esc,
                q.Length > 0 ? golpes.Count + " coincidencias" : N.Arch.Lista.Count + " transmisiones");

            int y = S(72);
            int alto = AltoLista;
            g.SetClip(new Rectangle(x, y, w, alto));
            int yy = y - desplaz;
            var fT = Tema.Fina(9.5f);
            var fE = Tema.Fina(8.5f);
            var fM = Tema.Mono(7.5f);
            for (int i = 0; i < golpes.Count; i++, yy += AltoFila)
            {
                var gp = golpes[i];
                var r = new Rectangle(x, yy, w, AltoFila - S(4));
                rects.Add(r);
                if (yy + AltoFila < y || yy > y + alto) continue;
                bool hov = hover == i;
                var c = gp.M.Rol == Rol.Usuario ? Tema.Teal : N.ColorPersona(N.PersonaDe(gp.C.Persona));
                if (hov)
                {
                    using (var b = new SolidBrush(Tema.Alpha(Tema.Consola, 200))) g.FillRectangle(b, r);
                    using (var p = new Pen(Tema.Alpha(c, 130), 1f)) g.DrawRectangle(p, r);
                }
                using (var b = new SolidBrush(Tema.Alpha(c, hov ? 230 : 130))) g.FillRectangle(b, r.X, r.Y, S(2), r.Height);

                Tema.Texto_(g, gp.C.NombreVisible, fT, hov ? Tema.Texto : Tema.Suave,
                    new Rectangle(r.X + S(12), r.Y + S(5), r.Width - S(190), S(15)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                string meta = (gp.M.Rol == Rol.Usuario ? "operador" : "capcom") + "  ·  " + gp.M.Hora.ToString("dd/MM/yy HH:mm");
                int wm = Tema.Medir(g, meta, fM).Width;
                Tema.Texto_(g, meta, fM, Tema.Alpha(Tema.Apagado, hov ? 220 : 160),
                    new Rectangle(r.Right - wm - S(10), r.Y + S(5), wm + S(3), S(15)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                PintarExtracto(g, gp, q, new Rectangle(r.X + S(12), r.Y + S(22), r.Width - S(22), S(18)), fE, hov);
            }
            g.ResetClip();
            if (golpes.Count == 0)
                Tema.Texto_(g, q.Length > 0 ? "nada con «" + q + "»" : "el archivo está vacío · mandá algo desde MISIÓN",
                    Tema.Fina(9.5f), Tema.Fantasma, new Rectangle(x + S(4), y + S(12), w, S(30)));

            int max = Math.Max(0, golpes.Count * AltoFila - alto);
            if (max > 0)
            {
                int hb = Math.Max(S(24), (int)((double)alto / (golpes.Count * AltoFila) * alto));
                int yb = y + (int)((double)desplaz / max * (alto - hb));
                using (var b = new SolidBrush(Tema.Alpha(Tema.Suave, 55))) g.FillRectangle(b, x + w + S(4), yb, S(2), hb);
            }

            // ---------------- panel de estadísticas
            using (var p = new Pen(Tema.Filete, 1f)) g.DrawLine(p, Width - anchoDer, 0, Width - anchoDer, Height);
            Estadisticas(g, new Rectangle(Width - anchoDer + S(12), S(12), anchoDer - S(24), Height - S(60)));
        }

        /// <summary>Dibuja el extracto resaltando el pedazo buscado: sin eso hay que leer la línea entera.</summary>
        void PintarExtracto(Graphics g, Golpe gp, string q, Rectangle r, Font f, bool hov)
        {
            var baseCol = hov ? Tema.Alpha(Tema.Suave, 230) : Tema.Apagado;
            if (q.Length == 0 || gp.Pos < 0)
            {
                Tema.Texto_(g, gp.Extracto, f, baseCol, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            int k = gp.Extracto.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (k < 0)
            {
                Tema.Texto_(g, gp.Extracto, f, baseCol, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            string a = gp.Extracto.Substring(0, k), b = gp.Extracto.Substring(k, q.Length), c = gp.Extracto.Substring(k + q.Length);
            int x = r.X;
            int wa = Tema.Medir(g, a, f).Width;
            Tema.Texto_(g, a, f, baseCol, new Rectangle(x, r.Y, wa + S(2), r.Height));
            x += wa;
            int wb = Tema.Medir(g, b, f).Width;
            using (var br = new SolidBrush(Tema.Alpha(Tema.Ambar, 40))) g.FillRectangle(br, x - S(1), r.Y + S(2), wb + S(2), r.Height - S(4));
            Tema.Texto_(g, b, Tema.Media(8.5f), Tema.Ambar, new Rectangle(x, r.Y, wb + S(2), r.Height));
            x += wb;
            if (x < r.Right)
                Tema.Texto_(g, c, f, baseCol, new Rectangle(x, r.Y, r.Right - x, r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void Estadisticas(Graphics g, Rectangle r)
        {
            int x = r.X, w = r.Width, y = r.Y;
            var todas = N.Arch.Lista.SelectMany(c => c.Mensajes).ToList();
            var resp = todas.Where(m => m.Rol == Rol.Asistente && m.Tokens > 0).ToList();

            Tema.Rotulo(g, "", "la estación en números", new Rectangle(x, y, w, S(14)), Tema.Salvia, esc);
            y += S(22);

            var pares = new List<string[]>
            {
                new[] { "transmisiones", N.Arch.Lista.Count.ToString("N0") },
                new[] { "mensajes", todas.Count.ToString("N0") },
                new[] { "tuyos", todas.Count(m => m.Rol == Rol.Usuario).ToString("N0") },
                new[] { "palabras", N.Arch.Lista.Sum(c => c.Palabras).ToString("N0") },
                new[] { "tokens generados", resp.Sum(m => (double)m.Tokens).ToString("N0") },
                new[] { "tiempo generando", TiempoCorto(TimeSpan.FromMilliseconds(resp.Sum(m => (double)m.Ms))) },
                new[] { "velocidad media", resp.Count > 0 ? (resp.Sum(m => (double)m.Tokens) / Math.Max(0.001, resp.Sum(m => m.Ms) / 1000.0)).ToString("0.00") + " tok/s" : "—" },
                new[] { "mejor marca", resp.Count > 0 ? resp.Max(m => m.TokPorSeg).ToString("0.00") + " tok/s" : "—" },
                new[] { "primera", N.Arch.Lista.Count > 0 ? N.Arch.Lista.Min(c => c.Creada).ToString("dd/MM/yy") : "—" },
                new[] { "última", N.Arch.Lista.Count > 0 ? Tema.Relativo(N.Arch.Lista.Max(c => c.Tocada)) : "—" },
            };
            var fk = Tema.Fina(8.5f);
            var fv = Tema.MonoMedia(8.5f);
            foreach (var p in pares)
            {
                Tema.Texto_(g, p[0], fk, Tema.Apagado, new Rectangle(x, y, w - S(90), S(15)));
                Tema.Texto_(g, p[1], fv, Tema.Suave, new Rectangle(x + w - S(96), y, S(96), S(15)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                y += S(16);
            }
            y += S(10);

            // --- por persona
            Tema.Rotulo(g, "", "por persona", new Rectangle(x, y, w, S(14)), Tema.Malva, esc);
            y += S(20);
            var porPersona = N.Personas
                .Select(p => new { P = p, N = N.Arch.Lista.Count(c => c.Persona == p.Clave) })
                .Where(t => t.N > 0).OrderByDescending(t => t.N).ToList();
            int maxP = porPersona.Count > 0 ? porPersona.Max(t => t.N) : 1;
            foreach (var t in porPersona)
            {
                var c = N.ColorPersona(t.P);
                Tema.Tracking(g, t.P.Nombre, Tema.Media(7.5f), Tema.Alpha(Tema.Suave, 220), x, y, S(12), S(2));
                string sv = t.N.ToString();
                int wv = Tema.Medir(g, sv, Tema.Mono(7.5f)).Width;
                Tema.Texto_(g, sv, Tema.Mono(7.5f), c, new Rectangle(x + w - wv, y, wv + S(3), S(12)), TextFormatFlags.Right | TextFormatFlags.Top);
                y += S(14);
                Tema.Segmentos(g, new RectangleF(x, y, w, S(4)), t.N / (float)maxP, c, esc, 20);
                y += S(11);
            }
            if (porPersona.Count == 0) { Tema.Texto_(g, "todavía nada", Tema.Fina(8.5f), Tema.Fantasma, new Rectangle(x, y, w, S(16))); y += S(18); }
            y += S(8);

            // --- por modelo
            Tema.Rotulo(g, "", "por modelo", new Rectangle(x, y, w, S(14)), Tema.Ambar, esc);
            y += S(20);
            var porModelo = resp.Where(m => m.Modelo.Length > 0).GroupBy(m => m.Modelo)
                .Select(gr => new { M = gr.Key, N = gr.Count(), V = gr.Average(m => m.TokPorSeg) })
                .OrderByDescending(t => t.N).Take(5).ToList();
            foreach (var t in porModelo)
            {
                Tema.Texto_(g, Corto(t.M, 26), Tema.Mono(7.5f), Tema.Alpha(Tema.Suave, 215), new Rectangle(x, y, w - S(70), S(13)),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
                Tema.Texto_(g, t.V.ToString("0.0") + " tok/s", Tema.Mono(7.5f), Tema.Ambar, new Rectangle(x + w - S(70), y, S(70), S(13)), TextFormatFlags.Right | TextFormatFlags.Top);
                y += S(15);
            }
            if (porModelo.Count == 0) Tema.Texto_(g, "ninguna respuesta del modelo todavía", Tema.Fina(8.5f), Tema.Fantasma, new Rectangle(x, y, w, S(16)));
        }

        static string Corto(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        static string TiempoCorto(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "—";
            if (t.TotalMinutes < 1) return t.TotalSeconds.ToString("0.0") + " s";
            if (t.TotalHours < 1) return ((int)t.TotalMinutes) + " m " + t.Seconds + " s";
            return ((int)t.TotalHours) + " h " + t.Minutes + " m";
        }
    }
}
