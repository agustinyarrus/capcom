using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// AJUSTES: dos columnas. A la izquierda lo que cambia CÓMO contesta el modelo (muestreo y enlace); a la
    /// derecha lo que cambia cómo se ve y cómo se porta la app, y el editor de personas.
    ///
    /// Todo se guarda al soltar el control, sin botón de "aplicar": un panel de control no tiene un botón de
    /// confirmar, tiene perillas.
    /// </summary>
    internal sealed class VistaAjustes : Pantalla
    {
        public override string Nombre => "ajustes";

        readonly Deslizador dTemp, dTopP, dTopK, dMax, dPres, dRep, dUI, dFuente, dAncho;
        readonly Interruptor iPensar, iBandejaMin, iBandejaCerrar, iInicioMin, iWindows, iAtajo, iSonido, iQuindar, iEnter, iHistorial, iReticula, iNotif;
        readonly Campo cPuerto, cHilos, cContexto, cServer, cFlags, cCarpetas, cToken;
        readonly Timer esperaToken = new Timer { Interval = 900 };
        readonly List<Chip> acentos = new List<Chip>();
        readonly List<Chip> chipsPersona = new List<Chip>();
        readonly CajaTexto cajaSistema = new CajaTexto();
        readonly Boton btGuardarPersona, btNuevaPersona, btProbar, btRestaurar;
        Persona editando;
        bool cargando;

        public VistaAjustes(Nucleo n) : base(n)
        {
            // ---------------- muestreo
            dTemp = Perilla("temperatura", 0, 2, 0.05, "0.00", Tema.Ambar, v => { N.Cfg.Temperatura = v; });
            dTopP = Perilla("top-p", 0, 1, 0.01, "0.00", Tema.Ambar, v => { N.Cfg.TopP = v; });
            dTopK = Perilla("top-k", 0, 100, 1, "0", Tema.Ambar, v => { N.Cfg.TopK = (int)v; });
            dMax = Perilla("tokens máximos", 32, 4096, 32, "0", Tema.Malva, v => { N.Cfg.MaxTokens = (int)v; });
            dPres = Perilla("penalización de presencia", 0, 2, 0.05, "0.00", Tema.Cielo, v => { N.Cfg.Presencia = v; });
            dRep = Perilla("penalización de repetición", 0.8, 1.5, 0.01, "0.00", Tema.Cielo, v => { N.Cfg.Repeticion = v; });

            iPensar = Palanca("dejar que piense", "los Qwen3 razonan antes de contestar: más lento, más preciso, y el razonamiento no se manda",
                () => N.Cfg.Pensar, v => N.Cfg.Pensar = v);

            // ---------------- enlace
            cPuerto = CampoNum("puerto", () => N.Cfg.Puerto.ToString(), s => { int v; if (int.TryParse(s, out v) && v > 0 && v < 65536) { N.Cfg.Puerto = v; N.Cli.Url = "http://127.0.0.1:" + v; N.Cfg.ServidorUrl = N.Cli.Url; } });
            cHilos = CampoNum("hilos", () => N.Cfg.Hilos.ToString(), s => { int v; if (int.TryParse(s, out v) && v > 0 && v <= 64) N.Cfg.Hilos = v; });
            cContexto = CampoNum("contexto", () => N.Cfg.Contexto.ToString(), s => { int v; if (int.TryParse(s, out v) && v >= 512) N.Cfg.Contexto = v; });
            cServer = CampoNum("ruta de llama-server", () => N.Cfg.LlamaServer, s => N.Cfg.LlamaServer = s);
            cFlags = CampoNum("banderas extra", () => N.Cfg.FlagsExtra, s => N.Cfg.FlagsExtra = s);
            cCarpetas = CampoNum("carpetas de modelos (separadas por ;)", () => string.Join(";", N.Cfg.CarpetasModelos),
                s => N.Cfg.CarpetasModelos = s.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList());

            // ---------------- huggingface: el token para los repos que piden iniciar sesión
            // se verifica solo, un rato después de la última tecla: pegarlo alcanza para saber si sirve
            cToken = CampoNum("token de huggingface (para los modelos que piden iniciar sesión)", () => N.Cfg.HfToken,
                s => { N.Cfg.HfToken = Descargas.LimpiarToken(s); esperaToken.Stop(); esperaToken.Start(); });
            cToken.Caja.UseSystemPasswordChar = true;
            cToken.Pista = "hf_…  ·  se saca en huggingface.co/settings/tokens (de lectura alcanza)";
            esperaToken.Tick += (s, e) => { esperaToken.Stop(); N.Bajadas.VerificarSesion(); };
            N.Bajadas.SesionCambio += () => N.EnUi(Invalidate);

            // ---------------- aspecto
            dUI = Perilla("densidad de la interfaz", 0.6, 1.2, 0.05, "0.00", Tema.Malva, v => { N.Cfg.EscalaUI = v; });
            dFuente = Perilla("tamaño de la letra", 0.7, 1.4, 0.05, "0.00", Tema.Malva, v => { N.Cfg.EscalaFuente = v; });
            dUI.Nota = "hace falta reiniciar para que entre en todos lados";
            dFuente.Nota = "idem: la métrica se calcula al arrancar";
            dAncho = Perilla("ancho del chat", 40, 100, 5, "0", Tema.Teal, v =>
            {
                N.Cfg.AnchoChat = (int)v;
                var f = FindForm();
                if (f != null) f.Invalidate(true);
            });
            dAncho.Nota = "porcentaje de la columna del medio que ocupa la transcripción";

            foreach (var a in new[] { "malva", "teal", "ambar", "cielo", "salvia", "crema", "rosa" })
            {
                var clave = a;
                var ch = new Chip { Tipo = Chip.Modo.Radio, Text = a, Acento = ColorDe(a), Superficie = Tema.Fondo };
                ch.Accion += (s, e) => { N.Cfg.Acento = clave; Guardar(); SincronizarAcentos(); N.Avisar(); FindForm()?.Invalidate(true); };
                acentos.Add(ch);
                Controls.Add(ch);
            }

            iReticula = Palanca("retícula de fondo", "el papel milimetrado que hace que parezca una consola", () => N.Cfg.Reticula, v => N.Cfg.Reticula = v);

            // ---------------- comportamiento
            iBandejaMin = Palanca("minimizar a la bandeja", "el botón de minimizar la guarda al lado del reloj", () => N.Cfg.MinimizarABandeja, v => N.Cfg.MinimizarABandeja = v);
            iBandejaCerrar = Palanca("cerrar a la bandeja", "la X no cierra: guarda. Para salir de verdad, el menú de la bandeja", () => N.Cfg.CerrarABandeja, v => N.Cfg.CerrarABandeja = v);
            iInicioMin = Palanca("arrancar en la bandeja", "sin ventana al abrirse", () => N.Cfg.IniciarMinimizado, v => N.Cfg.IniciarMinimizado = v);
            iWindows = Palanca("arrancar con Windows", "entra en la clave Run del usuario, sin UAC", () => N.Cfg.IniciarConWindows, v => { N.Cfg.IniciarConWindows = v; Autoarranque.Poner(v); });
            iAtajo = Palanca("atajo global Ctrl+Alt+Espacio", "trae la consola desde cualquier app (se aplica al reiniciar)", () => N.Cfg.AtajoGlobal, v => N.Cfg.AtajoGlobal = v);
            iSonido = Palanca("sonidos", "las campanitas de aviso", () => N.Cfg.Sonido, v => { N.Cfg.Sonido = v; Sonidos.Silencio = !v; if (v) Sonidos.Ok(); });
            iQuindar = Palanca("tonos quindar", "2525 Hz al abrir la transmisión y 2475 Hz al cerrarla, como en Apollo", () => N.Cfg.Quindar, v => { N.Cfg.Quindar = v; if (v) Sonidos.Abrir(); });
            iEnter = Palanca("Enter transmite", "si se apaga, hace falta Ctrl+Enter", () => N.Cfg.EnterEnvia, v => N.Cfg.EnterEnvia = v);
            iHistorial = Palanca("guardar el archivo", "cada transmisión queda en un .json propio", () => N.Cfg.GuardarHistorial, v => N.Cfg.GuardarHistorial = v);
            iNotif = Palanca("avisos de la bandeja", "globitos cuando pasa algo importante", () => N.Cfg.Notificaciones, v => N.Cfg.Notificaciones = v);

            // ---------------- personas
            cajaSistema.Font = Tema.Fina(9.5f);
            cajaSistema.Pista = "el prompt de sistema de esta persona…";
            cajaSistema.TopeLineas = 4;     // barra en cuanto el prompt pasa de cuatro renglones
            cajaSistema.HandleCreated += (s, e) => Win32.BarrasOscuras(cajaSistema.Handle);
            Controls.Add(cajaSistema);

            btGuardarPersona = new Boton { Text = "GUARDAR LA PERSONA", Icono = Boton.Glifo.Copiar, Primario = true, Acento = Tema.Malva };
            btGuardarPersona.Accion += (s, e) => GuardarPersona();
            Controls.Add(btGuardarPersona);

            btNuevaPersona = new Boton { Text = "NUEVA", Icono = Boton.Glifo.Mas, Acento = Tema.Suave };
            btNuevaPersona.Accion += (s, e) => NuevaPersona();
            Controls.Add(btNuevaPersona);

            btProbar = new Boton { Text = "PROBAR EL ENLACE", Icono = Boton.Glifo.Rehacer, Acento = Tema.Teal };
            btProbar.Accion += (s, e) =>
            {
                var est = N.Cli.Sondear(2500);
                N.Log.Escribir(est.Señal == Señal.Nominal ? Nivel.Ok : Nivel.Aviso,
                    "Sondeo a " + N.Cli.Url + " → " + est.Señal + " · " + est.Detalle + " · " + est.MsSondeo + " ms");
                if (est.Señal == Señal.Nominal) Sonidos.Ok(); else Sonidos.Falla();
                N.Avisar();
                Invalidate();
            };
            Controls.Add(btProbar);

            btRestaurar = new Boton { Text = "VALORES DE FÁBRICA", Icono = Boton.Glifo.Rehacer, Acento = Tema.Rosa };
            btRestaurar.Accion += (s, e) => Restaurar();
            Controls.Add(btRestaurar);
        }

        static Color ColorDe(string a)
        {
            switch (a)
            {
                case "teal": return Tema.Teal;
                case "ambar": return Tema.Ambar;
                case "cielo": return Tema.Cielo;
                case "salvia": return Tema.Salvia;
                case "crema": return Tema.Crema;
                case "rosa": return Tema.Rosa;
                default: return Tema.Malva;
            }
        }

        Deslizador Perilla(string etiqueta, double min, double max, double paso, string fmt, Color c, Action<double> set)
        {
            var d = new Deslizador { Etiqueta = etiqueta, Minimo = min, Maximo = max, Paso = paso, Formato = fmt, Acento = c };
            d.Cambio += (s, e) => { if (cargando) return; set(d.Valor); Guardar(); };
            Controls.Add(d);
            return d;
        }

        Interruptor Palanca(string texto, string ayuda, Func<bool> get, Action<bool> set)
        {
            var i = new Interruptor { Text = texto, Ayuda = ayuda, Acento = Tema.Teal, Activo = get() };
            i.Cambio += (s, e) => { if (cargando) return; set(i.Activo); Guardar(); N.Avisar(); };
            Controls.Add(i);
            return i;
        }

        Campo CampoNum(string etiqueta, Func<string> get, Action<string> set)
        {
            var c = new Campo { Etiqueta = etiqueta, Acento = Tema.Cielo };
            c.Texto = get();
            c.Cambio += (s, e) => { if (cargando) return; set(c.Texto.Trim()); Guardar(); };
            Controls.Add(c);
            return c;
        }

        DateTime ultimoGuardado = DateTime.MinValue;
        void Guardar()
        {
            N.Cfg.Guardar();
            ultimoGuardado = DateTime.Now;
            Invalidate();
        }

        public override void Refrescar()
        {
            cargando = true;
            dTemp.Valor = N.Cfg.Temperatura; dTopP.Valor = N.Cfg.TopP; dTopK.Valor = N.Cfg.TopK;
            dMax.Valor = N.Cfg.MaxTokens; dPres.Valor = N.Cfg.Presencia; dRep.Valor = N.Cfg.Repeticion;
            dUI.Valor = N.Cfg.EscalaUI; dFuente.Valor = N.Cfg.EscalaFuente; dAncho.Valor = N.Cfg.AnchoChat;
            iPensar.Activo = N.Cfg.Pensar;
            iBandejaMin.Activo = N.Cfg.MinimizarABandeja; iBandejaCerrar.Activo = N.Cfg.CerrarABandeja;
            iInicioMin.Activo = N.Cfg.IniciarMinimizado; iWindows.Activo = Autoarranque.Activo();
            iAtajo.Activo = N.Cfg.AtajoGlobal; iSonido.Activo = N.Cfg.Sonido; iQuindar.Activo = N.Cfg.Quindar;
            iEnter.Activo = N.Cfg.EnterEnvia; iHistorial.Activo = N.Cfg.GuardarHistorial;
            iReticula.Activo = N.Cfg.Reticula; iNotif.Activo = N.Cfg.Notificaciones;
            cPuerto.Texto = N.Cfg.Puerto.ToString(); cHilos.Texto = N.Cfg.Hilos.ToString(); cContexto.Texto = N.Cfg.Contexto.ToString();
            cServer.Texto = N.Cfg.LlamaServer; cFlags.Texto = N.Cfg.FlagsExtra;
            cCarpetas.Texto = string.Join(";", N.Cfg.CarpetasModelos);
            if (!cToken.Caja.Focused) cToken.Texto = N.Cfg.HfToken;
            N.Bajadas.Sesionar();
            SincronizarAcentos();
            if (editando == null) editando = N.PersonaActual;
            SincronizarPersonas();
            cargando = false;
            Acomodar();
            Invalidate();
        }

        void SincronizarAcentos()
        {
            foreach (var ch in acentos) { ch.Activo = string.Equals(ch.Text, N.Cfg.Acento, StringComparison.OrdinalIgnoreCase); ch.Invalidate(); }
        }

        void SincronizarPersonas()
        {
            foreach (var ch in chipsPersona) { Controls.Remove(ch); ch.Dispose(); }
            chipsPersona.Clear();
            foreach (var p in N.Personas)
            {
                var pp = p;
                var ch = new Chip { Tipo = Chip.Modo.Radio, Text = p.Nombre, Acento = N.ColorPersona(p), Superficie = Tema.Fondo, Activo = editando != null && editando.Clave == p.Clave };
                ch.Accion += (s, e) => { editando = pp; cajaSistema.Text = pp.Sistema; SincronizarPersonas(); Acomodar(); Invalidate(); };
                chipsPersona.Add(ch);
                Controls.Add(ch);
            }
            if (editando != null && cajaSistema.Text != editando.Sistema && !cajaSistema.Focused) cajaSistema.Text = editando.Sistema;
            Acomodar();
        }

        void GuardarPersona()
        {
            if (editando == null) return;
            editando.Sistema = cajaSistema.Text.Trim();
            Persona.Guardar(Path.Combine(N.CarpetaDatos, "personas.json"), N.Personas);
            N.Log.Ok("Persona «" + editando.Nombre + "» guardada");
            Sonidos.Ok();
            N.Avisar();
            Invalidate();
        }

        void NuevaPersona()
        {
            int i = 1;
            while (N.Personas.Any(p => p.Clave == "propia" + i)) i++;
            var p2 = new Persona { Clave = "propia" + i, Nombre = "PROPIA " + i, Color = "cielo", Nota = "tuya", Sistema = "" };
            N.Personas.Add(p2);
            editando = p2;
            cajaSistema.Text = "";
            Persona.Guardar(Path.Combine(N.CarpetaDatos, "personas.json"), N.Personas);
            SincronizarPersonas();
            cajaSistema.Focus();
            Invalidate();
        }

        void Restaurar()
        {
            if (MessageBox.Show(FindForm(), "¿Volver el muestreo y el aspecto a los valores de fábrica?\nNo se toca el archivo de transmisiones ni las personas.",
                "CAPCOM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var d = new Config();
            N.Cfg.Temperatura = d.Temperatura; N.Cfg.TopP = d.TopP; N.Cfg.TopK = d.TopK; N.Cfg.MaxTokens = d.MaxTokens;
            N.Cfg.Presencia = d.Presencia; N.Cfg.Repeticion = d.Repeticion; N.Cfg.Pensar = d.Pensar;
            N.Cfg.EscalaUI = d.EscalaUI; N.Cfg.EscalaFuente = d.EscalaFuente; N.Cfg.Acento = d.Acento;
            N.Cfg.AnchoChat = d.AnchoChat;
            N.Cfg.Reticula = d.Reticula;
            Guardar();
            Refrescar();
            N.Log.Info("Ajustes de fábrica restaurados");
        }

        public override void Modo(string que)
        {
            var q = (que ?? "").ToLowerInvariant();
            if (q == "personas" && N.Personas.Count > 0) { editando = N.Personas[0]; SincronizarPersonas(); }
            if (q == "huggingface" || q == "token")
            {
                cToken.Caja.Focus();
                cToken.Caja.SelectAll();
                N.Bajadas.Sesionar();
            }
            Invalidate();
        }

        // ------------------------------------------------------------------ maquetación

        int yPersonas, yAcentos;

        public override void Acomodar()
        {
            if (Width < 40 || Height < 40) return;
            esc = Dpi.Escala(this);
            int pad = S(14);
            int col = (Width - pad * 3) / 2;
            int xi = pad, xd = pad * 2 + col;

            // --- columna izquierda: muestreo
            int y = S(46);
            foreach (var d in new[] { dTemp, dTopP, dTopK, dMax, dPres, dRep })
            {
                d.SetBounds(xi, y, col - S(8), S(38));
                y += S(42);
            }
            iPensar.SetBounds(xi, y + S(2), col - S(8), S(30));
            y += S(46);

            // --- columna izquierda: enlace
            y += S(24);
            {
                // los tres campos se reparten la fila según lo que tienen adentro (rótulo o valor, el más ancho)
                var g = Maquetador.Medidor;
                var campos = new[] { cPuerto, cHilos, cContexto };
                var nat = campos.Select(c => Math.Max(Tema.MedirTracking(g, c.Etiqueta.ToUpperInvariant(), Tema.Media(7f), S(2)),
                                                       Tema.Medir(g, c.Texto.Length > 0 ? c.Texto : "00000", c.Caja.Font).Width) + S(28)).ToArray();
                var anchos = Tema.Repartir(nat, col - S(8) - S(8) * 2);
                int xf = xi;
                for (int i = 0; i < campos.Length; i++) { campos[i].SetBounds(xf, y, anchos[i], S(40)); xf += anchos[i] + S(8); }
            }
            y += S(48);
            cServer.SetBounds(xi, y, col - S(8), S(40)); y += S(48);
            cFlags.SetBounds(xi, y, col - S(8), S(40)); y += S(48);
            cCarpetas.SetBounds(xi, y, col - S(8), S(40)); y += S(48);
            Boton.Fila(xi, y, S(26), S(8), btProbar, btRestaurar);

            // --- columna izquierda: huggingface (después de los botones: PROBAR EL ENLACE es del servidor local, no de esto)
            y += S(26) + S(38);
            cToken.SetBounds(xi, y, col - S(8), S(40));

            // --- columna derecha: aspecto
            y = S(46);
            // con nota al pie hacen falta 54 px: con 44 la nota se cortaba por abajo
            dUI.SetBounds(xd, y, col - S(8), S(54)); y += S(56);
            dFuente.SetBounds(xd, y, col - S(8), S(54)); y += S(56);
            dAncho.SetBounds(xd, y, col - S(8), S(54)); y += S(58);
            yAcentos = y;
            int x = xd;
            foreach (var ch in acentos)
            {
                ch.Ajustar();
                if (x + ch.Width > xd + col - S(8)) { x = xd; y += ch.Height + S(5); }
                ch.Location = new Point(x, y + S(16));
                x += ch.Width + S(5);
            }
            y += S(46);
            iReticula.SetBounds(xd, y, col - S(8), S(28));
            y += S(38);

            // --- columna derecha: comportamiento
            y += S(20);
            var palancas = new[] { iBandejaMin, iBandejaCerrar, iInicioMin, iWindows, iAtajo, iEnter, iSonido, iQuindar, iHistorial, iNotif };
            // dos columnas: los interruptores usaban media columna y dejaban la otra mitad vacía
            int mitadCol = (col - S(8) - S(12)) / 2;
            int y0 = y;
            for (int i = 0; i < palancas.Length; i++)
            {
                int c = i / ((palancas.Length + 1) / 2), f = i % ((palancas.Length + 1) / 2);
                palancas[i].SetBounds(xd + c * (mitadCol + S(12)), y0 + f * S(30), mitadCol, S(28));
            }
            y = y0 + ((palancas.Length + 1) / 2) * S(30);
            Interruptor.Alinear(palancas.Take(5).ToArray());
            Interruptor.Alinear(palancas.Skip(5).ToArray());

            // --- columna derecha: personas
            y += S(22);
            yPersonas = y;
            x = xd;
            int yy = y + S(20);
            foreach (var ch in chipsPersona)
            {
                ch.Ajustar();
                if (x + ch.Width > xd + col - S(8)) { x = xd; yy += ch.Height + S(5); }
                ch.Location = new Point(x, yy);
                x += ch.Width + S(5);
            }
            yy += S(46);          // aire para la nota de la persona y el contador de tokens del sistema
            int altoCaja = Math.Max(S(50), Height - yy - S(48));
            cajaSistema.SetBounds(xd + S(2), yy, col - S(12), altoCaja);
            Boton.Fila(xd, yy + altoCaja + S(6), S(26), S(8), btGuardarPersona, btNuevaPersona);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            esc = Dpi.Escala(this);
            if (N.Cfg.Reticula) Tema.Reticula(g, ClientRectangle, S(22), Tema.Alpha(Tema.Reticulado, 150));

            int pad = S(14);
            int col = (Width - pad * 3) / 2;
            int xi = pad, xd = pad * 2 + col;

            string guardado = ultimoGuardado == DateTime.MinValue ? "" : "guardado " + Tema.Relativo(ultimoGuardado);
            Tema.Rotulo(g, "05", "muestreo", new Rectangle(xi, S(16), col - S(8), S(14)), Tema.Ambar, esc, guardado);
            // ⭐ los rótulos se ubican desde la posición REAL de los controles, no de una cuenta paralela que
            //    se desincroniza en cuanto se mueve un control (así se terminaba escribiendo encima de ellos)
            Tema.Rotulo(g, "", "enlace con el servidor", new Rectangle(xi, cPuerto.Top - S(21), col - S(8), S(14)), Tema.Teal, esc);
            Tema.Rotulo(g, "", "huggingface", new Rectangle(xi, cToken.Top - S(21), col - S(8), S(14)), Tema.Ambar, esc, N.Bajadas.Sesion);
            {
                // debajo del token: de dónde sale el que se usa, y que no queda en claro en el disco
                var b = N.Bajadas;
                string origen = b.OrigenToken;
                string nota = origen.Length > 0 && origen != "ajustes"
                    ? "usando el token de " + (origen == "huggingface-cli" ? "«hf auth login» (huggingface-cli)" : "la variable " + origen) + " · el de acá manda si ponés uno"
                    : "lo piden los repos restringidos y los privados · se guarda cifrado con tu cuenta de Windows";
                Tema.Texto_(g, nota, Tema.Fina(8f), Tema.Apagado, new Rectangle(xi + S(2), cToken.Bottom + S(4), col - S(12), S(14)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            Tema.Rotulo(g, "06", "aspecto", new Rectangle(xd, S(16), col - S(8), S(14)), N.Cfg.ColorAcento, esc);
            Tema.Tracking(g, "ACENTO", Tema.Media(7f), Tema.Apagado, xd, yAcentos, S(12), S(2));
            Tema.Rotulo(g, "", "comportamiento", new Rectangle(xd, iBandejaMin.Top - S(22), col - S(8), S(14)), Tema.Salvia, esc);
            Tema.Rotulo(g, "", "personas", new Rectangle(xd, yPersonas, col - S(8), S(14)), N.ColorPersona(editando), esc,
                editando != null ? editando.Clave : "");

            // separador entre columnas
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 220), 1f)) g.DrawLine(p, xd - S(8), S(14), xd - S(8), Height - S(14));

            // nota sobre la persona en edición
            if (editando != null)
            {
                var f = Tema.Fina(8f);
                Tema.Texto_(g, editando.Nota, f, Tema.Fantasma, new Rectangle(cajaSistema.Left, cajaSistema.Top - S(15), cajaSistema.Width, S(13)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int tok = Cliente.Estimar(cajaSistema.Text);
                string s = "~" + tok + " tokens de sistema en cada pedido";
                var fm = Tema.Mono(7.5f);
                int w = Tema.Medir(g, s, fm).Width;
                Tema.Texto_(g, s, fm, tok > 400 ? Tema.Ambar : Tema.Fantasma,
                    new Rectangle(cajaSistema.Right - w, cajaSistema.Top - S(15), w + S(3), S(13)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

    }
}
