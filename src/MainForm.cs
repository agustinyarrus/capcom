using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// La ventana: sin marco del sistema, con su propia barra fina y sus propios botones.
    ///
    /// Vive en la bandeja — minimizar y cerrar la guardan ahí, y sólo se cierra de verdad desde el menú de la
    /// bandeja o con Alt+F4 sobre el ícono. Eso es deliberado: una consola que se apaga sola pierde el hilo.
    /// </summary>
    internal sealed class MainForm : Form
    {
        readonly Nucleo N;
        readonly Bandeja bandeja;
        readonly Pestanas pestanas = new Pestanas();
        readonly Pantalla[] pantallas;
        readonly VistaChat vChat;
        readonly VistaConsola vConsola;
        readonly VistaModelos vModelos;
        readonly VistaRegistro vRegistro;
        readonly VistaAjustes vAjustes;
        readonly Paleta paleta;
        readonly Galaxia insignia;
        readonly Timer latido = new Timer { Interval = 60 };
        readonly Timer lento = new Timer { Interval = 2000 };

        float esc = 1f;
        bool permitirVisible, cerrarDeVerdad, hoverMin, hoverMax, hoverCerrar;
        Rectangle rTitulo, rMin, rMax, rCerrar, rLogo;
        bool atajoPuesto;
        const int IdAtajo = 0xC0C0;
        DateTime ultimoSondeo = DateTime.MinValue;

        public MainForm(Nucleo n, bool arrancarMinimizado, bool forzarVisible = false)
        {
            N = n;
            permitirVisible = forzarVisible || !(arrancarMinimizado || N.Cfg.IniciarMinimizado);

            FormBorderStyle = FormBorderStyle.None;
            BackColor = Tema.Fondo;
            ForeColor = Tema.Texto;
            Text = "CAPCOM";
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) esc = g.DpiX / 96f; } catch { }

            var libre = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Dpi.Bruto(esc, 1480), (int)(libre.Width * 0.84)), Math.Min(Dpi.Bruto(esc, 940), (int)(libre.Height * 0.88)));
            MinimumSize = new Size(Dpi.Bruto(esc, 860), Dpi.Bruto(esc, 560));
            bool recordada = false;
            if (N.Cfg.VentanaAncho >= 600 && N.Cfg.VentanaAlto >= 400)
            {
                var r = new Rectangle(N.Cfg.VentanaX, N.Cfg.VentanaY, N.Cfg.VentanaAncho, N.Cfg.VentanaAlto);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) { Bounds = r; recordada = true; }
            }
            if (!recordada)
                Location = new Point(libre.Left + (libre.Width - Width) / 2, libre.Top + (libre.Height - Height) / 2);

            // --- pantallas
            vChat = new VistaChat(N);
            vConsola = new VistaConsola(N);
            vModelos = new VistaModelos(N);
            vRegistro = new VistaRegistro(N);
            vRegistro.Abrir += c => { pestanas.Ir(0); vChat.Abrir(c); };
            vAjustes = new VistaAjustes(N);
            vModelos.PedirSesion += () => { pestanas.Ir(4); vAjustes.Modo("huggingface"); };
            pantallas = new Pantalla[] { vChat, vConsola, vModelos, vRegistro, vAjustes };
            foreach (var p in pantallas) { p.Visible = false; Controls.Add(p); }
            vChat.Visible = true;

            pestanas.Nombres = pantallas.Select(p => p.Nombre).ToArray();
            pestanas.Insignias = new string[pantallas.Length];
            pestanas.Acento = N.Cfg.ColorAcento;
            pestanas.Cambio += (s, e) => CambiarPestana();
            Controls.Add(pestanas);

            insignia = new Galaxia();
            Controls.Add(insignia);

            paleta = new Paleta(N) { Visible = false };
            paleta.Elegida += EjecutarComando;
            Controls.Add(paleta);
            paleta.BringToFront();

            // --- bandeja
            bandeja = new Bandeja();
            bandeja.Mostrar += (s, e) => Mostrar();
            bandeja.Nueva += (s, e) => { Mostrar(); pestanas.Ir(0); vChat.Nueva(); };
            bandeja.Cortar += (s, e) => N.Tx.Cortar();
            bandeja.AlternarServidor += (s, e) => { Mostrar(); pestanas.Ir(2); };
            bandeja.AlternarSilencio += (s, e) => { N.Cfg.Sonido = !N.Cfg.Sonido; Sonidos.Silencio = !N.Cfg.Sonido; N.Cfg.Guardar(); };
            bandeja.AbrirCarpeta += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", N.CarpetaDatos); } catch { } };
            bandeja.Salir += (s, e) => CerrarDeVerdad();

            N.Aviso = (t, x) => { if (N.Cfg.Notificaciones) bandeja.Aviso(t, x); };
            N.Log.LineaNueva += l => N.EnUi(() => { vConsola.Agregar(l); });
            N.Tx.Termino += r => N.EnUi(() =>
            {
                vChat.RegistrarResultado(r);
                Actualizar();
                if (esperandoRespuesta)
                {
                    esperandoRespuesta = false;
                    var ult = N.Actual != null ? N.Actual.Mensajes.LastOrDefault(x => x.Rol == Rol.Asistente) : null;
                    Responder(ult != null ? ult.Texto : (r != null && r.Error.Length > 0 ? "ERROR|" + r.Error : "ERROR|sin respuesta"));
                }
            });
            N.Tx.Empezo += () => N.EnUi(Actualizar);
            N.Cambio += () => N.EnUi(Actualizar);

            latido.Tick += (s, e) => Latido();
            lento.Tick += (s, e) => Lento();

            CreateHandle();
            vConsola.Refrescar();
            vModelos.Refrescar();
            vChat.Refrescar();
            if (N.Actual != null) vChat.Abrir(N.Actual);     // 🚨 sin esto la transcripción arranca vacía
            Sonidos.Silencio = !N.Cfg.Sonido;
            latido.Start();
            lento.Start();
        }

        int S(int px) => Dpi.S(esc, px);

        // ------------------------------------------------------------------ ciclo de vida

        protected override void SetVisibleCore(bool value)
        {
            // 🚨 CreateHandle acá adentro: así el handle existe aunque la ventana nunca se haya mostrado, que es
            //    lo que permite que `--foto` funcione con la app guardada en la bandeja.
            if (!permitirVisible) { value = false; if (!IsHandleCreated) CreateHandle(); }
            base.SetVisibleCore(value);
        }

        IntPtr hIconGrande, hIconChico;

        /// <summary>
        /// WinForms manda su ícono (el de SM_CXICON: 48 al 150 %) DENTRO de base.CreateHandle, después de
        /// OnHandleCreated; por eso los tamaños exactos se ponen acá, a la vuelta. Quien le pida el ícono a la ventana
        /// (Alt+Tab, las miniaturas, los conmutadores de ventanas) recibe el frame del tamaño que va a dibujar.
        ///
        /// 🚨 El botón de la barra de tareas de Windows 11 NO sale de acá: para una app sin AppUserModelID propio usa
        /// el ícono del exe (el de 48 reducido a 36), aunque el ICON_BIG de la ventana sea de 36 exacto. Medido el
        /// 21-sep-2026 capturando la barra: el mejor calce es el frame de 48 con bicúbico. Con un AppID explícito sí
        /// usa el de la ventana (0,03 de diferencia contra el frame de 36).
        /// </summary>
        protected override void CreateHandle()
        {
            base.CreateHandle();
            IconosExactos();
        }

        void IconosExactos()
        {
            if (!IsHandleCreated) return;
            uint dpi = 96;
            try { dpi = Win32.GetDpiForWindow(Handle); } catch { }
            if (dpi == 0) dpi = 96;
            int grande = (int)Math.Round(24 * dpi / 96.0);                       // 24 px lógicos: 36 al 150 %
            int chico;
            try { chico = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi); }  // título y Alt+Tab: 24 al 150 %
            catch { chico = (int)Math.Round(16 * dpi / 96.0); }
            IntPtr g = IntPtr.Zero, c = IntPtr.Zero;
            try { g = Insignia.HIcon(grande); c = Insignia.HIcon(chico); } catch { }
            if (g == IntPtr.Zero || c == IntPtr.Zero)
            {
                if (g != IntPtr.Zero) Win32.DestroyIcon(g);
                if (c != IntPtr.Zero) Win32.DestroyIcon(c);
                return;     // quedan los que puso WinForms
            }
            Win32.SendMessage(Handle, Win32.WM_SETICON, (IntPtr)Win32.ICON_BIG, g);
            Win32.SendMessage(Handle, Win32.WM_SETICON, (IntPtr)Win32.ICON_SMALL, c);
            if (hIconGrande != IntPtr.Zero) Win32.DestroyIcon(hIconGrande);
            if (hIconChico != IntPtr.Zero) Win32.DestroyIcon(hIconChico);
            hIconGrande = g;
            hIconChico = c;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            base.OnHandleDestroyed(e);
            if (hIconGrande != IntPtr.Zero) { Win32.DestroyIcon(hIconGrande); hIconGrande = IntPtr.Zero; }
            if (hIconChico != IntPtr.Zero) { Win32.DestroyIcon(hIconChico); hIconChico = IntPtr.Zero; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.EsquinasRedondas(Handle);
            Win32.ModoOscuro(Handle);
            Win32.BordeColor(Handle, Tema.Bgr(Tema.Filete));
            if (N.Cfg.AtajoGlobal)
            {
                atajoPuesto = Win32.RegisterHotKey(Handle, IdAtajo, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)Keys.Space);
                N.Log.Info(atajoPuesto
                    ? "Atajo global Ctrl+Alt+Espacio: trae la consola desde cualquier aplicación"
                    : "No pude registrar Ctrl+Alt+Espacio (ya lo usa otra app)");
            }
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Acomodar(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Acomodar(); Invalidate(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!cerrarDeVerdad && e.CloseReason == CloseReason.UserClosing && N.Cfg.CerrarABandeja)
            {
                e.Cancel = true;
                GuardarVentana();
                Hide();
                return;
            }
            base.OnFormClosing(e);
            GuardarVentana();
            if (atajoPuesto) { try { Win32.UnregisterHotKey(Handle, IdAtajo); } catch { } }
            latido.Stop(); lento.Stop();
            N.Tx.Cortar();
            if (N.Actual != null && N.Cfg.GuardarHistorial && N.Actual.Mensajes.Count > 0) N.Arch.Guardar(N.Actual);
            if (N.Srv.Nuestro) N.Srv.Bajar();
            if (N.Banco != null) N.Banco.Apagar();      // el servidor del banco es otro proceso: también se baja
            if (N.Bajadas != null) N.Bajadas.Cortar();
            bandeja.Dispose();
        }

        void CerrarDeVerdad()
        {
            cerrarDeVerdad = true;
            N.Log.Info("Cerrando CAPCOM");
            Close();
        }

        public void Mostrar()
        {
            permitirVisible = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            Win32.TraerAlFrente(Handle);
            vChat.Foco();
            Invalidate();
        }

        void GuardarVentana()
        {
            try
            {
                if (WindowState != FormWindowState.Normal || Width < 600 || Height < 400) return;
                if (N.Cfg.VentanaX == Left && N.Cfg.VentanaY == Top && N.Cfg.VentanaAncho == Width && N.Cfg.VentanaAlto == Height) return;
                N.Cfg.VentanaX = Left; N.Cfg.VentanaY = Top; N.Cfg.VentanaAncho = Width; N.Cfg.VentanaAlto = Height;
                N.Cfg.Guardar();
            }
            catch { }
        }

        // ------------------------------------------------------------------ latidos

        void Latido()
        {
            vChat.Latido();
            if (N.Tx.Ocupado) Invalidate(rTitulo);
        }

        void Lento()
        {
            if ((DateTime.Now - ultimoSondeo).TotalSeconds >= (N.Tx.Ocupado ? 30 : 6))
            {
                ultimoSondeo = DateTime.Now;
                var antes = N.Cli.Estado.Señal;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    var e = N.Cli.Sondear(1500);
                    if (e.Señal != antes) N.EnUi(Actualizar);
                });
            }
            Actualizar();
            var p = Activa();
            if (p == vConsola || p == vModelos || p == vRegistro) p.Invalidate();
        }

        void Actualizar()
        {
            insignia.Activa = N.Tx.Ocupado;
            pestanas.Acento = N.Cfg.ColorAcento;
            var est = N.Cli.Estado;
            pestanas.Insignias = new[]
            {
                N.Actual != null && N.Actual.Mensajes.Count > 0 ? N.Actual.Mensajes.Count.ToString() : "",
                "",
                est.Señal == Señal.Nominal ? "EN LÍNEA" : "",
                N.Arch.Lista.Count > 0 ? N.Arch.Lista.Count.ToString() : "",
                "",
            };
            pestanas.Invalidate();
            bandeja.Actualizar(est.Señal, N.Tx.Ocupado, est.ModeloCorto,
                N.Actual != null ? N.Actual.NombreVisible : "", !N.Cfg.Sonido);
            Invalidate(rTitulo);
        }

        Pantalla Activa() => pantallas[Math.Max(0, Math.Min(pantallas.Length - 1, pestanas.Activa))];

        void CambiarPestana()
        {
            for (int i = 0; i < pantallas.Length; i++) pantallas[i].Visible = i == pestanas.Activa;
            var p = Activa();
            p.Refrescar();
            p.Acomodar();
            p.BringToFront();
            paleta.BringToFront();
            Acomodar();
            Invalidate();
            if (p == vChat) vChat.Foco();
        }

        // ------------------------------------------------------------------ teclado

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (paleta.Visible)
            {
                if (keyData == Keys.Escape) { paleta.Cerrar(); return true; }
            }
            switch (keyData)
            {
                case Keys.Control | Keys.K: AbrirPaleta(); return true;
                case Keys.Control | Keys.N: pestanas.Ir(0); vChat.Nueva(); return true;
                case Keys.Control | Keys.E: vChat.Exportar(); return true;
                case Keys.Control | Keys.L: pestanas.Ir(1); return true;
                case Keys.Control | Keys.F: pestanas.Ir(3); vRegistro.Foco(); return true;
                case Keys.Control | Keys.D1: pestanas.Ir(0); return true;
                case Keys.Control | Keys.D2: pestanas.Ir(1); return true;
                case Keys.Control | Keys.D3: pestanas.Ir(2); return true;
                case Keys.Control | Keys.D4: pestanas.Ir(3); return true;
                case Keys.Control | Keys.D5: pestanas.Ir(4); return true;
                case Keys.F5: Activa().Refrescar(); return true;
                case Keys.Control | Keys.Tab: pestanas.Ir((pestanas.Activa + 1) % pantallas.Length); return true;
                case Keys.PageUp: if (Activa() == vChat) { vChat.Desplazar(-1); return true; } break;
                case Keys.PageDown: if (Activa() == vChat) { vChat.Desplazar(1); return true; } break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !paleta.Visible)
            {
                if (N.Tx.Ocupado) { N.Tx.Cortar(); e.Handled = true; return; }
            }
            base.OnKeyDown(e);
        }

        void AbrirPaleta()
        {
            paleta.Abrir(Comandos());
            paleta.BringToFront();
            Acomodar();
        }

        List<Comando> Comandos()
        {
            var l = new List<Comando>
            {
                new Comando("Transmisión nueva", "empieza un hilo limpio", "Ctrl+N", () => { pestanas.Ir(0); vChat.Nueva(); }),
                new Comando("Exportar esta transmisión", "la guarda como .md", "Ctrl+E", () => vChat.Exportar()),
                new Comando("Cortar la generación", "frena al modelo donde está", "Esc", () => N.Tx.Cortar()),
                new Comando("Rehacer la última respuesta", "vuelve a pedirla desde el mismo punto", "", () => { if (N.Tx.Regenerar(N.Actual)) vChat.Abrir(N.Actual); }),
                new Comando("Ir a misión", "la transcripción", "Ctrl+1", () => pestanas.Ir(0)),
                new Comando("Ir a consola", "diario de vuelo y salida del servidor", "Ctrl+2", () => pestanas.Ir(1)),
                new Comando("Ir a modelos", "inventario y arranque del servidor", "Ctrl+3", () => pestanas.Ir(2)),
                new Comando("Ir a registro", "buscar en todo el archivo", "Ctrl+4", () => pestanas.Ir(3)),
                new Comando("Ir a ajustes", "muestreo, personas y aspecto", "Ctrl+5", () => pestanas.Ir(4)),
                new Comando("Buscar modelos en HuggingFace", "cualquier repo con .gguf, también los que piden iniciar sesión", "", () => { pestanas.Ir(2); vModelos.Modo("descargar"); vModelos.FocoBusqueda(); }),
                new Comando("Sesión de HuggingFace", "el token para bajar los modelos restringidos o privados", "", () => { pestanas.Ir(4); vAjustes.Modo("huggingface"); }),
                new Comando("Plegar el riel izquierdo", "más lugar para la transcripción", "", () => { N.Cfg.RielIzquierdo = !N.Cfg.RielIzquierdo; N.Cfg.Guardar(); vChat.Acomodar(); vChat.Invalidate(); }),
                new Comando("Plegar el riel derecho", "esconde la telemetría", "", () => { N.Cfg.RielDerecho = !N.Cfg.RielDerecho; N.Cfg.Guardar(); vChat.Acomodar(); vChat.Invalidate(); }),
                new Comando("Retícula de fondo", "prende o apaga el papel milimetrado", "", () => { N.Cfg.Reticula = !N.Cfg.Reticula; N.Cfg.Guardar(); Invalidate(true); foreach (var p in pantallas) p.Invalidate(); }),
                new Comando("Tonos quindar", "los bips de radio de Apollo", "", () => { N.Cfg.Quindar = !N.Cfg.Quindar; N.Cfg.Guardar(); if (N.Cfg.Quindar) Sonidos.Abrir(); }),
                new Comando("Abrir la carpeta de datos", N.CarpetaDatos, "", () => { try { System.Diagnostics.Process.Start("explorer.exe", N.CarpetaDatos); } catch { } }),
                new Comando("Guardar en la bandeja", "sigue vivo y contesta el atajo global", "", () => Hide()),
                new Comando("Salir de CAPCOM", "cierra de verdad", "", () => CerrarDeVerdad()),
            };
            foreach (var p in N.Personas)
            {
                var pp = p;
                l.Add(new Comando("Persona: " + p.Nombre, p.Nota, "", () => { pestanas.Ir(0); vChat.PonerPersona(pp); }));
            }
            foreach (var c in N.Arch.Lista.Take(12))
            {
                var cc = c;
                l.Add(new Comando("Abrir: " + c.NombreVisible, Tema.Relativo(c.Tocada) + " · " + c.Mensajes.Count + " mensajes", "", () => { pestanas.Ir(0); vChat.Abrir(cc); }));
            }
            return l;
        }

        void EjecutarComando(Comando c)
        {
            paleta.Cerrar();
            try { c.Hacer(); } catch (Exception ex) { N.Log.Error("El comando falló: " + ex.Message); }
            Invalidate();
        }

        // ------------------------------------------------------------------ maquetación y pintura

        void Acomodar()
        {
            if (Width < 10 || Height < 10) return;
            esc = Dpi.Escala(this);
            rTitulo = new Rectangle(0, 0, Width, S(38));
            int b = S(40);
            rCerrar = new Rectangle(Width - b, 0, b, S(38));
            rMax = new Rectangle(Width - b * 2, 0, b, S(38));
            rMin = new Rectangle(Width - b * 3, 0, b, S(38));
            // la insignia va al tamaño de frame más parecido al hueco (29 px al 150 % → el de 30): un frame exacto se
            // dibuja píxel por píxel; reescalarlo por 1 px lo emborrona
            int hueco = S(24), lado = Insignia.MasCercano(hueco);
            if (lado > S(34) || lado < S(16)) lado = hueco;
            rLogo = new Rectangle(S(14) + (hueco - lado) / 2, (S(38) - lado) / 2, lado, lado);
            insignia.SetBounds(rLogo.X, rLogo.Y, rLogo.Width, rLogo.Height);
            insignia.BackColor = Tema.Panel;

            pestanas.SetBounds(S(8), S(38), Width - S(16), S(30));
            int y = S(70);
            foreach (var p in pantallas) { p.SetBounds(0, y, Width, Math.Max(S(80), Height - y)); p.Acomodar(); }
            if (paleta.Visible) paleta.SetBounds(0, 0, Width, Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Tema.Fondo);

            // --- barra de título propia
            using (var b = new SolidBrush(Tema.Panel)) g.FillRectangle(b, rTitulo);
            using (var p = new Pen(Tema.Filete)) g.DrawLine(p, 0, rTitulo.Bottom - 1, Width, rTitulo.Bottom - 1);

            int x = rLogo.Right + S(11);
            var fN = Tema.Media(11f);
            int wn = Tema.Tracking(g, "CAPCOM", fN, Tema.Texto, x, S(6), S(16), S(4));
            x += wn + S(12);
            Tema.Texto_(g, "capsule communicator", Tema.Fina(8.5f), Tema.Apagado, new Rectangle(x, S(5), S(200), S(18)));

            // subtítulo vivo: qué está haciendo ahora mismo
            string abajo = N.Tx.Ocupado
                ? "TRANSMITIENDO · " + N.Tx.Fase
                : N.Actual != null && N.Actual.Mensajes.Count > 0 ? N.Actual.NombreVisible
                : "sin transmisión abierta";
            Tema.Texto_(g, abajo, Tema.Fina(8f), N.Tx.Ocupado ? Tema.Alpha(N.ColorPersona(N.PersonaActual), 235) : Tema.Fantasma,
                new Rectangle(rLogo.Right + S(11), S(20), Math.Max(S(60), Width - rLogo.Right - S(430)), S(14)),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);

            // --- reloj de misión + estado, a la izquierda de los botones
            var est = N.Cli.Estado;
            bool vivo = est.Señal == Señal.Nominal;
            var cSeñal = vivo ? Tema.Teal : est.Señal == Señal.Cargando ? Tema.Ambar : Tema.Apagado;
            int xr = rMin.Left - S(16);
            var fReloj = Tema.MonoMedia(9.5f);
            string t = Tema.TMas(DateTime.Now - N.Arranque);
            int wt = Tema.Medir(g, t, fReloj).Width;
            Tema.Texto_(g, t, fReloj, Tema.Alpha(Tema.Texto, 210), new Rectangle(xr - wt, S(4), wt + S(3), S(15)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var fE = Tema.Media(7f);
            string se = vivo ? "NOMINAL" : est.Señal == Señal.Cargando ? "CARGANDO" : "SIN SEÑAL";
            int we = Tema.MedirTracking(g, se, fE, S(2));
            Tema.Tracking(g, se, fE, cSeñal, xr - we, S(21), S(12), S(2));
            Tema.Diodo(g, xr - we - S(9), S(27), S(4), cSeñal, vivo, vivo ? 1f : 0.35f);

            // modelo cargado, si entra
            if (est.ModeloCorto.Length > 0)
            {
                var fm = Tema.Mono(7.5f);
                int wm = Tema.Medir(g, est.ModeloCorto, fm).Width;
                int xm = xr - Math.Max(wt, we) - S(22) - wm;
                if (xm > Width / 2)
                    Tema.Texto_(g, est.ModeloCorto, fm, Tema.Alpha(Tema.Teal, 190), new Rectangle(xm, S(12), wm + S(4), S(14)));
            }

            // --- botones de ventana, dibujados a mano
            if (hoverMin) using (var b = new SolidBrush(Tema.Alza)) g.FillRectangle(b, rMin);
            if (hoverMax) using (var b = new SolidBrush(Tema.Alza)) g.FillRectangle(b, rMax);
            if (hoverCerrar) using (var b = new SolidBrush(Tema.Alpha(Tema.Rosa, 45))) g.FillRectangle(b, rCerrar);
            using (var p = new Pen(hoverMin ? Tema.Texto : Tema.Suave, 1f))
                g.DrawLine(p, rMin.Left + rMin.Width / 2 - S(5), rMin.Top + rMin.Height / 2, rMin.Left + rMin.Width / 2 + S(5), rMin.Top + rMin.Height / 2);
            using (var p = new Pen(hoverMax ? Tema.Texto : Tema.Suave, 1f))
            {
                int cx = rMax.Left + rMax.Width / 2, cy = rMax.Top + rMax.Height / 2, d = S(5);
                if (WindowState == FormWindowState.Maximized)
                {
                    g.DrawRectangle(p, cx - d, cy - d + S(2), d * 2 - S(2), d * 2 - S(2));
                    g.DrawLine(p, cx - d + S(2), cy - d, cx + d, cy - d);
                    g.DrawLine(p, cx + d, cy - d, cx + d, cy + d - S(2));
                }
                else g.DrawRectangle(p, cx - d, cy - d, d * 2, d * 2);
            }
            using (var p = new Pen(hoverCerrar ? Tema.Rosa : Tema.Suave, 1f))
            {
                int cx = rCerrar.Left + rCerrar.Width / 2, cy = rCerrar.Top + rCerrar.Height / 2, d = S(5);
                g.DrawLine(p, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(p, cx + d, cy - d, cx - d, cy + d);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool m = rMin.Contains(e.Location), x = rMax.Contains(e.Location), c = rCerrar.Contains(e.Location);
            if (m != hoverMin || x != hoverMax || c != hoverCerrar) { hoverMin = m; hoverMax = x; hoverCerrar = c; Invalidate(rTitulo); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverMin || hoverMax || hoverCerrar) { hoverMin = hoverMax = hoverCerrar = false; Invalidate(rTitulo); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (rMin.Contains(e.Location)) { if (N.Cfg.MinimizarABandeja) Hide(); else WindowState = FormWindowState.Minimized; return; }
                if (rMax.Contains(e.Location)) { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; return; }
                if (rCerrar.Contains(e.Location)) { if (N.Cfg.CerrarABandeja) Hide(); else CerrarDeVerdad(); return; }
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (rTitulo.Contains(e.Location) && !rMin.Contains(e.Location) && !rMax.Contains(e.Location) && !rCerrar.Contains(e.Location))
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            base.OnMouseDoubleClick(e);
        }

        // ------------------------------------------------------------------ mensajes de ventana

        static Control BajoElMouse(Control raiz, Point pantalla)
        {
            Control actual = raiz;
            for (int i = 0; i < 12; i++)
            {
                Control hijo = null;
                try { hijo = actual.GetChildAtPoint(actual.PointToClient(pantalla)); } catch { }
                if (hijo == null) return actual;
                actual = hijo;
            }
            return actual;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Programa.MsgMostrar) { Mostrar(); return; }
            if (m.Msg == Programa.MsgFoto) { TomarFoto(); return; }
            if (m.Msg == Programa.MsgOrden) { AtenderOrden(); return; }
            if (m.Msg == Win32.WM_MOUSEWHEEL)
            {
                // la rueda va al control que está BAJO el mouse, sin que nadie robe el foco del teclado
                long lp = m.LParam.ToInt64();
                var pantalla = new Point((short)(lp & 0xffff), (short)((lp >> 16) & 0xffff));
                var c = BajoElMouse(this, pantalla);
                while (c != null && !(c is IRueda)) c = c.Parent;
                if (c is IRueda r) { r.Rueda((short)((m.WParam.ToInt64() >> 16) & 0xffff)); return; }
            }
            if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == IdAtajo)
            {
                if (Visible && Win32.GetForegroundWindow() == Handle) Hide();
                else Mostrar();
                return;
            }
            if (m.Msg == Win32.WM_NCLBUTTONDBLCLK) return;
            if (m.Msg == Win32.WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xffff), (short)((lp >> 16) & 0xffff)));
                int b = S(6);
                bool izq = p.X < b, der = p.X >= Width - b, arr = p.Y < b, aba = p.Y >= Height - b;
                int ht = Win32.HTCLIENT;
                if (WindowState == FormWindowState.Normal)
                {
                    if (arr && izq) ht = Win32.HTTOPLEFT; else if (arr && der) ht = Win32.HTTOPRIGHT;
                    else if (aba && izq) ht = Win32.HTBOTTOMLEFT; else if (aba && der) ht = Win32.HTBOTTOMRIGHT;
                    else if (izq) ht = Win32.HTLEFT; else if (der) ht = Win32.HTRIGHT; else if (arr) ht = Win32.HTTOP; else if (aba) ht = Win32.HTBOTTOM;
                }
                if (ht == Win32.HTCLIENT && rTitulo.Contains(p) && !rMin.Contains(p) && !rMax.Contains(p) && !rCerrar.Contains(p))
                    ht = Win32.HTCAPTION;
                m.Result = (IntPtr)ht;
                return;
            }
            if (m.Msg == 0x02E0) // WM_DPICHANGED
            {
                esc = (int)(m.WParam.ToInt64() & 0xffff) / 96f;
                try
                {
                    var r = (Win32.RECT)Marshal.PtrToStructure(m.LParam, typeof(Win32.RECT));
                    Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                }
                catch { }
                MinimumSize = new Size(Dpi.Bruto(esc, 860), Dpi.Bruto(esc, 560));
                Acomodar();
                Invalidate(true);
                IconosExactos();      // otro monitor, otra escala: otro tamaño exacto
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------ órdenes desde la terminal

        bool esperandoRespuesta;

        /// <summary>
        /// Atiende una orden que dejó otra instancia en `orden-pedido.txt`. Misma vía que `--foto`: un mensaje de
        /// ventana registrado y un archivito con los parámetros. Sirve para dos cosas distintas y las dos valen:
        /// usar la consola desde la terminal (`capcom --decir "che, ¿qué onda?"`) y poder verificar la app de
        /// punta a punta sin pelearle el mouse al usuario.
        /// </summary>
        void AtenderOrden()
        {
            string pedido = Path.Combine(N.CarpetaDatos, "orden-pedido.txt");
            string verbo, arg;
            try
            {
                if (!File.Exists(pedido)) return;
                var t = File.ReadAllText(pedido).Split(new[] { '|' }, 2);
                verbo = t[0].Trim().ToLowerInvariant();
                arg = t.Length > 1 ? t[1] : "";
                File.Delete(pedido);
            }
            catch { return; }

            switch (verbo)
            {
                case "decir":
                    if (N.Tx.Ocupado) { Responder("ERROR|hay una transmisión en curso"); return; }
                    pestanas.Ir(0);
                    esperandoRespuesta = true;
                    vChat.Mandar(arg);
                    break;
                case "levantar":
                    {
                        pestanas.Ir(2);
                        string porque;
                        bool arranco = vModelos.LevantarPorNombre(arg, (ok, det) => Responder(ok ? det : "ERROR|" + det), out porque);
                        if (!arranco) Responder("ERROR|" + porque);
                        break;
                    }
                case "traer":
                    {
                        pestanas.Ir(2);
                        string porque3;
                        bool ok3 = vModelos.TraerPorNombre(arg, out porque3);
                        Responder(ok3 ? "bajando " + porque3 : "ERROR|" + porque3);
                        break;
                    }
                case "examinar":
                    {
                        // le pide al banco de la app (no a una instancia aparte) que examine esos modelos:
                        // es la única forma de verificar la pantalla de examen con datos reales moviéndose
                        pestanas.Ir(2);
                        string porque2;
                        bool arranco2 = vModelos.ExaminarPorNombre(arg, out porque2);
                        Responder(arranco2 ? "examinando " + arg : "ERROR|" + porque2);
                        break;
                    }
                case "bajar":
                    {
                        string porque;
                        bool bajo = vModelos.BajarAhora(out porque);
                        Responder(bajo ? "servidor bajado" : "ERROR|" + porque);
                        break;
                    }
                case "nueva":
                    pestanas.Ir(0);
                    vChat.Nueva();
                    Responder("transmisión nueva · persona " + N.PersonaActual.Nombre);
                    break;
                case "persona":
                    {
                        string q = Sinacentos(arg.Trim().ToLowerInvariant());
                        var per = N.Personas.FirstOrDefault(x => Sinacentos(x.Clave.ToLowerInvariant()) == q)
                               ?? N.Personas.FirstOrDefault(x => Sinacentos(x.Nombre.ToLowerInvariant()).StartsWith(q));
                        if (per == null) { Responder("ERROR|no conozco la persona «" + arg + "»; están: " + string.Join(", ", N.Personas.Select(x => x.Clave))); return; }
                        pestanas.Ir(0);
                        vChat.PonerPersona(per);
                        Responder("persona " + per.Nombre);
                        break;
                    }
                case "salir":
                    // cierra DE VERDAD y por las buenas: guarda, baja su servidor y saca el ícono de la bandeja. Es lo
                    // que usa build.ps1 para reemplazar el exe: matar el proceso deja el ícono fantasma en la bandeja
                    // y el llama-server huérfano.
                    Responder("cerrando");
                    BeginInvoke((Action)CerrarDeVerdad);
                    break;
                case "ir":
                    {
                        var nombres = pantallas.Select(x => Sinacentos(x.Nombre)).ToArray();
                        int i = Array.FindIndex(nombres, x => x.StartsWith(Sinacentos(arg.Trim().ToLowerInvariant()), StringComparison.OrdinalIgnoreCase));
                        if (i < 0) { Responder("ERROR|no conozco la pantalla «" + arg + "»"); return; }
                        Mostrar();
                        pestanas.Ir(i);
                        Responder("en " + pantallas[i].Nombre);
                        break;
                    }
                default:
                    Responder("ERROR|no entiendo la orden «" + verbo + "»");
                    break;
            }
        }

        static string Sinacentos(string s) => (s ?? "").Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u');

        void Responder(string texto)
        {
            try
            {
                string tmp = Path.Combine(N.CarpetaDatos, "orden-respuesta.tmp");
                string fin = Path.Combine(N.CarpetaDatos, "orden-respuesta.txt");
                File.WriteAllText(tmp, texto ?? "", new System.Text.UTF8Encoding(false));
                if (File.Exists(fin)) File.Delete(fin);
                File.Move(tmp, fin);     // aparece entero o no aparece: el que espera nunca lee un archivo a medias
            }
            catch { }
        }

        // ------------------------------------------------------------------ la app se retrata sola

        /// <summary>
        /// `--foto`: la instancia que ya corre se saca una foto de la pestaña que le pidan. Si está guardada en
        /// la bandeja, se muestra LEJOS del escritorio (20000,20000) y sin robar el foco, así el usuario no ve
        /// nada parpadear. Sale del estado REAL, con los datos de verdad adentro.
        /// </summary>
        void TomarFoto()
        {
            string pedido = Path.Combine(N.CarpetaDatos, "foto-pedido.txt");
            string[] p;
            try { if (!File.Exists(pedido)) return; p = File.ReadAllText(pedido).Split('|'); }
            catch { return; }
            if (p.Length < 2) return;

            int tab; int.TryParse(p[0].Trim(), out tab);
            string salida = p[1].Trim();
            int w = 0, h = 0, espera = 0;
            if (p.Length > 3) { int.TryParse(p[2].Trim(), out w); int.TryParse(p[3].Trim(), out h); }
            string modo = p.Length > 4 ? p[4].Trim() : "";
            if (p.Length > 5) int.TryParse(p[5].Trim(), out espera);

            bool eraVisible = Visible;
            var posAntes = Location; var tamAntes = Size; int tabAntes = pestanas.Activa;
            try
            {
                if (!eraVisible)
                {
                    permitirVisible = true;
                    StartPosition = FormStartPosition.Manual;
                    Location = new Point(20000, 20000);
                    if (w > 400 && h > 300) Size = new Size(w, h);
                    Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
                }
                else if (w > 400 && h > 300) Size = new Size(w, h);

                pestanas.Activa = Math.Max(0, Math.Min(pantallas.Length - 1, tab));
                CambiarPestana();
                Acomodar();
                Invalidate(true);
                if (modo.Length > 0) Activa().Modo(modo);
                Application.DoEvents();
                // bombear mensajes: lo que carga de fondo llega TARDE y la foto saldría vacía
                var hasta = DateTime.Now.AddMilliseconds(Math.Max(0, Math.Min(30000, espera)));
                while (DateTime.Now < hasta) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                if (espera > 0) { Activa().Refrescar(); Acomodar(); Invalidate(true); Application.DoEvents(); }

                using (var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    string dir = Path.GetDirectoryName(salida);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    bmp.Save(salida, System.Drawing.Imaging.ImageFormat.Png);
                }
                N.Log.Info($"--foto: «{pestanas.Nombres[pestanas.Activa]}» ({Width}x{Height}) → {salida}");
            }
            catch (Exception ex) { try { N.Log.Error("--foto falló: " + ex.Message); } catch { } }
            finally
            {
                try
                {
                    pestanas.Activa = tabAntes;
                    CambiarPestana();
                    if (!eraVisible)
                    {
                        Win32.ShowWindow(Handle, Win32.SW_HIDE);
                        permitirVisible = false;
                    }
                    Size = tamAntes; Location = posAntes;
                    Acomodar();
                }
                catch { }
                try { File.Delete(pedido); } catch { }
            }
        }
    }
}
