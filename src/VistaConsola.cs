using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// CONSOLA: la sala de control. Arriba los números de la misión, abajo el diario de vuelo y la salida cruda
    /// del servidor. Es la pantalla que contesta «¿qué está pasando?» sin que haya que adivinar nada.
    /// </summary>
    internal sealed class VistaConsola : Pantalla
    {
        public override string Nombre => "consola";

        readonly LineaTiempo diario;
        readonly Boton btLimpiar, btCarpeta, btCopiarLog;
        int desplazServidor;
        string[] cacheServidor = new string[0];
        DateTime ultimaLectura = DateTime.MinValue;

        public VistaConsola(Nucleo n) : base(n)
        {
            diario = new LineaTiempo { Vacio = "el diario de vuelo está limpio" };
            Controls.Add(diario);

            btLimpiar = new Boton { Text = "AL FINAL", Icono = Boton.Glifo.Flecha, Acento = Tema.Suave };
            btLimpiar.Accion += (s, e) => { diario.Cargar(N.Log.Ultimas()); desplazServidor = 0; Invalidate(); };
            Controls.Add(btLimpiar);

            btCarpeta = new Boton { Text = "CARPETA", Icono = Boton.Glifo.Copiar, Acento = Tema.Suave };
            btCarpeta.Accion += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", N.CarpetaDatos); } catch { } };
            Controls.Add(btCarpeta);

            btCopiarLog = new Boton { Text = "COPIAR SALIDA", Icono = Boton.Glifo.Copiar, Acento = Tema.Crema };
            btCopiarLog.Accion += (s, e) =>
            {
                try { Clipboard.SetText(string.Join(Environment.NewLine, N.Srv.Salida())); N.Log.Ok("Salida del servidor copiada"); } catch { }
            };
            Controls.Add(btCopiarLog);
        }

        public void Agregar(LineaLog l) => diario.Agregar(l);

        public override void Refrescar()
        {
            diario.Cargar(N.Log.Ultimas());
            Invalidate();
        }

        public override void Acomodar()
        {
            if (Width < 10) return;
            esc = Dpi.Escala(this);
            int y = S(168);
            int mitad = (Width - S(36)) / 2;
            diario.SetBounds(S(12), y + S(22), mitad, Height - y - S(34));
            // los botones se miden por su rótulo y se apoyan en el borde derecho de SU panel
            int h = S(19);
            btLimpiar.SetBounds(S(12) + mitad - btLimpiar.AnchoDeseado, y - S(3), btLimpiar.AnchoDeseado, h);
            btCarpeta.SetBounds(Width - S(12) - btCarpeta.AnchoDeseado, y - S(3), btCarpeta.AnchoDeseado, h);
            btCopiarLog.SetBounds(btCarpeta.Left - S(7) - btCopiarLog.AnchoDeseado, y - S(3), btCopiarLog.AnchoDeseado, h);
        }

        public override void Modo(string que)
        {
            if ((que ?? "").ToLowerInvariant() == "servidor") desplazServidor = 0;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int mitad = (Width - S(36)) / 2;
            if (e.X > S(24) + mitad)
            {
                int max = Math.Max(0, cacheServidor.Length - 10);
                desplazServidor = Math.Max(0, Math.Min(max, desplazServidor - Math.Sign(e.Delta) * 3));
                Invalidate();
            }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            esc = Dpi.Escala(this);
            if (N.Cfg.Reticula) Tema.Reticula(g, ClientRectangle, S(22), Tema.Alpha(Tema.Reticulado, 170));

            int x = S(12), w = Width - S(24);
            // ---------------- encabezado: reloj de misión y estado
            var up = DateTime.Now - N.Arranque;
            var est = N.Cli.Estado;
            bool vivo = est.Señal == Señal.Nominal;

            Tema.Rotulo(g, "02", "consola de vuelo", new Rectangle(x, S(12), w, S(14)), Tema.Teal, esc,
                DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));

            var rReloj = new Rectangle(x, S(32), w, S(56));
            Tema.Esquinas(g, new RectangleF(rReloj.X, rReloj.Y, rReloj.Width, rReloj.Height), S(10), Tema.Alpha(Tema.Filete, 235));

            var fBig = Tema.MonoMedia(23f);
            string t = Tema.TMas(up);
            Tema.Texto_(g, t, fBig, Tema.Texto, new Rectangle(x + S(16), rReloj.Y + S(8), S(260), S(40)), TextFormatFlags.Left | TextFormatFlags.Top);
            int wt = Tema.Medir(g, t, fBig).Width;
            Tema.Tracking(g, "TIEMPO DE MISIÓN", Tema.Media(7f), Tema.Apagado, x + S(16), rReloj.Y + S(40), S(12), S(2));

            // diodos de estado a la derecha del reloj
            int xd = x + S(30) + wt;
            var señales = new[]
            {
                new { E = "ENLACE", V = vivo, C = vivo ? Tema.Teal : Tema.Rosa },
                new { E = "MODELO", V = est.ModeloCorto.Length > 0, C = est.ModeloCorto.Length > 0 ? Tema.Salvia : Tema.Apagado },
                new { E = "TRANSMITIENDO", V = N.Tx != null && N.Tx.Ocupado, C = Tema.Malva },
                new { E = "ARCHIVO", V = N.Cfg.GuardarHistorial, C = Tema.Cielo },
            };
            // dos columnas de dos: con tres arriba y una sola al costado la fila quedaba coja
            int anchoCol = S(132), colDiodo = 0;
            for (int i = 0; i < señales.Length; i++)
            {
                var sn = señales[i];
                int fila = i % 2, col = i / 2;
                int yy = rReloj.Y + S(14) + fila * S(16);
                int xx = xd + col * anchoCol;
                Tema.Diodo(g, xx + S(4), yy + S(5), S(5), sn.C, sn.V, sn.V ? 1f : 0f);
                Tema.Tracking(g, sn.E, Tema.Media(7f), sn.V ? Tema.Alpha(sn.C, 235) : Tema.Fantasma, xx + S(14), yy, S(11), S(2));
                colDiodo = Math.Max(colDiodo, col);
            }
            xd += (colDiodo + 1) * anchoCol;

            if (est.ModeloCorto.Length > 0)
            {
                var fm = Tema.Mono(8f);
                int wm = Tema.Medir(g, est.ModeloCorto, fm).Width;
                Tema.Texto_(g, est.ModeloCorto, fm, Tema.Alpha(Tema.Teal, 220),
                    new Rectangle(rReloj.Right - wm - S(16), rReloj.Bottom - S(22), wm + S(4), S(14)), TextFormatFlags.Right | TextFormatFlags.Top);
            }

            // ---------------- lecturas de la sesión
            var todas = N.Arch.Lista.SelectMany(c => c.Mensajes).ToList();
            var respuestas = todas.Where(m => m.Rol == Rol.Asistente && m.Tokens > 0).ToList();
            double tokTotal = respuestas.Sum(m => (double)m.Tokens);
            double segTotal = respuestas.Sum(m => m.Ms) / 1000.0;
            double velMedia = segTotal > 0.01 ? tokTotal / segTotal : 0;
            int errores = todas.Count(m => m.Error);

            int cols = 5, sep = S(16);
            int yL = S(100);
            var datos = new[]
            {
                new { E = "transmisiones", V = N.Arch.Lista.Count.ToString(), U = "", C = Tema.Malva },
                new { E = "mensajes", V = todas.Count.ToString(), U = "", C = Tema.Cielo },
                new { E = "tokens generados", V = tokTotal.ToString("N0"), U = "", C = Tema.Ambar },
                new { E = "velocidad media", V = velMedia > 0 ? velMedia.ToString("0.00") : "—", U = "tok/s", C = Tema.Teal },
                new { E = "errores", V = errores.ToString(), U = "", C = errores > 0 ? Tema.Rosa : Tema.Salvia },
            };
            // proporcional al texto de cada tarjeta (rótulo o valor, el que sea más ancho), llenando el ancho
            var nat = new int[datos.Length];
            var fEt = Tema.Media(7f);
            var fVal = Tema.MonoMedia(13f);
            for (int i = 0; i < datos.Length; i++)
                nat[i] = Math.Max(Tema.MedirTracking(g, datos[i].E.ToUpperInvariant(), fEt, S(2)),
                                  Tema.Medir(g, datos[i].V, fVal).Width + (datos[i].U.Length > 0 ? Tema.Medir(g, datos[i].U, Tema.Mono(7.5f)).Width + S(6) : 0)) + S(30);
            var anchosL = Tema.Repartir(nat, w - sep * (cols - 1));
            int xl = x;
            for (int i = 0; i < datos.Length; i++)
            {
                var r = new Rectangle(xl, yL, anchosL[i], S(54));
                xl += anchosL[i] + sep;
                using (var b = new SolidBrush(Tema.Alpha(Tema.Consola, 170))) g.FillRectangle(b, r);
                using (var p = new Pen(Tema.Alpha(Tema.Filete, 220), 1f)) g.DrawRectangle(p, r);
                using (var b = new SolidBrush(Tema.Alpha(datos[i].C, 200))) g.FillRectangle(b, r.X, r.Y, S(2), r.Height);
                Tema.Lectura(g, new Rectangle(r.X + S(11), r.Y + S(7), r.Width - S(16), r.Height - S(9)), datos[i].E, datos[i].V, datos[i].U, datos[i].C, esc, 14f);
            }

            // ---------------- dos paneles
            int mitad = (w - S(12)) / 2;
            int yP = S(168);
            // el filete del rótulo frena donde empiezan los botones: antes se los pasaba por encima
            Tema.Rotulo(g, "", "diario de vuelo", new Rectangle(x, yP, Math.Max(S(60), btLimpiar.Left - x - S(10)), S(14)), Tema.Cielo, esc);
            Tema.Rotulo(g, "", "salida del servidor", new Rectangle(x + mitad + S(12), yP, Math.Max(S(60), btCopiarLog.Left - (x + mitad + S(12)) - S(10)), S(14)), Tema.Crema, esc);

            var rServ = new Rectangle(x + mitad + S(12), yP + S(22), mitad, Height - yP - S(34));
            using (var b = new SolidBrush(Tema.Panel)) g.FillRectangle(b, rServ);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 220), 1f)) g.DrawRectangle(p, rServ);
            PintarServidor(g, rServ);

            using (var p = new Pen(Tema.Alpha(Tema.Filete, 200), 1f))
                g.DrawRectangle(p, new Rectangle(x, yP + S(22), mitad, Height - yP - S(34)));
        }

        void PintarServidor(Graphics g, Rectangle r)
        {
            if ((DateTime.Now - ultimaLectura).TotalMilliseconds > 400)
            {
                cacheServidor = N.Srv.Salida();
                ultimaLectura = DateTime.Now;
            }
            var f = Tema.Mono(7.5f);
            int fila = Math.Max(S(11), Tema.Medir(g, "Xy", f).Height + S(1));
            int visibles = Math.Max(1, (r.Height - S(12)) / fila);
            if (cacheServidor.Length == 0)
            {
                string s = N.Srv.Nuestro
                    ? "el servidor corre pero todavía no dijo nada"
                    : "no levantamos ningún servidor desde acá.\n\nSi hay uno escuchando, es de otro proceso (por ejemplo la tarea programada que repone el modelo grande): se usa igual, pero su salida no pasa por acá.";
                Tema.Texto_(g, s, Tema.Fina(8.5f), Tema.Fantasma, new Rectangle(r.X + S(12), r.Y + S(10), r.Width - S(24), r.Height - S(20)),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                return;
            }
            int fin = Math.Max(0, cacheServidor.Length - desplazServidor);
            int ini = Math.Max(0, fin - visibles);
            int y = r.Y + S(6);
            g.SetClip(r);
            for (int i = ini; i < fin; i++, y += fila)
            {
                string l = cacheServidor[i];
                var c = Color(l);
                Tema.Texto_(g, l, f, c, new Rectangle(r.X + S(9), y, r.Width - S(16), fila),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            g.ResetClip();
            if (desplazServidor > 0)
            {
                string s = "▾ " + desplazServidor;
                var fs = Tema.Media(7.5f);
                int w = Tema.Medir(g, s, fs).Width + S(14);
                var rb = new RectangleF(r.Right - w - S(10), r.Bottom - S(22), w, S(15));
                Tema.Placa(g, rb, S(2), Tema.Mezcla(Tema.Panel, Tema.Crema, 0.16f), Tema.Alpha(Tema.Crema, 130));
                Tema.Texto_(g, s, fs, Tema.Crema, Rectangle.Round(rb), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        static Color Color(string l)
        {
            if (l == null) return Tema.Suave;
            if (l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) return Tema.Rosa;
            if (l.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0) return Tema.Ambar;
            if (l.StartsWith("srv ") || l.IndexOf("listening", StringComparison.OrdinalIgnoreCase) >= 0) return Tema.Salvia;
            if (l.StartsWith("load_tensors") || l.StartsWith("llama_model_loader")) return Tema.Alpha(Tema.Cielo, 210);
            if (l.StartsWith("print_info") || l.StartsWith("llm_load")) return Tema.Apagado;
            return Tema.Alpha(Tema.Suave, 210);
        }
    }
}
