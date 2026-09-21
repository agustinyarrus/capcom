using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Capcom
{
    /// <summary>
    /// MODELOS: el inventario de `.gguf`, el banco de pruebas y la descarga, en tres modos de la misma pantalla.
    ///
    /// - **inventario**: qué hay en el disco, cuánta RAM pide cada uno, y arranque de un clic (▶ en la fila).
    /// - **examen**: se marcan varios con la casilla y el banco los levanta de a uno, les toma las 20 preguntas
    ///   con corrección automática y arma el ranking. El código se ejecuta de verdad en Node.
    /// - **descargar**: catálogo de HuggingFace, con reanudación y verificación de SHA256.
    ///
    /// La tabla dice de entrada lo único que importa antes de apretar: **cuánta RAM hace falta y cuánta hay**.
    /// Cargar un modelo que no entra deja la máquina inutilizable durante minutos, así que la fila va en rojo y
    /// el botón no arranca.
    /// </summary>
    internal sealed class VistaModelos : Pantalla
    {
        public override string Nombre => "modelos";

        /// <summary>Los tres paneles de la pestaña. (Se llama Panel y no Modo porque `Modo(string)` ya es el gancho de --foto.)</summary>
        enum Panel { Inventario, Examen, Descargar }
        Panel panel = Panel.Inventario;

        readonly Chip chInv, chExa, chDes;
        readonly Boton btLevantar, btBajar, btRefrescar, btCopiarCmd, btCarpeta, btExaminar;
        readonly Boton btCortarExamen, btInforme, btBorrarExamen, btCarpetaExamen;
        readonly Boton btBajarArchivo, btCortarDescarga, btCarpetaDestino;

        List<ModeloArchivo> modelos = new List<ModeloArchivo>();
        ModeloArchivo elegido;
        readonly HashSet<string> marcados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int hover = -1, hoverCasilla = -1, hoverPlay = -1, desplaz;
        readonly List<Rectangle> filas = new List<Rectangle>();
        volatile bool cargando;
        string progreso = "";
        string ultimoDetalle = "";
        Thread hilo;

        // --- examen
        ResultadoExamen examenElegido;
        int hoverExamen = -1, desplazExamen;
        readonly List<Rectangle> filasExamen = new List<Rectangle>();

        // --- descargas
        Repo repoElegido;
        List<ArchivoRemoto> archivos = new List<ArchivoRemoto>();
        string problemaRepo = "";
        volatile bool listando;
        ArchivoRemoto archivoElegido;
        int hoverRepo = -1, hoverArchivo = -1;
        readonly List<Rectangle> filasRepo = new List<Rectangle>();
        readonly List<Rectangle> filasArchivo = new List<Rectangle>();

        public VistaModelos(Nucleo n) : base(n)
        {
            chInv = Segmento("inventario", Panel.Inventario, Tema.Ambar);
            chExa = Segmento("examen", Panel.Examen, Tema.Malva);
            chDes = Segmento("descargar", Panel.Descargar, Tema.Cielo);
            chInv.Activo = true;

            btLevantar = new Boton { Text = "LEVANTAR", Icono = Boton.Glifo.Play, Primario = true, Acento = Tema.Salvia };
            btLevantar.Accion += (s, e) => Levantar();
            Controls.Add(btLevantar);

            btBajar = new Boton { Text = "BAJAR", Icono = Boton.Glifo.Stop, Acento = Tema.Rosa };
            btBajar.Accion += (s, e) => Bajar();
            Controls.Add(btBajar);

            btExaminar = new Boton { Text = "EXAMINAR", Icono = Boton.Glifo.Buscar, Acento = Tema.Malva };
            btExaminar.Accion += (s, e) => Examinar();
            Controls.Add(btExaminar);

            btRefrescar = new Boton { Text = "RELEER EL DISCO", Icono = Boton.Glifo.Rehacer, Acento = Tema.Suave };
            btRefrescar.Accion += (s, e) => Refrescar();
            Controls.Add(btRefrescar);

            btCopiarCmd = new Boton { Text = "COPIAR EL COMANDO", Icono = Boton.Glifo.Copiar, Acento = Tema.Crema };
            btCopiarCmd.Accion += (s, e) =>
            {
                if (elegido == null) return;
                try { Clipboard.SetText(N.Srv.Comando(elegido)); N.Log.Ok("Comando copiado: se puede pegar en una terminal tal cual"); Sonidos.Tic(); } catch { }
            };
            Controls.Add(btCopiarCmd);

            btCarpeta = new Boton { Text = "ABRIR LA CARPETA", Icono = Boton.Glifo.Adjuntar, Acento = Tema.Suave };
            btCarpeta.Accion += (s, e) =>
            {
                if (elegido == null) return;
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + elegido.Ruta + "\""); } catch { }
            };
            Controls.Add(btCarpeta);

            // --- examen
            btCortarExamen = new Boton { Text = "CORTAR EL EXAMEN", Icono = Boton.Glifo.Cortar, Acento = Tema.Rosa };
            btCortarExamen.Accion += (s, e) => N.Banco.Cortar();
            Controls.Add(btCortarExamen);

            btInforme = new Boton { Text = "INFORME .MD", Icono = Boton.Glifo.Copiar, Acento = Tema.Crema };
            btInforme.Accion += (s, e) => GuardarInforme();
            Controls.Add(btInforme);

            btBorrarExamen = new Boton { Text = "BORRAR ESTE", Icono = Boton.Glifo.Basura, Acento = Tema.Rosa };
            btBorrarExamen.Accion += (s, e) => { if (examenElegido != null) { N.Banco.Borrar(examenElegido); examenElegido = null; Invalidate(); } };
            Controls.Add(btBorrarExamen);

            btCarpetaExamen = new Boton { Text = "CARPETA", Icono = Boton.Glifo.Adjuntar, Acento = Tema.Suave };
            btCarpetaExamen.Accion += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", N.Banco.Carpeta); } catch { } };
            Controls.Add(btCarpetaExamen);

            // --- descargas
            btBajarArchivo = new Boton { Text = "BAJAR ESTE", Icono = Boton.Glifo.Flecha, Primario = true, Acento = Tema.Cielo };
            btBajarArchivo.Accion += (s, e) => BajarArchivo();
            Controls.Add(btBajarArchivo);

            btCortarDescarga = new Boton { Text = "CORTAR", Icono = Boton.Glifo.Cortar, Acento = Tema.Rosa };
            btCortarDescarga.Accion += (s, e) => N.Bajadas.Cortar();
            Controls.Add(btCortarDescarga);

            btCarpetaDestino = new Boton { Text = "CARPETA DESTINO", Icono = Boton.Glifo.Adjuntar, Acento = Tema.Suave };
            btCarpetaDestino.Accion += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", CarpetaDestino); } catch { } };
            Controls.Add(btCarpetaDestino);

            N.Banco.Cambio += () => N.EnUi(() => { Botones(); Invalidate(); });
            N.Banco.Fin += () => N.EnUi(() => { Sonidos.Ok(); N.Aviso("Examen terminado", N.Banco.Resultados.Count + " modelos en el ranking"); Invalidate(); });
            N.Bajadas.Cambio += () => N.EnUi(() => { Botones(); Invalidate(); });
            N.Bajadas.Listo += r => N.EnUi(() => { Refrescar(); Sonidos.Ok(); N.Aviso("Modelo bajado", Path.GetFileName(r)); });
        }

        Chip Segmento(string texto, Panel m, Color c)
        {
            var ch = new Chip { Tipo = Chip.Modo.Radio, Text = texto, Acento = c, Superficie = Tema.Fondo };
            ch.Accion += (s, e) => PonerPanel(m);
            Controls.Add(ch);
            return ch;
        }

        void PonerPanel(Panel m)
        {
            panel = m;
            chInv.Activo = m == Panel.Inventario; chExa.Activo = m == Panel.Examen; chDes.Activo = m == Panel.Descargar;
            chInv.Invalidate(); chExa.Invalidate(); chDes.Invalidate();
            if (m == Panel.Descargar && repoElegido == null && Descargas.Catalogo.Count > 0) ElegirRepo(Descargas.Catalogo[0]);
            if (m == Panel.Examen && examenElegido == null) examenElegido = N.Banco.Resultados.FirstOrDefault();
            Botones();
            Acomodar();
            Invalidate();
        }

        string CarpetaDestino => N.Cfg.CarpetasModelos.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";

        // ------------------------------------------------------------------ datos

        public override void Refrescar()
        {
            modelos = N.Srv.Inventario();
            if (elegido != null) elegido = modelos.FirstOrDefault(m => m.Ruta == elegido.Ruta);
            if (elegido == null && N.Cfg.ModeloPreferido.Length > 0)
                elegido = modelos.FirstOrDefault(m => string.Equals(m.Ruta, N.Cfg.ModeloPreferido, StringComparison.OrdinalIgnoreCase));
            if (elegido == null)
            {
                var cargado = N.Cli.Estado.ModeloCorto;
                if (cargado.Length > 0) elegido = modelos.FirstOrDefault(m => m.Nombre == cargado);
            }
            if (elegido == null) elegido = modelos.FirstOrDefault(m => !m.EsProyector);
            marcados.RemoveWhere(k => !modelos.Any(m => m.Ruta == k));
            Botones();
            Invalidate();
        }

        void Botones()
        {
            bool inv = panel == Panel.Inventario, exa = panel == Panel.Examen, des = panel == Panel.Descargar;
            btLevantar.Visible = btBajar.Visible = btCopiarCmd.Visible = btCarpeta.Visible = btRefrescar.Visible = btExaminar.Visible = inv;
            btCortarExamen.Visible = btInforme.Visible = btBorrarExamen.Visible = btCarpetaExamen.Visible = exa;
            btBajarArchivo.Visible = btCortarDescarga.Visible = btCarpetaDestino.Visible = des;

            bool vivo = N.Cli.Estado.Señal == Señal.Nominal;
            double libre, total;
            Win32.Memoria(out libre, out total);
            bool entra = elegido != null && !elegido.EsProyector && elegido.GbNecesarios(N.Cfg.Contexto) <= libre;
            btLevantar.Enabled = !cargando && !vivo && entra;
            btLevantar.Text = cargando ? "CARGANDO…" : "LEVANTAR";
            btBajar.Enabled = !cargando && N.Srv.Nuestro;
            btCopiarCmd.Enabled = elegido != null;
            btCarpeta.Enabled = elegido != null;
            btExaminar.Enabled = !N.Banco.Corriendo && Marcados().Count > 0;
            btExaminar.Text = N.Banco.Corriendo ? "EXAMEN EN CURSO…" : "EXAMINAR" + (Marcados().Count > 0 ? " (" + Marcados().Count + ")" : "");
            btExaminar.Primario = Marcados().Count > 0;

            btCortarExamen.Enabled = N.Banco.Corriendo;
            btInforme.Enabled = N.Banco.Resultados.Count > 0;
            btBorrarExamen.Enabled = examenElegido != null;

            btBajarArchivo.Enabled = !N.Bajadas.Bajando && archivoElegido != null;
            btCortarDescarga.Enabled = N.Bajadas.Bajando;

            foreach (var b in new[] { btLevantar, btBajar, btCopiarCmd, btCarpeta, btExaminar, btCortarExamen, btInforme, btBorrarExamen, btBajarArchivo, btCortarDescarga })
                b.Invalidate();
            Acomodar();
        }

        List<ModeloArchivo> Marcados() => modelos.Where(m => marcados.Contains(m.Ruta)).ToList();

        // ------------------------------------------------------------------ acciones

        /// <summary>Levanta el modelo cuyo nombre contenga `q`. Devuelve false si no lo encuentra o ya hay uno.</summary>
        public bool LevantarPorNombre(string q, Action<bool, string> alTerminar, out string porque)
        {
            porque = "";
            if (cargando) { porque = "ya hay una carga en curso"; return false; }
            if (N.Cli.Estado.Señal == Señal.Nominal) { porque = "ya hay un modelo en línea: " + N.Cli.Estado.ModeloCorto; return false; }
            if (modelos.Count == 0) Refrescar();
            var m = modelos.FirstOrDefault(x => !x.EsProyector && x.Nombre.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (m == null) { porque = "no encontré ningún modelo que diga «" + q + "»"; return false; }
            elegido = m;
            Botones();
            Levantar(alTerminar);
            return true;
        }

        public bool BajarAhora(out string porque)
        {
            porque = "";
            if (!N.Srv.Nuestro) { porque = "el servidor que corre no lo levantamos nosotros: no lo toco"; return false; }
            Bajar();
            return true;
        }

        void Levantar() { Levantar(null); }

        /// <summary>Arranque rápido desde la fila: si hay uno nuestro arriba, lo baja y levanta el otro.</summary>
        void LevantarRapido(ModeloArchivo m)
        {
            if (cargando || m == null || m.EsProyector) return;
            if (N.Cli.Estado.Señal == Señal.Nominal)
            {
                if (!N.Srv.Nuestro)
                {
                    ultimoDetalle = "hay un servidor ajeno en el puerto " + N.Cfg.Puerto + ": no lo bajo yo";
                    Invalidate();
                    return;
                }
                if (string.Equals(N.Cli.Estado.ModeloCorto, m.Nombre, StringComparison.OrdinalIgnoreCase))
                {
                    ultimoDetalle = m.Nombre + " ya está en línea";
                    Invalidate();
                    return;
                }
                N.Srv.Bajar();
                N.Cli.Sondear(800);
            }
            elegido = m;
            Botones();
            Levantar(null);
        }

        void Levantar(Action<bool, string> alTerminar)
        {
            if (cargando || elegido == null) return;
            var m = elegido;
            cargando = true;
            progreso = "arrancando el proceso…";
            ultimoDetalle = "";
            Botones();
            Invalidate();
            N.Cfg.ModeloPreferido = m.Ruta;
            N.Cfg.Guardar();
            hilo = new Thread(() =>
            {
                string det;
                bool ok = N.Srv.Levantar(m, p => { progreso = p; N.EnUi(Invalidate); }, out det);
                N.EnUi(() =>
                {
                    cargando = false;
                    ultimoDetalle = det;
                    progreso = "";
                    if (ok) { N.Log.Ok("Modelo en línea · " + det); Sonidos.Ok(); N.Aviso("Modelo en línea", m.Nombre); }
                    else { N.Log.Error("No pude levantar " + m.Nombre + " · " + det); Sonidos.Falla(); }
                    N.Cli.Sondear();
                    Botones();
                    N.Avisar();
                    Invalidate();
                    if (alTerminar != null) alTerminar(ok, det);
                });
            })
            { IsBackground = true, Name = "capcom-levantar" };
            hilo.Start();
        }

        void Bajar()
        {
            if (!N.Srv.Nuestro) return;
            N.Srv.Bajar();
            ultimoDetalle = "servidor bajado";
            N.Cli.Sondear();
            Botones();
            N.Avisar();
            Invalidate();
        }

        /// <summary>Manda los marcados al banco de pruebas.</summary>
        public bool Examinar()
        {
            var lista = Marcados();
            if (lista.Count == 0 && elegido != null) lista = new List<ModeloArchivo> { elegido };
            string porque;
            if (!N.Banco.Correr(lista, out porque))
            {
                ultimoDetalle = porque;
                N.Log.Aviso("Banco: " + porque);
                Invalidate();
                return false;
            }
            PonerPanel(Panel.Examen);
            N.Log.Info("Banco de pruebas: " + lista.Count + " modelo/s en cola");
            return true;
        }

        /// <summary>Marca por nombre (coma para varios) y arranca el banco. Lo usa la orden `--examinar`.</summary>
        public bool ExaminarPorNombre(string lista, out string porque)
        {
            porque = "";
            if (modelos.Count == 0) Refrescar();
            marcados.Clear();
            foreach (var q in (lista ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                var m = modelos.FirstOrDefault(x => !x.EsProyector && x.Nombre.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (m != null) marcados.Add(m.Ruta);
                else porque = "no encontré ningún modelo que diga «" + q + "»";
            }
            if (marcados.Count == 0) { if (porque.Length == 0) porque = "no marcaste ninguno"; return false; }
            Botones();
            if (!Examinar()) { porque = ultimoDetalle; return false; }
            return true;
        }

        void GuardarInforme()
        {
            using (var d = new SaveFileDialog
            {
                Title = "Guardar el informe del banco",
                Filter = "Markdown|*.md",
                FileName = "banco-modelos-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".md",
            })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, N.Banco.Informe(), new System.Text.UTF8Encoding(false));
                    N.Log.Ok("Informe guardado en " + d.FileName);
                    Sonidos.Ok();
                }
                catch (Exception ex) { N.Log.Error("No pude guardar el informe: " + ex.Message); }
            }
        }

        void ElegirRepo(Repo r)
        {
            repoElegido = r;
            archivos = new List<ArchivoRemoto>();
            archivoElegido = null;
            problemaRepo = "";
            listando = true;
            Invalidate();
            new Thread(() =>
            {
                string porque;
                var l = N.Bajadas.Listar(r, out porque);
                N.EnUi(() =>
                {
                    // 🚨 carrera real: al entrar al panel se lista el primer repo en un hilo, y si mientras tanto
                    //    se eligió otro (o lo eligió una orden `--traer`), la respuesta vieja llegaba después y
                    //    pisaba la lista buena — el encabezado decía un repo y la tabla mostraba los archivos de
                    //    otro. La respuesta sólo se acepta si sigue siendo la del repo elegido.
                    if (repoElegido == null || repoElegido.Ruta != r.Ruta) return;
                    listando = false;
                    archivos = l;
                    problemaRepo = porque;
                    // preseleccionar la cuantización recomendada del repo
                    archivoElegido = l.FirstOrDefault(a => a.Nombre.IndexOf(r.Prefiere, StringComparison.OrdinalIgnoreCase) >= 0)
                                  ?? l.FirstOrDefault();
                    Botones();
                    Invalidate();
                });
            })
            { IsBackground = true, Name = "capcom-listar" }.Start();
        }

        /// <summary>
        /// Busca en el catálogo y arranca la descarga. `q` es «repo archivo», por ejemplo `qwen3.5-1.5b q2_k`.
        /// Lo usa la orden `--traer` y también sirve para dejar bajando algo desde la terminal.
        /// </summary>
        public bool TraerPorNombre(string q, out string porque)
        {
            porque = "";
            var partes = (q ?? "").Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length == 0) { porque = "uso: --traer \"<repo> <cuantización>\""; return false; }
            var r = Descargas.Catalogo.FirstOrDefault(c => c.Nombre.IndexOf(partes[0], StringComparison.OrdinalIgnoreCase) >= 0
                                                        || c.Ruta.IndexOf(partes[0], StringComparison.OrdinalIgnoreCase) >= 0);
            if (r == null) { porque = "no hay ningún repo que diga «" + partes[0] + "»"; return false; }
            string pp;
            var lista = N.Bajadas.Listar(r, out pp);
            if (lista.Count == 0) { porque = pp.Length > 0 ? pp : "el repo no tiene .gguf"; return false; }
            string filtro = partes.Length > 1 ? partes[1].Trim() : r.Prefiere;
            var a = lista.FirstOrDefault(x => x.Nombre.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) >= 0) ?? lista.First();
            PonerPanel(Panel.Descargar);
            repoElegido = r;
            archivos = lista;
            archivoElegido = a;
            Botones();
            Invalidate();
            if (!N.Bajadas.Bajar(a, CarpetaDestino, out porque)) return false;
            porque = a.Nombre + " · " + Tema.Bytes(a.Bytes);
            return true;
        }

        void BajarArchivo()
        {
            if (archivoElegido == null) return;
            string porque;
            if (!N.Bajadas.Bajar(archivoElegido, CarpetaDestino, out porque))
            {
                problemaRepo = porque;
                N.Log.Aviso("Descarga: " + porque);
                Invalidate();
                return;
            }
            N.Log.Info("Bajando " + archivoElegido.Nombre + " (" + Tema.Bytes(archivoElegido.Bytes) + ") a " + CarpetaDestino);
        }

        public override void Modo(string que)
        {
            var q = (que ?? "").ToLowerInvariant();
            if (q.Length == 0) return;
            if (q == "examen") { PonerPanel(Panel.Examen); return; }
            if (q == "descargar" || q == "bajar") { PonerPanel(Panel.Descargar); return; }
            if (q == "inventario") { PonerPanel(Panel.Inventario); return; }
            var m = modelos.FirstOrDefault(x => x.Nombre.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (m != null) { elegido = m; Botones(); }
            Invalidate();
        }

        // ------------------------------------------------------------------ maquetación

        public override void Acomodar()
        {
            if (Width < 10) return;
            esc = Dpi.Escala(this);
            int x = S(12);
            chInv.Ajustar(); chExa.Ajustar(); chDes.Ajustar();
            chInv.Location = new Point(x, S(32));
            chExa.Location = new Point(chInv.Right + S(6), S(32));
            chDes.Location = new Point(chExa.Right + S(6), S(32));

            int yb = Height - S(38);
            switch (panel)
            {
                case Panel.Inventario:
                    Boton.Fila(x, yb, S(26), S(8), btLevantar, btBajar, btExaminar, btCopiarCmd, btCarpeta);
                    btRefrescar.SetBounds(Width - S(12) - btRefrescar.AnchoDeseado, yb, btRefrescar.AnchoDeseado, S(26));
                    break;
                case Panel.Examen:
                    Boton.Fila(x, yb, S(26), S(8), btCortarExamen, btInforme, btBorrarExamen);
                    btCarpetaExamen.SetBounds(Width - S(12) - btCarpetaExamen.AnchoDeseado, yb, btCarpetaExamen.AnchoDeseado, S(26));
                    break;
                case Panel.Descargar:
                    Boton.Fila(x, yb, S(26), S(8), btBajarArchivo, btCortarDescarga);
                    btCarpetaDestino.SetBounds(Width - S(12) - btCarpetaDestino.AnchoDeseado, yb, btCarpetaDestino.AnchoDeseado, S(26));
                    break;
            }
        }

        // ------------------------------------------------------------------ interacción

        int AltoFila => S(26);
        int yTabla = 100;
        int AltoTabla => Math.Max(S(60), Height - yTabla - S(19) - (panel == Panel.Inventario ? S(212) : S(60)));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1, hc = -1, hp = -1, he = -1, hr = -1, ha = -1;
            if (panel == Panel.Inventario)
            {
                h = filas.FindIndex(r => r.Contains(e.Location));
                if (h >= 0)
                {
                    if (Casilla(filas[h]).Contains(e.Location)) hc = h;
                    if (Play(filas[h]).Contains(e.Location)) hp = h;
                }
            }
            else if (panel == Panel.Examen) he = filasExamen.FindIndex(r => r.Contains(e.Location));
            else
            {
                hr = filasRepo.FindIndex(r => r.Contains(e.Location));
                ha = filasArchivo.FindIndex(r => r.Contains(e.Location));
            }
            if (h != hover || hc != hoverCasilla || hp != hoverPlay || he != hoverExamen || hr != hoverRepo || ha != hoverArchivo)
            {
                hover = h; hoverCasilla = hc; hoverPlay = hp; hoverExamen = he; hoverRepo = hr; hoverArchivo = ha;
                Cursor = (h >= 0 || he >= 0 || hr >= 0 || ha >= 0) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = hoverCasilla = hoverPlay = hoverExamen = hoverRepo = hoverArchivo = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        Rectangle Casilla(Rectangle fila) => new Rectangle(fila.X + S(6), fila.Y + (fila.Height - S(11)) / 2, S(11), S(11));
        Rectangle Play(Rectangle fila) => new Rectangle(fila.Right - S(26), fila.Y + (fila.Height - S(16)) / 2, S(20), S(16));

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (panel == Panel.Inventario)
            {
                int i = filas.FindIndex(r => r.Contains(e.Location));
                if (i < 0 || i >= modelos.Count) return;
                var m = modelos[i];
                if (Play(filas[i]).Contains(e.Location) && !m.EsProyector) { LevantarRapido(m); return; }
                if (Casilla(filas[i]).Contains(e.Location) || (Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    if (m.EsProyector) return;
                    if (!marcados.Remove(m.Ruta)) marcados.Add(m.Ruta);
                    Botones();
                    Invalidate();
                    return;
                }
                elegido = m;
                Botones();
                Invalidate();
                return;
            }
            if (panel == Panel.Examen)
            {
                int i = filasExamen.FindIndex(r => r.Contains(e.Location));
                if (i >= 0 && i < N.Banco.Resultados.Count) { examenElegido = N.Banco.Resultados[i]; Botones(); Invalidate(); }
                return;
            }
            int ir = filasRepo.FindIndex(r => r.Contains(e.Location));
            if (ir >= 0 && ir < Descargas.Catalogo.Count) { ElegirRepo(Descargas.Catalogo[ir]); return; }
            int ia = filasArchivo.FindIndex(r => r.Contains(e.Location));
            if (ia >= 0 && ia < archivos.Count) { archivoElegido = archivos[ia]; Botones(); Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (panel == Panel.Inventario)
            {
                int max = Math.Max(0, modelos.Count * AltoFila - AltoTabla);
                desplaz = Math.Max(0, Math.Min(max, desplaz - Math.Sign(e.Delta) * AltoFila * 2));
            }
            else if (panel == Panel.Examen)
            {
                int max = Math.Max(0, N.Banco.Resultados.Count * AltoFila - AltoTabla);
                desplazExamen = Math.Max(0, Math.Min(max, desplazExamen - Math.Sign(e.Delta) * AltoFila * 2));
            }
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
            filas.Clear(); filasExamen.Clear(); filasRepo.Clear(); filasArchivo.Clear();

            int x = S(12), w = Width - S(24);
            double libre, total;
            Win32.Memoria(out libre, out total);
            var est = N.Cli.Estado;

            string derecha = panel == Panel.Inventario ? modelos.Count(m => !m.EsProyector) + " modelos · " + libre.ToString("0.0") + " GB libres"
                           : panel == Panel.Examen ? N.Banco.Resultados.Count + " evaluados · " + Examen.Preguntas.Count + " preguntas"
                           : Descargas.Catalogo.Count + " repos · destino " + Path.GetFileName(CarpetaDestino);
            Tema.Rotulo(g, "03", panel == Panel.Inventario ? "inventario de modelos" : panel == Panel.Examen ? "banco de pruebas" : "descargar modelos",
                new Rectangle(x, S(12), w, S(14)), panel == Panel.Inventario ? Tema.Ambar : panel == Panel.Examen ? Tema.Malva : Tema.Cielo, esc, derecha);

            int y = S(66);
            y = PanelEstado(g, x, y, w, est, libre, total);

            switch (panel)
            {
                case Panel.Inventario: Inventario(g, x, y, w, libre, est); break;
                case Panel.Examen: ExamenPanel(g, x, y, w); break;
                case Panel.Descargar: Descargar(g, x, y, w); break;
            }
        }

        int PanelEstado(Graphics g, int x, int y, int w, EstadoServidor est, double libre, double total)
        {
            var rEstado = new Rectangle(x, y, w, S(42));
            using (var b = new SolidBrush(Tema.Alpha(Tema.Consola, 180))) g.FillRectangle(b, rEstado);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 220), 1f)) g.DrawRectangle(p, rEstado);
            bool vivo = est.Señal == Señal.Nominal;
            var cSeñal = vivo ? Tema.Teal : est.Señal == Señal.Cargando ? Tema.Ambar : Tema.Apagado;
            using (var b = new SolidBrush(Tema.Alpha(cSeñal, 200))) g.FillRectangle(b, rEstado.X, rEstado.Y, S(2), rEstado.Height);
            Tema.Diodo(g, rEstado.X + S(16), rEstado.Y + S(21), S(5), cSeñal, vivo, vivo ? 1f : 0.3f);
            string titulo = cargando ? "CARGANDO" : vivo ? "EN LÍNEA" : "APAGADO";
            Tema.Tracking(g, titulo, Tema.Media(8.5f), cSeñal, rEstado.X + S(28), rEstado.Y + S(8), S(13), S(2));
            string sub = cargando ? progreso
                       : vivo ? est.ModeloCorto + "  ·  puerto " + N.Cfg.Puerto + (N.Srv.Nuestro ? "  ·  lo levantamos nosotros (pid " + N.Srv.Pid + ")" : "  ·  lo levantó otro proceso")
                       : ultimoDetalle.Length > 0 ? ultimoDetalle : "elegí un modelo de la lista y apretá LEVANTAR, o tocá el ▶ de su fila";
            Tema.Texto_(g, sub, Tema.Fina(8.5f), Tema.Suave, new Rectangle(rEstado.X + S(28), rEstado.Y + S(23), rEstado.Width - S(210), S(14)),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);

            var fm = Tema.Mono(7.5f);
            int wMem = S(158);
            int xMem = rEstado.Right - wMem - S(14);
            Tema.Tracking(g, "MEMORIA", Tema.Media(7f), Tema.Apagado, xMem, rEstado.Y + S(7), S(11), S(2));
            string sMem = libre.ToString("0.0") + " / " + total.ToString("0.0") + " GB";
            Tema.Texto_(g, sMem, fm, Tema.Suave, new Rectangle(xMem, rEstado.Y + S(7), wMem, S(11)), TextFormatFlags.Right | TextFormatFlags.Top);
            double uso = total > 0 ? 1 - libre / total : 0;
            Tema.Segmentos(g, new RectangleF(xMem, rEstado.Y + S(24), wMem, S(6)), (float)uso, uso > 0.9 ? Tema.Rosa : Tema.Cielo, esc, 26);

            y = rEstado.Bottom + S(7);
            var ajenos = N.Srv.Ajenos();
            if (ajenos.Count > 0 && !N.Srv.Nuestro && panel == Panel.Inventario)
            {
                string s = "hay " + ajenos.Count + " llama-server que no levantamos nosotros (pid " + string.Join(", ", ajenos) + ") · lo uso pero no lo toco";
                Tema.Texto_(g, s, Tema.Fina(8f), Tema.Ambar, new Rectangle(x + S(4), y, w, S(13)));
                y += S(16);
            }
            return y;
        }

        // ---------------- modo inventario

        void Inventario(Graphics g, int x, int y, int w, double libre, EstadoServidor est)
        {
            yTabla = y;
            string[] cab = { "modelo", "familia", "cuantización", "peso", "ram", "carpeta" };
            int[] anchos = AnchosTabla(g, cab, w - S(34));
            var fc = Tema.Media(7f);
            int xc = x + S(34);
            for (int i = 0; i < cab.Length; i++)
            {
                bool numerica = i == 3 || i == 4;
                int wc = Tema.MedirTracking(g, cab[i].ToUpperInvariant(), fc, S(2));
                int xh = numerica ? xc + anchos[i] - S(14) - wc : xc;
                Tema.Tracking(g, cab[i].ToUpperInvariant(), fc, Tema.Apagado, xh, y, S(12), S(2));
                xc += anchos[i];
            }
            y += S(15);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 235), 1f)) g.DrawLine(p, x, y, x + w, y);
            y += S(4);

            int alto = AltoTabla;
            g.SetClip(new Rectangle(x, y, w, alto));
            int yy = y - desplaz;
            var fN = Tema.Fina(9f);
            var fD = Tema.Mono(8f);
            bool vivo = est.Señal == Señal.Nominal;
            for (int i = 0; i < modelos.Count; i++, yy += AltoFila)
            {
                var m = modelos[i];
                var r = new Rectangle(x, yy, w, AltoFila - S(2));
                filas.Add(r);
                // 🚨 la fila se dibuja sólo si entra ENTERA: `TextRenderer` (GDI) no respeta el Clip de GDI+,
                //    así que la última fila se escapaba de la banda y se montaba sobre la ficha de abajo.
                if (yy < y || yy + AltoFila > y + alto) continue;
                bool sel = elegido != null && elegido.Ruta == m.Ruta;
                bool hov = hover == i;
                bool mar = marcados.Contains(m.Ruta);
                double necesita = m.GbNecesarios(N.Cfg.Contexto);
                bool entra = necesita <= libre;
                bool enLinea = vivo && est.ModeloCorto == m.Nombre;
                var c = m.EsProyector ? Tema.Fantasma : enLinea ? Tema.Teal : entra ? Tema.Salvia : Tema.Rosa;

                if (sel || hov || mar)
                    using (var b = new SolidBrush(mar ? Tema.Alpha(Tema.Malva, 26) : sel ? Tema.Alpha(Tema.Consola, 235) : Tema.Alpha(Tema.Consola, 130)))
                        g.FillRectangle(b, r);
                if (sel) using (var b = new SolidBrush(Tema.Alpha(Tema.Ambar, 220))) g.FillRectangle(b, r.X, r.Y, S(2), r.Height);
                else if (mar) using (var b = new SolidBrush(Tema.Alpha(Tema.Malva, 200))) g.FillRectangle(b, r.X, r.Y, S(2), r.Height);

                // casilla de selección múltiple
                if (!m.EsProyector)
                {
                    var rc = Casilla(r);
                    var cc = mar ? Tema.Malva : hoverCasilla == i ? Tema.Suave : Tema.Alpha(Tema.Suave, 90);
                    using (var p = new Pen(cc, 1f)) g.DrawRectangle(p, rc.X, rc.Y, rc.Width, rc.Height);
                    if (mar) using (var b = new SolidBrush(Tema.Malva)) g.FillRectangle(b, rc.X + S(3), rc.Y + S(3), rc.Width - S(5), rc.Height - S(5));
                }

                Tema.Diodo(g, r.X + S(25), r.Y + r.Height / 2f, S(4), c, enLinea || (!m.EsProyector && entra), enLinea ? 1f : 0.35f);
                int cx = r.X + S(34);
                var ct = m.EsProyector ? Tema.Fantasma : sel ? Tema.Texto : hov || mar ? Tema.Suave : Tema.Alpha(Tema.Suave, 215);
                int wNombre = anchos[0] - S(8) - (hov ? S(26) : 0);
                Tema.Texto_(g, m.Nombre, fN, ct, new Rectangle(cx, r.Y, wNombre, r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                cx += anchos[0];
                Tema.Texto_(g, m.EsProyector ? "visión" : m.Familia, Tema.Fina(8.5f), Tema.Apagado, new Rectangle(cx, r.Y, anchos[1] - S(8), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                cx += anchos[1];
                Tema.Texto_(g, m.Cuant, fD, m.Cuant.StartsWith("Q8") || m.Cuant.StartsWith("F") ? Tema.Crema : Tema.Alpha(Tema.Suave, 200), new Rectangle(cx, r.Y, anchos[2] - S(8), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                cx += anchos[2];
                Tema.Texto_(g, m.Gb.ToString("0.00"), fD, Tema.Suave, new Rectangle(cx, r.Y, anchos[3] - S(14), r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                cx += anchos[3];
                Tema.Texto_(g, m.EsProyector ? "—" : necesita.ToString("0.0"), fD, m.EsProyector ? Tema.Fantasma : entra ? Tema.Salvia : Tema.Rosa,
                    new Rectangle(cx, r.Y, anchos[4] - S(14), r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                cx += anchos[4];
                Tema.Texto_(g, m.Carpeta, Tema.Fina(8f), Tema.Fantasma, new Rectangle(cx, r.Y, Math.Max(S(20), r.Right - cx - S(34)), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // nota del examen, si lo rindió
                var ex = N.Banco.DeModelo(m.Nombre);
                if (ex != null && ex.Problema.Length == 0 && !hov)
                {
                    var fx = Tema.Media(7f);
                    string s = ex.Nivel + " · " + ex.Puntaje.ToString("0");
                    int wx = Tema.MedirTracking(g, s, fx, S(1)) + S(12);
                    var rx = new RectangleF(r.Right - wx - S(6), r.Y + S(5), wx, S(14));
                    var cx2 = ex.Nivel == "A" ? Tema.Salvia : ex.Nivel == "B" ? Tema.Teal : ex.Nivel == "C" ? Tema.Ambar : Tema.Rosa;
                    Tema.Placa(g, rx, S(2), Tema.Mezcla(Tema.Fondo, cx2, 0.16f), Tema.Alpha(cx2, 110));
                    Tema.Tracking(g, s, fx, cx2, (int)rx.X + S(6), (int)rx.Y, S(14), S(1));
                }

                if (enLinea && !hov)
                    Tema.Insignia(g, "EN LÍNEA", Tema.Media(7f), Tema.Teal, r.Right - (ex != null ? S(58) : S(8)), r.Y + S(5), esc, true);

                // ▶ arranque rápido, sólo en la fila bajo el mouse
                if (hov && !m.EsProyector)
                {
                    var rp = Play(r);
                    bool puede = !cargando && (!vivo || N.Srv.Nuestro) && entra && !enLinea;
                    var cp = !puede ? Tema.Fantasma : hoverPlay == i ? Tema.Salvia : Tema.Alpha(Tema.Salvia, 190);
                    if (hoverPlay == i && puede)
                        Tema.Placa(g, new RectangleF(rp.X, rp.Y, rp.Width, rp.Height), S(2), Tema.Mezcla(Tema.Fondo, Tema.Salvia, 0.2f), Tema.Alpha(Tema.Salvia, 150));
                    Boton.DibujarGlifo(g, Boton.Glifo.Play, new RectangleF(rp.X + S(6), rp.Y + S(3), S(9), S(10)), cp, esc);
                }
            }
            g.ResetClip();
            if (modelos.Count == 0)
                Tema.Texto_(g, "no encontré ningún .gguf en " + string.Join(" · ", N.Cfg.CarpetasModelos) + " — probá el modo DESCARGAR",
                    Tema.Fina(9f), Tema.Fantasma, new Rectangle(x + S(4), y + S(10), w, S(40)), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

            // ---------------- ficha del elegido
            int yF = y + alto + S(10);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 220), 1f)) g.DrawLine(p, x, yF - S(6), x + w, yF - S(6));
            if (elegido == null) return;

            int nMarcados = Marcados().Count;
            Tema.Rotulo(g, "", "modelo elegido", new Rectangle(x, yF, w / 2, S(14)), Tema.Ambar, esc,
                nMarcados > 0 ? nMarcados + " marcados para el examen" : "marcá con la casilla para examinar varios");
            yF += S(20);
            var fV = Tema.Mono(8f);
            var exa = N.Banco.DeModelo(elegido.Nombre);
            var fichas = new List<string[]>
            {
                new[] { "archivo", elegido.Nombre },
                new[] { "ruta", elegido.Ruta },
                new[] { "peso en disco", elegido.Gb.ToString("0.00") + " GB" },
                new[] { "ram estimada", elegido.GbNecesarios(N.Cfg.Contexto).ToString("0.0") + " GB  (pesos + estado + " + N.Cfg.Contexto.ToString("N0") + " de contexto)" },
                new[] { "veredicto", elegido.EsProyector ? "es un proyector de visión: acompaña a otro modelo, no se carga solo"
                        : elegido.GbNecesarios(N.Cfg.Contexto) <= libre ? "entra en la memoria que hay ahora" : "NO entra: faltan " + (elegido.GbNecesarios(N.Cfg.Contexto) - libre).ToString("0.0") + " GB" },
                new[] { "examen", exa == null ? "todavía no rindió · marcalo y apretá EXAMINAR"
                        : exa.Problema.Length > 0 ? exa.Problema
                        : "nivel " + exa.Nivel + " · " + exa.Puntaje.ToString("0.0") + " puntos · " + exa.SegMediana.ToString("0.0") + " s de mediana · " + exa.Veredicto },
            };
            int anchoClave = S(96);
            foreach (var fi in fichas)
            {
                Tema.Texto_(g, fi[0], Tema.Fina(8.5f), Tema.Apagado, new Rectangle(x + S(4), yF, anchoClave, S(15)));
                var col = fi[0] == "veredicto"
                    ? (elegido.EsProyector ? Tema.Fantasma : elegido.GbNecesarios(N.Cfg.Contexto) <= libre ? Tema.Salvia : Tema.Rosa)
                    : fi[0] == "examen" && exa != null && exa.Problema.Length == 0
                    ? (exa.Nivel == "A" ? Tema.Salvia : exa.Nivel == "B" ? Tema.Teal : exa.Nivel == "C" ? Tema.Ambar : Tema.Rosa)
                    : Tema.Suave;
                Tema.Texto_(g, fi[1], fi[0] == "ruta" || fi[0] == "archivo" ? fV : Tema.Fina(8.5f), col,
                    new Rectangle(x + S(4) + anchoClave, yF, w - anchoClave - S(8), S(15)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                yF += S(16);
            }
            yF += S(4);
            Tema.Texto_(g, "comando", Tema.Fina(8.5f), Tema.Apagado, new Rectangle(x + S(4), yF, anchoClave, S(15)));
            Tema.Texto_(g, N.Srv.Comando(elegido), Tema.Mono(7.5f), Tema.Alpha(Tema.Crema, 210),
                new Rectangle(x + S(4) + anchoClave, yF, w - anchoClave - S(8), S(30)),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        int[] AnchosTabla(Graphics g, string[] cab, int disponible)
        {
            var fN = Tema.Fina(9f);
            var fD = Tema.Mono(8f);
            var fF = Tema.Fina(8.5f);
            var fC = Tema.Media(7f);
            var a = new int[6];
            for (int i = 0; i < 6; i++) a[i] = Tema.MedirTracking(g, cab[i].ToUpperInvariant(), fC, S(2)) + S(16);
            foreach (var m in modelos)
            {
                a[0] = Math.Max(a[0], Tema.Medir(g, m.Nombre, fN).Width + S(16));
                a[1] = Math.Max(a[1], Tema.Medir(g, m.EsProyector ? "visión" : m.Familia, fF).Width + S(16));
                a[2] = Math.Max(a[2], Tema.Medir(g, m.Cuant, fD).Width + S(16));
                a[3] = Math.Max(a[3], Tema.Medir(g, m.Gb.ToString("0.00"), fD).Width + S(20));
                a[4] = Math.Max(a[4], Tema.Medir(g, m.GbNecesarios(N.Cfg.Contexto).ToString("0.0"), fD).Width + S(20));
                a[5] = Math.Max(a[5], Tema.Medir(g, m.Carpeta, fF).Width + S(16));
            }
            a[0] += S(14);
            int[] minimo = { S(120), S(44), S(58), S(46), S(46), S(40) };
            return Tema.Repartir(a, disponible, minimo);
        }

        // ---------------- modo examen

        void ExamenPanel(Graphics g, int x, int y, int w)
        {
            var banco = N.Banco;
            // --- barra de progreso mientras corre
            if (banco.Corriendo)
            {
                var rp = new Rectangle(x, y, w, S(40));
                using (var b = new SolidBrush(Tema.Alpha(Tema.Consola, 200))) g.FillRectangle(b, rp);
                using (var p = new Pen(Tema.Alpha(Tema.Malva, 120), 1f)) g.DrawRectangle(p, rp);
                using (var b = new SolidBrush(Tema.Malva)) g.FillRectangle(b, rp.X, rp.Y, S(2), rp.Height);
                string t = "EXAMINANDO " + banco.ModeloActual.ToUpperInvariant();
                Tema.Tracking(g, t, Tema.Media(8f), Tema.Malva, rp.X + S(14), rp.Y + S(6), S(13), S(2));
                string sub = "modelo " + (banco.Hecho + 1) + " de " + banco.Cuantos +
                             (banco.PreguntaActual > 0 ? "  ·  pregunta " + banco.PreguntaActual + " de " + banco.PreguntasTotal : "") +
                             (banco.Fase.Length > 0 ? "  ·  " + banco.Fase : "");
                Tema.Texto_(g, sub, Tema.Fina(8.5f), Tema.Suave, new Rectangle(rp.X + S(14), rp.Y + S(21), rp.Width - S(200), S(14)),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
                double frac = banco.Cuantos <= 0 ? 0 :
                    (banco.Hecho + (banco.PreguntasTotal > 0 ? banco.PreguntaActual / (double)banco.PreguntasTotal : 0)) / banco.Cuantos;
                Tema.Segmentos(g, new RectangleF(rp.Right - S(180), rp.Y + S(17), S(166), S(6)), (float)frac, Tema.Malva, esc, 28);
                y = rp.Bottom + S(10);
            }
            else if (banco.Resultados.Count == 0)
            {
                Tema.Texto_(g,
                    "Todavía no hay exámenes.\n\n" +
                    "Andá al modo INVENTARIO, marcá con la casilla los modelos que quieras comparar y apretá EXAMINAR.\n" +
                    "El banco los levanta de a uno en el puerto " + (N.Cfg.Puerto + 1) + " (así no se cae el que estás usando), " +
                    "les toma " + Examen.Preguntas.Count + " preguntas con corrección automática y arma el ranking.",
                    Tema.Fina(9.5f), Tema.Suave, new Rectangle(x + S(4), y + S(10), Math.Min(w, S(620)), S(120)),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                y += S(120);
                Tema.Texto_(g, "El examen mide " + string.Join(" · ", Examen.Categorias) + ".  " +
                    (Node.Hay ? "El código se EJECUTA en Node contra casos ocultos." : "Sin Node instalado: el código se corrige a ojo."),
                    Tema.Fina(8.5f), Tema.Fantasma, new Rectangle(x + S(4), y, Math.Min(w, S(620)), S(40)),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                return;
            }

            // --- ranking
            yTabla = y;
            var cats = Examen.Categorias;
            var cab = new List<string> { "#", "modelo", "nivel", "puntaje" };
            cab.AddRange(cats);
            cab.AddRange(new[] { "mediana", "tok/s", "veredicto" });
            var nat = new int[cab.Count];
            var fc = Tema.Media(7f);
            var fN = Tema.Fina(9f);
            var fD = Tema.Mono(8f);
            for (int i = 0; i < cab.Count; i++) nat[i] = Tema.MedirTracking(g, cab[i].ToUpperInvariant(), fc, S(1)) + S(14);
            foreach (var r in banco.Resultados)
            {
                nat[1] = Math.Max(nat[1], Tema.Medir(g, r.Modelo, fN).Width + S(16));
                nat[cab.Count - 1] = Math.Max(nat[cab.Count - 1], Math.Min(S(300), Tema.Medir(g, r.Veredicto, Tema.Fina(8.5f)).Width + S(16)));
            }
            var min = new int[cab.Count];
            for (int i = 0; i < cab.Count; i++) min[i] = S(30);
            min[1] = S(120); min[cab.Count - 1] = S(120);
            var anchos = Tema.Repartir(nat, w, min);

            int xc = x;
            for (int i = 0; i < cab.Count; i++)
            {
                bool der = i >= 2 && i < cab.Count - 1;
                int wc = Tema.MedirTracking(g, cab[i].ToUpperInvariant(), fc, S(1));
                int xh = der ? xc + anchos[i] - S(8) - wc : xc + S(4);
                Tema.Tracking(g, cab[i].ToUpperInvariant(), fc, Tema.Apagado, xh, y, S(12), S(1));
                xc += anchos[i];
            }
            y += S(15);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 235), 1f)) g.DrawLine(p, x, y, x + w, y);
            y += S(4);

            // la tabla ocupa lo que necesita, no todo el hueco: abajo va el detalle y después los botones,
            // y con la tabla estirada el detalle terminaba escrito encima de ellos
            int altoDetalle = S(132);
            int alto = Math.Max(S(60), Math.Min(N.Banco.Resultados.Count * AltoFila + S(4), Height - y - altoDetalle - S(44)));
            g.SetClip(new Rectangle(x, y, w, alto));
            int yy = y - desplazExamen;
            for (int i = 0; i < banco.Resultados.Count; i++, yy += AltoFila)
            {
                var r = banco.Resultados[i];
                var rf = new Rectangle(x, yy, w, AltoFila - S(2));
                filasExamen.Add(rf);
                if (yy < y || yy + AltoFila > y + alto) continue;      // idem: nada de filas cortadas por abajo
                bool sel = examenElegido != null && examenElegido.Modelo == r.Modelo;
                bool hov = hoverExamen == i;
                var cn = r.Problema.Length > 0 ? Tema.Fantasma
                       : r.Nivel == "A" ? Tema.Salvia : r.Nivel == "B" ? Tema.Teal : r.Nivel == "C" ? Tema.Ambar : Tema.Rosa;
                if (sel || hov)
                    using (var b = new SolidBrush(sel ? Tema.Alpha(Tema.Consola, 235) : Tema.Alpha(Tema.Consola, 130))) g.FillRectangle(b, rf);
                if (sel) using (var b = new SolidBrush(Tema.Alpha(Tema.Malva, 220))) g.FillRectangle(b, rf.X, rf.Y, S(2), rf.Height);

                int cx = rf.X;
                Tema.Texto_(g, (i + 1).ToString("00"), Tema.Mono(7.5f), Tema.Fantasma, new Rectangle(cx + S(4), rf.Y, anchos[0] - S(8), rf.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                cx += anchos[0];
                Tema.Texto_(g, r.Modelo, fN, sel ? Tema.Texto : Tema.Alpha(Tema.Suave, 220), new Rectangle(cx + S(4), rf.Y, anchos[1] - S(10), rf.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                cx += anchos[1];
                Tema.Tracking(g, r.Nivel, Tema.Media(9f), cn, cx + anchos[2] - S(8) - Tema.MedirTracking(g, r.Nivel, Tema.Media(9f), S(1)), rf.Y, rf.Height, S(1));
                cx += anchos[2];
                Tema.Texto_(g, r.Problema.Length > 0 ? "—" : r.Puntaje.ToString("0.0"), Tema.MonoMedia(8.5f), r.Problema.Length > 0 ? Tema.Fantasma : cn,
                    new Rectangle(cx, rf.Y, anchos[3] - S(8), rf.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                cx += anchos[3];
                for (int k = 0; k < cats.Length; k++)
                {
                    double v = r.PorCategoria(cats[k]);
                    var cv = double.IsNaN(v) ? Tema.Fantasma : v >= 85 ? Tema.Salvia : v >= 60 ? Tema.Teal : v >= 35 ? Tema.Ambar : Tema.Rosa;
                    Tema.Texto_(g, double.IsNaN(v) ? "—" : v.ToString("0"), fD, cv, new Rectangle(cx, rf.Y, anchos[4 + k] - S(8), rf.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    cx += anchos[4 + k];
                }
                int iMed = 4 + cats.Length;
                Tema.Texto_(g, r.Problema.Length > 0 ? "—" : r.SegMediana.ToString("0.0") + " s", fD, Tema.Suave, new Rectangle(cx, rf.Y, anchos[iMed] - S(8), rf.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                cx += anchos[iMed];
                Tema.Texto_(g, r.Problema.Length > 0 ? "—" : r.TokPorSeg.ToString("0.0"), fD, Tema.Ambar, new Rectangle(cx, rf.Y, anchos[iMed + 1] - S(8), rf.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                cx += anchos[iMed + 1];
                Tema.Texto_(g, r.Veredicto, Tema.Fina(8.5f), r.Problema.Length > 0 ? Tema.Rosa : Tema.Alpha(Tema.Suave, 190),
                    new Rectangle(cx + S(4), rf.Y, Math.Max(S(40), rf.Right - cx - S(8)), rf.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            g.ResetClip();

            // --- detalle del elegido
            int yD = y + alto + S(10);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 220), 1f)) g.DrawLine(p, x, yD - S(6), x + w, yD - S(6));
            if (examenElegido == null) return;
            var e = examenElegido;
            Tema.Rotulo(g, "", "qué falló en " + e.Modelo, new Rectangle(x, yD, w, S(14)), Tema.Malva, esc,
                e.Aciertos + "/" + e.Total + " · carga " + (e.MsCarga / 1000.0).ToString("0.0") + " s · " + e.Cuando.ToString("dd/MM HH:mm"));
            yD += S(20);
            var fallan = e.Respuestas.Where(r => !r.Ok).OrderByDescending(r => 1 - r.Parcial).Take(6).ToList();
            if (fallan.Count == 0)
            {
                Tema.Texto_(g, "no falló ninguna: las " + e.Total + " preguntas salieron bien", Tema.Fina(9f), Tema.Salvia, new Rectangle(x + S(4), yD, w, S(16)));
                return;
            }
            foreach (var r in fallan)
            {
                var cc = r.Parcial > 0 ? Tema.Ambar : Tema.Rosa;
                Tema.Diodo(g, x + S(7), yD + S(8), S(4), cc, true, 0.4f);
                Tema.Tracking(g, r.Clave.ToUpperInvariant(), Tema.Media(7f), cc, x + S(16), yD, S(14), S(1));
                Tema.Texto_(g, r.Categoria, Tema.Fina(8f), Tema.Fantasma, new Rectangle(x + S(58), yD, S(80), S(15)));
                string q = r.Porque.Length > 0 ? r.Porque : "no pasó";
                if (r.Parcial > 0) q = "parcial " + (r.Parcial * 100).ToString("0") + "% · " + q;
                Tema.Texto_(g, q, Tema.Fina(8.5f), Tema.Alpha(Tema.Suave, 210), new Rectangle(x + S(140), yD, w - S(150), S(15)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                yD += S(16);
            }
        }

        // ---------------- modo descargar

        void Descargar(Graphics g, int x, int y, int w)
        {
            int anchoIzq = Math.Min(S(300), w / 2 - S(10));
            Tema.Rotulo(g, "", "catálogo", new Rectangle(x, y, anchoIzq, S(14)), Tema.Cielo, esc);
            Tema.Rotulo(g, "", repoElegido != null ? "archivos de " + repoElegido.Nombre : "archivos",
                new Rectangle(x + anchoIzq + S(16), y, w - anchoIzq - S(16), S(14)), Tema.Crema, esc,
                listando ? "leyendo la API…" : archivos.Count > 0 ? archivos.Count + " gguf" : "");
            y += S(22);

            int alto = Height - y - S(96);
            var fN = Tema.Fina(9f);
            var fD = Tema.Mono(8f);

            // --- repos
            int yy = y;
            for (int i = 0; i < Descargas.Catalogo.Count; i++, yy += S(34))
            {
                var r = Descargas.Catalogo[i];
                var rf = new Rectangle(x, yy, anchoIzq, S(32));
                filasRepo.Add(rf);
                bool sel = repoElegido != null && repoElegido.Ruta == r.Ruta;
                bool hov = hoverRepo == i;
                if (sel || hov)
                    using (var b = new SolidBrush(sel ? Tema.Alpha(Tema.Consola, 235) : Tema.Alpha(Tema.Consola, 130))) g.FillRectangle(b, rf);
                if (sel) using (var b = new SolidBrush(Tema.Alpha(Tema.Cielo, 220))) g.FillRectangle(b, rf.X, rf.Y, S(2), rf.Height);
                Tema.Texto_(g, r.Nombre, fN, sel ? Tema.Texto : Tema.Alpha(Tema.Suave, 215), new Rectangle(rf.X + S(10), rf.Y + S(2), rf.Width - S(70), S(15)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Tema.Texto_(g, "~" + r.GbAprox.ToString("0.0") + " GB", fD, Tema.Alpha(Tema.Apagado, 200), new Rectangle(rf.Right - S(64), rf.Y + S(2), S(58), S(15)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                Tema.Texto_(g, r.Nota, Tema.Fina(8f), Tema.Fantasma, new Rectangle(rf.X + S(10), rf.Y + S(16), rf.Width - S(16), S(14)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            // --- archivos del repo
            int xa = x + anchoIzq + S(16), wa = w - anchoIzq - S(16);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 200), 1f)) g.DrawLine(p, xa - S(8), y - S(14), xa - S(8), y + alto);
            if (listando)
                Tema.Texto_(g, "preguntándole a huggingface.co qué archivos tiene…", Tema.Fina(9f), Tema.Apagado, new Rectangle(xa, y + S(6), wa, S(20)));
            else if (problemaRepo.Length > 0)
                Tema.Texto_(g, problemaRepo, Tema.Fina(9f), Tema.Rosa, new Rectangle(xa, y + S(6), wa, S(40)), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
            else
            {
                double libre, total;
                Win32.Memoria(out libre, out total);
                yy = y;
                var yaTengo = new HashSet<string>(modelos.Select(m => m.Nombre), StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < archivos.Count && yy < y + alto; i++, yy += S(26))
                {
                    var a = archivos[i];
                    var rf = new Rectangle(xa, yy, wa, S(24));
                    filasArchivo.Add(rf);
                    bool sel = archivoElegido != null && archivoElegido.Ruta == a.Ruta;
                    bool hov = hoverArchivo == i;
                    bool tengo = yaTengo.Contains(Path.GetFileNameWithoutExtension(a.Nombre));
                    if (sel || hov)
                        using (var b = new SolidBrush(sel ? Tema.Alpha(Tema.Consola, 235) : Tema.Alpha(Tema.Consola, 130))) g.FillRectangle(b, rf);
                    if (sel) using (var b = new SolidBrush(Tema.Alpha(Tema.Cielo, 220))) g.FillRectangle(b, rf.X, rf.Y, S(2), rf.Height);
                    var ct = tengo ? Tema.Fantasma : sel ? Tema.Texto : Tema.Alpha(Tema.Suave, 215);
                    Tema.Texto_(g, a.Nombre, fN, ct, new Rectangle(rf.X + S(10), rf.Y, rf.Width - S(190), rf.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    Tema.Texto_(g, a.Cuant, fD, Tema.Alpha(Tema.Crema, 190), new Rectangle(rf.Right - S(178), rf.Y, S(66), rf.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    Tema.Texto_(g, a.Gb.ToString("0.00") + " GB", fD, a.Gb + 1 < libre ? Tema.Suave : Tema.Ambar, new Rectangle(rf.Right - S(112), rf.Y, S(62), rf.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    if (tengo) Tema.Tracking(g, "YA ESTÁ", Tema.Media(7f), Tema.Salvia, rf.Right - S(44), rf.Y, rf.Height, S(1));
                    else if (a.Sha256.Length == 64) Tema.Tracking(g, "SHA", Tema.Media(7f), Tema.Alpha(Tema.Fantasma, 220), rf.Right - S(30), rf.Y, rf.Height, S(1));
                }
                if (archivos.Count == 0)
                    Tema.Texto_(g, "elegí un repo de la izquierda", Tema.Fina(9f), Tema.Fantasma, new Rectangle(xa, y + S(6), wa, S(20)));
            }

            // --- progreso de la descarga
            int yP = Height - S(86);
            using (var p = new Pen(Tema.Alpha(Tema.Filete, 220), 1f)) g.DrawLine(p, x, yP - S(6), x + w, yP - S(6));
            var d2 = N.Bajadas;
            if (d2.Bajando && d2.Actual != null)
            {
                Tema.Tracking(g, "BAJANDO", Tema.Media(8f), Tema.Cielo, x + S(4), yP, S(14), S(2));
                Tema.Texto_(g, d2.Actual.Nombre, Tema.Mono(8f), Tema.Texto, new Rectangle(x + S(84), yP, w - S(300), S(14)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                string der = Tema.Bytes(d2.Hechos) + " de " + Tema.Bytes(d2.Totales) +
                             (d2.BytesPorSeg > 1024 ? "  ·  " + Tema.Bytes(d2.BytesPorSeg) + "/s" : "") +
                             (d2.Falta.TotalSeconds > 1 ? "  ·  faltan " + (d2.Falta.TotalMinutes >= 1 ? ((int)d2.Falta.TotalMinutes) + " min" : ((int)d2.Falta.TotalSeconds) + " s") : "");
                Tema.Texto_(g, der, Tema.Mono(7.5f), Tema.Suave, new Rectangle(x + w - S(300), yP, S(300), S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                Tema.Segmentos(g, new RectangleF(x + S(4), yP + S(20), w - S(8), S(7)), (float)d2.Fraccion, Tema.Cielo, esc, 48);
                Tema.Texto_(g, d2.Estado, Tema.Fina(8f), Tema.Alpha(Tema.Apagado, 220), new Rectangle(x + S(4), yP + S(30), w - S(8), S(14)));
            }
            else
            {
                string s = d2.Estado.Length > 0 ? d2.Estado : "nada en curso";
                Tema.Texto_(g, s, Tema.Fina(9f), d2.Estado.IndexOf("listo", StringComparison.OrdinalIgnoreCase) >= 0 ? Tema.Salvia : Tema.Fantasma,
                    new Rectangle(x + S(4), yP, w - S(8), S(16)));
                if (archivoElegido != null)
                    Tema.Texto_(g, "elegido: " + archivoElegido.Nombre + "  ·  " + Tema.Bytes(archivoElegido.Bytes) + "  →  " + CarpetaDestino,
                        Tema.Fina(8.5f), Tema.Alpha(Tema.Suave, 200), new Rectangle(x + S(4), yP + S(18), w - S(8), S(16)),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
