using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>Lista de transmisiones guardadas: título, cuándo, cuántos mensajes y un punto por persona.</summary>
    internal sealed class ListaTransmisiones : Control, IRueda
    {
        readonly Nucleo N;
        public List<Conversacion> Filas = new List<Conversacion>();
        public Conversacion Seleccionada;
        public string Filtro = "";
        int desplaz, hover = -1, hoverBorrar = -1;
        float esc = 1f;
        readonly List<Rectangle> rects = new List<Rectangle>();

        public event Action<Conversacion> Elegida;
        public event Action<Conversacion> Borrar;
        public event Action<Conversacion> Fijar;

        public ListaTransmisiones(Nucleo n)
        {
            N = n;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        int S(int px) => Dpi.S(esc, px);
        int AltoFila => S(40);

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); esc = Dpi.Escala(this); }

        public void Refrescar()
        {
            Filas = N.Arch.Lista.Where(c => Coincide(c)).ToList();
            Invalidate();
        }

        bool Coincide(Conversacion c)
        {
            if (Filtro.Length == 0) return true;
            if (c.NombreVisible.IndexOf(Filtro, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return c.Mensajes.Any(m => m.Texto.IndexOf(Filtro, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public void Rueda(int delta)
        {
            int max = Math.Max(0, Filas.Count * AltoFila - Height);
            desplaz = Math.Max(0, Math.Min(max, desplaz - Math.Sign(delta) * AltoFila));
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1, hb = -1;
            for (int i = 0; i < rects.Count; i++)
                if (rects[i].Contains(e.Location))
                {
                    h = i;
                    var rb = new Rectangle(rects[i].Right - S(22), rects[i].Y + S(4), S(18), S(18));
                    if (rb.Contains(e.Location)) hb = i;
                }
            if (h != hover || hb != hoverBorrar) { hover = h; hoverBorrar = hb; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = hoverBorrar = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < rects.Count && i < Filas.Count; i++)
            {
                if (!rects[i].Contains(e.Location)) continue;
                if (e.Button == MouseButtons.Right) { Fijar?.Invoke(Filas[i]); return; }
                if (hoverBorrar == i) { Borrar?.Invoke(Filas[i]); return; }
                Elegida?.Invoke(Filas[i]);
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            esc = Dpi.Escala(this);
            rects.Clear();
            if (Filas.Count == 0)
            {
                Tema.Texto_(g, Filtro.Length > 0 ? "nada con «" + Filtro + "»" : "todavía no hay transmisiones",
                    Tema.Fina(8.5f), Tema.Fantasma, new Rectangle(S(12), S(8), Width - S(20), S(30)));
                return;
            }
            var fT = Tema.Fina(9.5f);
            var fM = Tema.Mono(7.5f);
            int y = -desplaz;
            for (int i = 0; i < Filas.Count; i++, y += AltoFila)
            {
                var r = new Rectangle(0, y, Width, AltoFila - S(2));
                rects.Add(r);
                if (y + AltoFila < 0 || y > Height) continue;
                var c = Filas[i];
                bool sel = Seleccionada != null && c.Id == Seleccionada.Id;
                bool hov = hover == i;
                var acento = N.ColorPersona(N.PersonaDe(c.Persona));
                if (sel || hov)
                {
                    using (var b = new SolidBrush(sel ? Tema.Alpha(Tema.Consola, 235) : Tema.Alpha(Tema.Consola, 150)))
                        g.FillRectangle(b, r.X, r.Y, r.Width - S(4), r.Height);
                }
                if (sel) using (var b = new SolidBrush(acento)) g.FillRectangle(b, 0, r.Y, S(2), r.Height);
                else if (c.Fijada) using (var b = new SolidBrush(Tema.Alpha(Tema.Ambar, 150))) g.FillRectangle(b, 0, r.Y, S(2), r.Height);

                int x = S(11);
                Tema.Diodo(g, x, r.Y + S(13), S(4), acento, sel || hov, 0.7f);
                x += S(9);
                int wDer = hov ? S(24) : 0;
                Tema.Texto_(g, c.NombreVisible, fT, sel ? Tema.Texto : hov ? Tema.Suave : Tema.Alpha(Tema.Suave, 215),
                    new Rectangle(x, r.Y + S(4), r.Width - x - S(8) - wDer, S(15)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                string meta = Tema.Relativo(c.Tocada) + "  ·  " + c.Mensajes.Count + (c.Fijada ? "  ·  fijada" : "");
                Tema.Texto_(g, meta, fM, Tema.Alpha(Tema.Apagado, sel ? 220 : 160),
                    new Rectangle(x, r.Y + S(20), r.Width - x - S(8), S(13)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                if (hov)
                {
                    var rb = new Rectangle(r.Right - S(26), r.Y + S(4), S(18), S(18));
                    if (hoverBorrar == i)
                        Tema.Placa(g, new RectangleF(rb.X, rb.Y, rb.Width, rb.Height), S(2), Tema.Mezcla(Tema.Fondo, Tema.Rosa, 0.2f), Tema.Alpha(Tema.Rosa, 140));
                    Boton.DibujarGlifo(g, Boton.Glifo.Basura, new RectangleF(rb.X + S(5), rb.Y + S(4), S(9), S(10)),
                        hoverBorrar == i ? Tema.Rosa : Tema.Apagado, esc);
                }
            }
            int max = Math.Max(0, Filas.Count * AltoFila - Height);
            if (max > 0)
            {
                int hb = Math.Max(S(20), (int)((double)Height / (Filas.Count * AltoFila) * Height));
                int yb = (int)((double)desplaz / max * (Height - hb));
                using (var b = new SolidBrush(Tema.Alpha(Tema.Suave, 55))) g.FillRectangle(b, Width - S(2), yb, S(2), hb);
            }
        }
    }

    /// <summary>
    /// MISIÓN: la pantalla principal. Tres columnas — el archivo de transmisiones a la izquierda, la
    /// transcripción y el compositor en el medio, y la telemetría a la derecha. Los dos rieles se pueden
    /// plegar; el del medio nunca.
    /// </summary>
    internal sealed class VistaChat : Pantalla
    {
        public override string Nombre => "misión";

        readonly Hilo hilo;
        readonly Compositor comp;
        readonly ListaTransmisiones lista;
        readonly Campo busqueda;
        readonly Boton btNueva, btPlegarIzq, btPlegarDer;
        readonly Serie sVelocidad, sLatencia;
        readonly Orbita orbita;
        int hoverPersona = -1;
        readonly List<Rectangle> zonasPersona = new List<Rectangle>();

        public VistaChat(Nucleo n) : base(n)
        {
            hilo = new Hilo(n);
            hilo.Accion += AccionMensaje;
            hilo.Sugerencia += s => { comp.Poner(s); };
            Controls.Add(hilo);

            comp = new Compositor(n);
            comp.Enviar += Mandar;
            comp.Cortar += () => N.Tx.Cortar();
            comp.CambiarPersona += SiguientePersona;
            comp.Adjuntar += Adjuntar;
            comp.SubirAlUltimo += () =>
            {
                var ult = N.Actual != null ? N.Actual.Mensajes.LastOrDefault(m => m.Rol == Rol.Usuario) : null;
                if (ult != null) comp.Poner(ult.Texto);
            };
            comp.AltoCambio += () => { Acomodar(); Invalidate(); };
            Controls.Add(comp);

            lista = new ListaTransmisiones(n);
            lista.Elegida += c => Abrir(c);
            lista.Borrar += c => BorrarConversacion(c);
            lista.Fijar += c => { c.Fijada = !c.Fijada; N.Arch.Guardar(c); N.Arch.Ordenar(); lista.Refrescar(); Invalidate(); };
            Controls.Add(lista);

            busqueda = new Campo { Pista = "buscar en el archivo…" };
            busqueda.Cambio += (s, e) => { lista.Filtro = busqueda.Texto.Trim(); lista.Refrescar(); };
            Controls.Add(busqueda);

            btNueva = new Boton { Text = "NUEVA", Icono = Boton.Glifo.Mas, Primario = true, Acento = Tema.Malva, Atajo = "^N" };
            btNueva.Accion += (s, e) => Nueva();
            Controls.Add(btNueva);

            btPlegarIzq = new Boton { Icono = Boton.Glifo.Flecha, Acento = Tema.Suave };
            btPlegarIzq.Accion += (s, e) => { N.Cfg.RielIzquierdo = !N.Cfg.RielIzquierdo; N.Cfg.Guardar(); Acomodar(); Invalidate(); };
            Controls.Add(btPlegarIzq);

            btPlegarDer = new Boton { Icono = Boton.Glifo.Flecha, Acento = Tema.Suave };
            btPlegarDer.Accion += (s, e) => { N.Cfg.RielDerecho = !N.Cfg.RielDerecho; N.Cfg.Guardar(); Acomodar(); Invalidate(); };
            Controls.Add(btPlegarDer);

            sVelocidad = new Serie { Etiqueta = "velocidad tok/s", Color = Tema.Ambar, Formato = v => v.ToString("0.0"), Limite = 120, Vacio = "sin transmisiones" };
            Controls.Add(sVelocidad);
            sLatencia = new Serie { Etiqueta = "latencia 1er token (s)", Color = Tema.Cielo, Formato = v => v.ToString("0.00"), Limite = 120, Vacio = "sin muestras" };
            Controls.Add(sLatencia);

            orbita = new Orbita { Acento = Tema.Malva };
            Controls.Add(orbita);
        }

        // ------------------------------------------------------------------ acciones

        public void Nueva()
        {
            if (N.Actual != null && N.Actual.Mensajes.Count == 0) { comp.Focus(); return; }
            var c = N.Arch.Nueva(N.Cfg.PersonaActiva);
            Abrir(c);
        }

        public void Abrir(Conversacion c)
        {
            N.Actual = c;
            ResolverAnchos();
            lista.Seleccionada = c;
            hilo.Poner(c);
            comp.Estado();
            lista.Refrescar();
            Acomodar();
            Invalidate();
            comp.Focus();
        }

        void BorrarConversacion(Conversacion c)
        {
            bool era = N.Actual != null && N.Actual.Id == c.Id;
            N.Arch.Borrar(c);
            N.Log.Info("Transmisión borrada: " + c.NombreVisible);
            if (era) { N.Actual = N.Arch.Lista.FirstOrDefault() ?? N.Arch.Nueva(N.Cfg.PersonaActiva); hilo.Poner(N.Actual); }
            lista.Seleccionada = N.Actual;
            lista.Refrescar();
            Invalidate();
        }

        public void Mandar(string texto)
        {
            if (N.Actual == null) N.Actual = N.Arch.Nueva(N.Cfg.PersonaActiva);
            if (!N.Tx.Enviar(N.Actual, texto)) return;
            lista.Seleccionada = N.Actual;
            hilo.Poner(N.Actual);
            hilo.AlFinal();
            lista.Refrescar();
            comp.Estado();
            Invalidate();
        }

        void AccionMensaje(Mensaje m, string que)
        {
            if (N.Actual == null) return;
            switch (que)
            {
                case "copiar":
                    try { Clipboard.SetText(m.Texto); N.Log.Ok("Mensaje copiado"); Sonidos.Tic(); } catch { }
                    break;
                case "rehacer":
                    if (N.Tx.Regenerar(N.Actual)) { hilo.Poner(N.Actual); comp.Estado(); }
                    break;
                case "editar":
                    if (m.Rol == Rol.Usuario)
                    {
                        comp.Poner(m.Texto);
                        int i = N.Actual.Mensajes.IndexOf(m);
                        if (i >= 0) N.Actual.Mensajes.RemoveRange(i, N.Actual.Mensajes.Count - i);
                        hilo.Poner(N.Actual);
                    }
                    else comp.Poner(m.Texto);
                    break;
                case "borrar":
                    {
                        int i = N.Actual.Mensajes.IndexOf(m);
                        if (i >= 0) N.Actual.Mensajes.RemoveAt(i);
                        N.Arch.Guardar(N.Actual);
                        hilo.Poner(N.Actual);
                        break;
                    }
            }
            Invalidate();
        }

        void SiguientePersona()
        {
            if (N.Personas.Count == 0) return;
            var actual = N.PersonaActual;
            int i = N.Personas.IndexOf(actual);
            var p = N.Personas[(i + 1) % N.Personas.Count];
            PonerPersona(p);
        }

        public void PonerPersona(Persona p)
        {
            if (p == null) return;
            N.Cfg.PersonaActiva = p.Clave;
            N.Cfg.Guardar();
            if (N.Actual != null) { N.Actual.Persona = p.Clave; N.Arch.Guardar(N.Actual); }
            orbita.Acento = N.ColorPersona(p);
            btNueva.Acento = N.ColorPersona(p);
            comp.Estado();
            lista.Refrescar();
            N.Log.Info("Persona: " + p.Nombre + " · " + p.Nota);
            Invalidate();
        }

        void Adjuntar()
        {
            using (var d = new OpenFileDialog
            {
                Title = "Pegar un archivo de texto en el mensaje",
                Filter = "Texto y código|*.txt;*.md;*.cs;*.js;*.ts;*.py;*.go;*.sql;*.json;*.xml;*.yaml;*.yml;*.ps1;*.sh;*.css;*.html;*.log;*.csv|Todos|*.*",
                CheckFileExists = true,
            })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    var fi = new FileInfo(d.FileName);
                    if (fi.Length > 400 * 1024)
                    {
                        MessageBox.Show(FindForm(), "Ese archivo pesa " + Tema.Bytes(fi.Length) + ". El tope son 400 KB para no reventar la ventana de contexto.",
                            "CAPCOM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    string txt = File.ReadAllText(d.FileName);
                    string ext = Path.GetExtension(d.FileName).TrimStart('.').ToLowerInvariant();
                    string lg = Resaltador.Normalizar(ext);
                    comp.Agregar("Archivo `" + Path.GetFileName(d.FileName) + "` (" + Tema.Bytes(fi.Length) + ", ~" + Cliente.Estimar(txt) + " tok):" +
                                 Environment.NewLine + "```" + (lg.Length > 0 ? lg : ext) + Environment.NewLine + txt.TrimEnd() + Environment.NewLine + "```");
                    N.Log.Info("Adjuntado " + Path.GetFileName(d.FileName) + " · " + Tema.Bytes(fi.Length));
                }
                catch (Exception ex) { N.Log.Error("No pude leer el archivo: " + ex.Message); }
            }
        }

        public void Exportar()
        {
            var c = N.Actual;
            if (c == null || c.Mensajes.Count == 0) { N.Log.Aviso("No hay nada para exportar"); return; }
            using (var d = new SaveFileDialog
            {
                Title = "Exportar la transmisión",
                Filter = "Markdown|*.md|Texto|*.txt",
                FileName = Limpio(c.NombreVisible) + ".md",
            })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# " + c.NombreVisible).AppendLine();
                sb.AppendLine("- transmisión `" + c.Id + "`");
                sb.AppendLine("- " + c.Creada.ToString("dd/MM/yyyy HH:mm") + " → " + c.Tocada.ToString("dd/MM/yyyy HH:mm"));
                sb.AppendLine("- persona: " + (N.PersonaDe(c.Persona) != null ? N.PersonaDe(c.Persona).Nombre : c.Persona));
                if (c.Modelo.Length > 0) sb.AppendLine("- modelo: `" + c.Modelo + "`");
                sb.AppendLine().AppendLine("---").AppendLine();
                foreach (var m in c.Mensajes)
                {
                    sb.AppendLine("## " + (m.Rol == Rol.Usuario ? "Operador" : m.Motor ? "A bordo" : "CAPCOM") + " · " + m.Hora.ToString("HH:mm:ss"));
                    if (m.Rol == Rol.Asistente && m.Tokens > 0)
                        sb.AppendLine("`" + (m.Ms / 1000.0).ToString("0.0") + " s · " + m.Tokens + " tokens · " + m.TokPorSeg.ToString("0.0") + " tok/s`").AppendLine();
                    sb.AppendLine().AppendLine(m.Texto).AppendLine();
                }
                try
                {
                    File.WriteAllText(d.FileName, sb.ToString(), new System.Text.UTF8Encoding(false));
                    N.Log.Ok("Exportado a " + d.FileName);
                    Sonidos.Ok();
                }
                catch (Exception ex) { N.Log.Error("No pude exportar: " + ex.Message); }
            }
        }

        static string Limpio(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Length > 60 ? s.Substring(0, 60).Trim() : s.Trim();
        }

        // ------------------------------------------------------------------ ciclo

        bool seriesSembradas;

        /// <summary>
        /// Las series arrancan con lo que ya pasó, no vacías: al abrir la app, la velocidad y la latencia de las
        /// últimas respuestas guardadas dicen más que dos gráficos en blanco esperando a que transmitas.
        /// </summary>
        void SembrarSeries()
        {
            if (seriesSembradas) return;
            seriesSembradas = true;
            var ultimas = N.Arch.Lista
                .SelectMany(c => c.Mensajes)
                .Where(m => m.Rol == Rol.Asistente && m.TokPorSeg > 0)
                .OrderBy(m => m.Hora)
                .Reverse().Take(60).Reverse().ToList();
            foreach (var m in ultimas)
            {
                sVelocidad.Empujar(m.TokPorSeg);
                if (m.MsPrimerToken > 0) sLatencia.Empujar(m.MsPrimerToken / 1000.0);
            }
        }

        public override void Refrescar()
        {
            ResolverAnchos();
            SembrarSeries();
            lista.Refrescar();
            comp.Estado();
            orbita.Activa = N.Tx != null && N.Tx.Ocupado;
            Invalidate();
        }

        /// <summary>Se llama en cada latido: mueve lo que tiene que moverse y nada más.</summary>
        public void Latido()
        {
            bool ocupado = N.Tx != null && N.Tx.Ocupado;
            if (orbita.Activa != ocupado) { orbita.Activa = ocupado; comp.Estado(); }
            if (N.Tx != null && N.Tx.Novedad) { hilo.Novedad(); InvalidarTelemetria(); }
            else if (ocupado) InvalidarTelemetria();
        }

        void InvalidarTelemetria()
        {
            if (N.Cfg.RielDerecho) Invalidate(new Rectangle(Width - AnchoDer, 0, AnchoDer, Height));
        }

        public void RegistrarResultado(Resultado r)
        {
            if (r == null) return;
            if (r.TokPorSeg > 0) sVelocidad.Empujar(r.TokPorSeg);
            if (r.MsPrimerToken > 0) sLatencia.Empujar(r.MsPrimerToken / 1000.0);
            comp.Estado();
            lista.Refrescar();
            Invalidate();
        }

        public void Foco() => comp.Focus();
        public void AlFinal() => hilo.AlFinal();
        public void Desplazar(int pag) => hilo.Desplazar(pag);
        public Compositor Comp => comp;

        public override void Modo(string que)
        {
            switch ((que ?? "").ToLowerInvariant())
            {
                case "vacio": case "vacío": N.Actual = N.Arch.Nueva(N.Cfg.PersonaActiva); Abrir(N.Actual); break;
                case "sinrieles": N.Cfg.RielIzquierdo = N.Cfg.RielDerecho = false; Acomodar(); break;
                case "rieles": N.Cfg.RielIzquierdo = N.Cfg.RielDerecho = true; Acomodar(); break;
            }
            Invalidate();
        }

        // ------------------------------------------------------------------ maquetación y rieles

        int anchoIzq, anchoDer;
        int arrastrando;            // 0 = nada · 1 = riel izquierdo · 2 = riel derecho
        int agarreX;
        bool sobreDivisor;

        int AnchoIzq => N.Cfg.RielIzquierdo ? anchoIzq : S(24);
        int AnchoDer => N.Cfg.RielDerecho ? anchoDer : S(24);

        int MinIzq => S(150);
        int MinDer => S(186);
        int MaxRiel => Math.Max(S(200), (int)(Width * 0.42));

        /// <summary>
        /// ⭐ El ancho de cada riel sale de lo que hay ADENTRO, no de un número inventado: se mide el título más
        /// largo del archivo, la lectura más ancha de la telemetría, los nombres de las personas. Así no queda
        /// nada recortado con «…» al pedo ni sobra medio riel vacío. Y si el usuario lo arrastra, su medida gana
        /// (queda guardada en config) hasta que haga doble clic en el divisor para volver al automático.
        /// </summary>
        public void ResolverAnchos()
        {
            esc = Dpi.Escala(this);
            anchoIzq = N.Cfg.AnchoIzq > 0 ? N.Cfg.AnchoIzq : AutoIzq();
            anchoDer = N.Cfg.AnchoDer > 0 ? N.Cfg.AnchoDer : AutoDer();
            anchoIzq = Math.Max(MinIzq, Math.Min(MaxRiel, anchoIzq));
            anchoDer = Math.Max(MinDer, Math.Min(MaxRiel, anchoDer));
            // el centro manda: si los dos rieles lo ahogan, se les recorta lo mismo a cada uno
            int centro = Width - anchoIzq - anchoDer - S(12);
            int minCentro = S(360);
            if (centro < minCentro)
            {
                int sobra = minCentro - centro;
                int quitoIzq = Math.Min(sobra / 2, Math.Max(0, anchoIzq - MinIzq));
                anchoIzq -= quitoIzq;
                int quitoDer = Math.Min(sobra - quitoIzq, Math.Max(0, anchoDer - MinDer));
                anchoDer -= quitoDer;
            }
        }

        int AutoIzq()
        {
            var g = Maquetador.Medidor;
            var fT = Tema.Fina(9.5f);
            var fM = Tema.Mono(7.5f);
            // el ancho de fila = punto + título + el botón de borrar que aparece al pasar el mouse
            int ancho = S(11) + S(9) + S(8) + S(26);
            int texto = Tema.Medir(g, "transmisión nueva", fT).Width;
            foreach (var c in N.Arch.Lista.Take(60))
            {
                texto = Math.Max(texto, Tema.Medir(g, c.NombreVisible, fT).Width);
                texto = Math.Max(texto, Tema.Medir(g, Tema.Relativo(c.Tocada) + "  ·  " + c.Mensajes.Count + "  ·  fijada", fM).Width);
            }
            // la caja de búsqueda y el botón NUEVA también tienen que entrar cómodos
            int piso = Tema.Medir(g, "buscar en el archivo…", Tema.Fina(10f)).Width + S(34);
            return Math.Max(piso, ancho + Math.Min(texto, S(300))) + S(14);
        }

        int AutoDer()
        {
            var g = Maquetador.Medidor;
            int w = 0;
            var fEt = Tema.Media(7f);
            var fMono = Tema.Mono(7.5f);
            // la fila más ancha de la telemetría es «VENTANA … 12.345 / 131.072»
            w = Math.Max(w, Tema.MedirTracking(g, "VENTANA", fEt, S(2)) + Tema.Medir(g, "999.999 / 999.999", fMono).Width + S(22));
            w = Math.Max(w, Tema.MedirTracking(g, "MEMORIA", fEt, S(2)) + Tema.Medir(g, "999,9 GB libres", fMono).Width + S(22));
            // el nombre del modelo cargado, que es lo que más largo se pone
            string modelo = N.Cli.Estado.ModeloCorto.Length > 0 ? N.Cli.Estado.ModeloCorto : "gemma-4-E2B-it-qat-UD-Q4_K_XL";
            w = Math.Max(w, S(13) + Math.Min(Tema.Medir(g, modelo, fMono).Width, S(300)));
            // el tablero GO / NO-GO
            var fc = Tema.Media(7.5f);
            w = Math.Max(w, S(12) + Tema.MedirTracking(g, "TRANSMITIENDO", fc, S(2)) + Tema.MedirTracking(g, "NO-GO", fc, S(1)) + S(16));
            // las personas y su nota
            foreach (var p in N.Personas)
                w = Math.Max(w, S(8) + Tema.MedirTracking(g, p.Nombre, fc, S(2)) + S(10));
            // las dos lecturas grandes, que van de a dos por fila
            w = Math.Max(w, (Tema.Medir(g, "88.88", Tema.MonoMedia(14f)).Width + S(24)) * 2);
            return w + S(24);
        }

        /// <summary>El divisor de cada riel, unos pocos píxeles donde el cursor cambia y se puede arrastrar.</summary>
        Rectangle DivIzq => new Rectangle(AnchoIzq - S(13), 0, S(7), Height);
        Rectangle DivDer => new Rectangle(Width - AnchoDer - S(2), 0, S(7), Height);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (N.Cfg.RielIzquierdo && DivIzq.Contains(e.Location)) { arrastrando = 1; agarreX = e.X - anchoIzq; Capture = true; return; }
                if (N.Cfg.RielDerecho && DivDer.Contains(e.Location)) { arrastrando = 2; agarreX = e.X + anchoDer; Capture = true; return; }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (arrastrando != 0)
            {
                Capture = false;
                if (arrastrando == 1) N.Cfg.AnchoIzq = anchoIzq; else N.Cfg.AnchoDer = anchoDer;
                N.Cfg.Guardar();
                arrastrando = 0;
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // doble clic en el divisor: volver a la medida que pide el contenido
            if (N.Cfg.RielIzquierdo && DivIzq.Contains(e.Location)) { N.Cfg.AnchoIzq = 0; N.Cfg.Guardar(); ResolverAnchos(); Acomodar(); Invalidate(); return; }
            if (N.Cfg.RielDerecho && DivDer.Contains(e.Location)) { N.Cfg.AnchoDer = 0; N.Cfg.Guardar(); ResolverAnchos(); Acomodar(); Invalidate(); return; }
            base.OnMouseDoubleClick(e);
        }

        public override void Acomodar()
        {
            if (Width < 10 || Height < 10) return;
            esc = Dpi.Escala(this);
            if (anchoIzq == 0 || anchoDer == 0) ResolverAnchos();
            int izq = AnchoIzq, der = AnchoDer;
            bool vi = N.Cfg.RielIzquierdo, vd = N.Cfg.RielDerecho;

            busqueda.Visible = vi; lista.Visible = vi; btNueva.Visible = vi;
            sVelocidad.Visible = vd; sLatencia.Visible = vd; orbita.Visible = vd;

            if (vi)
            {
                btNueva.SetBounds(S(12), S(38), izq - S(24), S(26));
                busqueda.SetBounds(S(12), S(70), izq - S(24), S(26));
                lista.SetBounds(0, S(104), izq - S(10), Height - S(112));
            }
            btPlegarIzq.SetBounds(vi ? izq - S(22) : S(4), S(8), S(18), S(18));
            btPlegarDer.SetBounds(vd ? Width - der + S(4) : Width - S(22), S(8), S(18), S(18));

            int xc = izq + S(6), anchoC = Math.Max(S(320), Width - izq - der - S(12));
            int altoComp = Math.Max(S(84), Math.Min(S(240), comp.AltoDeseado));
            hilo.SetBounds(xc, S(4), anchoC, Math.Max(S(80), Height - altoComp - S(14)));
            // ⭐ el compositor se alinea EXACTAMENTE con la banda de la transcripción: si el chat va al 90 %,
            //    la caja de escribir empieza y termina donde empiezan y terminan los mensajes.
            var banda = hilo.Banda;
            comp.SetBounds(xc + banda.X - S(6), Height - altoComp - S(6), banda.Width + S(12), altoComp);
            comp.Acomodar();

            if (vd)
            {
                int x = Width - der + S(12), w = der - S(24);
                // el orbital va ARRIBA de las series, centrado: en la esquina superior pisaba el nombre del modelo
                orbita.SetBounds(x + (w - S(58)) / 2, Height - S(226), S(58), S(58));
                sVelocidad.SetBounds(x, Height - S(154), w, S(64));
                sLatencia.SetBounds(x, Height - S(82), w, S(64));
            }
        }

        // ------------------------------------------------------------------ pintura de los rieles

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            esc = Dpi.Escala(this);
            int izq = AnchoIzq, der = AnchoDer;

            // --- riel izquierdo
            if (N.Cfg.RielIzquierdo)
            {
                using (var b = new SolidBrush(Tema.Panel)) g.FillRectangle(b, 0, 0, izq - S(10), Height);
                Tema.Rotulo(g, "01", "archivo", new Rectangle(S(12), S(12), izq - S(40), S(14)), Tema.Malva, esc);
            }
            else
            {
                using (var b = new SolidBrush(Tema.Panel)) g.FillRectangle(b, 0, 0, izq - S(10), Height);
                Tema.Tracking(g, "A", Tema.Media(7.5f), Tema.Fantasma, S(6), S(32), S(12), 0);
            }
            using (var p = new Pen(Tema.Filete, 1f)) g.DrawLine(p, izq - S(10), 0, izq - S(10), Height);

            // --- riel derecho
            using (var p = new Pen(Tema.Filete, 1f)) g.DrawLine(p, Width - der + S(1), 0, Width - der + S(1), Height);
            if (N.Cfg.RielDerecho) Telemetria(g, new Rectangle(Width - der + S(12), S(12), der - S(24), Height - S(24)));
            else Tema.Tracking(g, "T", Tema.Media(7.5f), Tema.Fantasma, Width - S(14), S(32), S(12), 0);

            // --- agarraderas: tres puntitos en el divisor, para que se vea que se puede arrastrar
            var mouse = PointToClient(MousePosition);
            if (N.Cfg.RielIzquierdo) Agarradera(g, izq - S(10), DivIzq.Contains(mouse) || arrastrando == 1);
            if (N.Cfg.RielDerecho) Agarradera(g, Width - der + S(1), DivDer.Contains(mouse) || arrastrando == 2);
        }

        /// <summary>Tres puntitos en el medio del divisor: la única pista de que el riel se puede arrastrar.</summary>
        void Agarradera(Graphics g, int x, bool vivo)
        {
            int d = S(2), sep = S(5);
            int y = Height / 2 - sep;
            using (var b = new SolidBrush(vivo ? Tema.Alpha(N.ColorPersona(N.PersonaActual), 235) : Tema.Alpha(Tema.Suave, 60)))
                for (int i = 0; i < 3; i++) g.FillRectangle(b, x - d / 2, y + i * sep, d, d);
            if (vivo)
                using (var p = new Pen(Tema.Alpha(N.ColorPersona(N.PersonaActual), 120), 1f)) g.DrawLine(p, x, 0, x, Height);
        }

        void Telemetria(Graphics g, Rectangle r)
        {
            int x = r.X, w = r.Width;
            int y = r.Y;
            var est = N.Cli.Estado;
            bool vivo = est.Señal == Señal.Nominal;
            var cSeñal = vivo ? Tema.Teal : est.Señal == Señal.Cargando ? Tema.Ambar : Tema.Apagado;

            Tema.Rotulo(g, "02", "telemetría", new Rectangle(x, y, w - S(60), S(14)), Tema.Teal, esc);
            y += S(24);

            // --- enlace
            Tema.Diodo(g, x + S(3), y + S(6), S(5), cSeñal, vivo, vivo ? 1f : 0.4f);
            Tema.Tracking(g, vivo ? "ENLACE NOMINAL" : est.Señal == Señal.Cargando ? "CARGANDO" : "SIN SEÑAL",
                Tema.Media(8f), cSeñal, x + S(13), y, S(13), S(2));
            y += S(16);
            string modelo = est.ModeloCorto.Length > 0 ? est.ModeloCorto : est.Detalle;
            Tema.Texto_(g, modelo, Tema.Mono(7.5f), Tema.Apagado, new Rectangle(x + S(13), y, w - S(13), S(13)),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            y += S(24);

            // --- lecturas grandes
            var ult = UltimaRespuesta();
            double tokS = ult != null ? ult.TokPorSeg : 0;
            double lat = ult != null ? ult.MsPrimerToken / 1000.0 : 0;
            int mitad = w / 2;
            Tema.Lectura(g, new Rectangle(x, y, mitad - S(6), S(38)), "tok/s", tokS > 0 ? tokS.ToString("0.00") : "—", "", tokS > 0 ? Tema.Ambar : Tema.Fantasma, esc, 14f);
            Tema.Lectura(g, new Rectangle(x + mitad, y, mitad - S(6), S(38)), "1er token", lat > 0 ? lat.ToString("0.00") : "—", "s", lat > 0 ? Tema.Cielo : Tema.Fantasma, esc, 14f);
            y += S(42);

            // --- ventana de contexto
            int usados = N.Actual != null ? Cliente.Estimar(string.Join("\n", N.Actual.Mensajes.Select(m => m.Texto))) : 0;
            int ctx = est.Contexto > 0 ? est.Contexto : N.Cfg.Contexto;
            double frac = ctx > 0 ? Math.Min(1.0, usados / (double)ctx) : 0;
            Tema.Tracking(g, "VENTANA", Tema.Media(7f), Tema.Apagado, x, y, S(12), S(2));
            string sv = usados.ToString("N0") + " / " + ctx.ToString("N0");
            var fv = Tema.Mono(7.5f);
            int wv = Tema.Medir(g, sv, fv).Width;
            Tema.Texto_(g, sv, fv, frac > 0.85 ? Tema.Rosa : Tema.Suave, new Rectangle(x + w - wv, y, wv + S(3), S(12)), TextFormatFlags.Right | TextFormatFlags.Top);
            y += S(15);
            Tema.Segmentos(g, new RectangleF(x, y, w, S(5)), (float)frac, frac > 0.85 ? Tema.Rosa : frac > 0.6 ? Tema.Ambar : Tema.Teal, esc, 28);
            y += S(16);

            // --- memoria
            double libre, total;
            Win32.Memoria(out libre, out total);
            double usoMem = total > 0 ? 1 - libre / total : 0;
            Tema.Tracking(g, "MEMORIA", Tema.Media(7f), Tema.Apagado, x, y, S(12), S(2));
            string sm = libre.ToString("0.0") + " GB libres";
            int wm = Tema.Medir(g, sm, fv).Width;
            Tema.Texto_(g, sm, fv, libre < 3 ? Tema.Rosa : Tema.Suave, new Rectangle(x + w - wm, y, wm + S(3), S(12)), TextFormatFlags.Right | TextFormatFlags.Top);
            y += S(15);
            Tema.Segmentos(g, new RectangleF(x, y, w, S(5)), (float)usoMem, usoMem > 0.9 ? Tema.Rosa : Tema.Cielo, esc, 28);
            y += S(22);

            // --- tablero GO / NO-GO: la lista de verificación de la sala antes de lanzar
            Tema.Rotulo(g, "03", "go / no-go", new Rectangle(x, y, w, S(14)), Tema.Salvia, esc);
            y += S(20);
            var checks = new List<KeyValuePair<string, bool>>
            {
                new KeyValuePair<string, bool>("ENLACE", vivo),
                new KeyValuePair<string, bool>("MODELO", est.ModeloCorto.Length > 0),
                new KeyValuePair<string, bool>("MEMORIA", libre > 2.5),
                new KeyValuePair<string, bool>("ARCHIVO", N.Cfg.GuardarHistorial),
                new KeyValuePair<string, bool>("A BORDO", true),
            };
            var fc = Tema.Media(7.5f);
            foreach (var c in checks)
            {
                Tema.Diodo(g, x + S(3), y + S(5), S(4), c.Value ? Tema.Salvia : Tema.Rosa, true, 0.5f);
                Tema.Tracking(g, c.Key, fc, Tema.Alpha(Tema.Suave, 220), x + S(12), y, S(12), S(2));
                string v = c.Value ? "GO" : "NO-GO";
                int wg = Tema.MedirTracking(g, v, fc, S(1));
                Tema.Tracking(g, v, fc, c.Value ? Tema.Salvia : Tema.Rosa, x + w - wg, y, S(12), S(1));
                y += S(14);
            }
            y += S(10);

            // --- persona: el selector vive acá, no escondido en ajustes
            Tema.Rotulo(g, "04", "persona", new Rectangle(x, y, w, S(14)), N.ColorPersona(N.PersonaActual), esc);
            y += S(20);
            zonasPersona.Clear();
            var actual = N.PersonaActual;
            var mouse = PointToClient(MousePosition);
            foreach (var p in N.Personas)
            {
                bool sel = actual != null && p.Clave == actual.Clave;
                var cp = N.ColorPersona(p);
                var rp = new Rectangle(x, y, w, S(17));
                bool hov = rp.Contains(mouse);
                if (sel || hov)
                {
                    using (var b = new SolidBrush(sel ? Tema.Alpha(cp, 26) : Tema.Alpha(Tema.Texto, 10))) g.FillRectangle(b, rp);
                    using (var b = new SolidBrush(Tema.Alpha(cp, sel ? 230 : 120))) g.FillRectangle(b, rp.X, rp.Y, S(2), rp.Height);
                }
                Tema.Tracking(g, p.Nombre, Tema.Media(7.5f), sel ? cp : hov ? Tema.Suave : Tema.Alpha(Tema.Suave, 170), x + S(8), y, S(17), S(2));
                zonasPersona.Add(rp);
                y += S(19);
            }
            var nota = actual != null ? actual.Nota : "";
            if (nota.Length > 0)
            {
                // tres renglones y lo que no entre se corta con «…»: antes se cortaba a la mitad de una palabra
                int altoNota = Math.Min(S(40), Math.Max(S(13), orbita.Top - y - S(8)));
                Tema.Texto_(g, nota, Tema.Fina(8f), Tema.Alpha(Tema.Apagado, 190), new Rectangle(x + S(8), y + S(1), w - S(10), altoNota),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
        }

        Mensaje UltimaRespuesta() =>
            N.Actual != null ? N.Actual.Mensajes.LastOrDefault(m => m.Rol == Rol.Asistente && m.Tokens > 0) : null;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // --- arrastre de los divisores
            if (arrastrando == 1)
            {
                N.Cfg.AnchoIzq = Math.Max(MinIzq, Math.Min(MaxRiel, e.X - agarreX));
                ResolverAnchos(); Acomodar(); Invalidate();
                return;
            }
            if (arrastrando == 2)
            {
                N.Cfg.AnchoDer = Math.Max(MinDer, Math.Min(MaxRiel, agarreX - e.X));
                ResolverAnchos(); Acomodar(); Invalidate();
                return;
            }
            bool sobre = (N.Cfg.RielIzquierdo && DivIzq.Contains(e.Location)) || (N.Cfg.RielDerecho && DivDer.Contains(e.Location));
            if (sobre != sobreDivisor)
            {
                sobreDivisor = sobre;
                Cursor = sobre ? Cursors.SizeWE : Cursors.Default;
                Invalidate();
            }
            if (sobre) return;

            if (!N.Cfg.RielDerecho) return;
            int h = zonasPersona.FindIndex(r => r.Contains(e.Location));
            if (h != hoverPersona)
            {
                hoverPersona = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate(new Rectangle(Width - AnchoDer, 0, AnchoDer, Height));
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = zonasPersona.FindIndex(r => r.Contains(e.Location));
            if (i >= 0 && i < N.Personas.Count) PonerPersona(N.Personas[i]);
        }
    }
}
