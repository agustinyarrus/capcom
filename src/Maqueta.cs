using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Capcom
{
    // ------------------------------------------------------------------ piezas de dibujo

    internal abstract class Pieza
    {
        public Rectangle R;
        public abstract void Dibujar(Graphics g, int dx, int dy);
    }

    internal sealed class PTexto : Pieza
    {
        public string S; public Font F; public Color C; public bool Subrayado, Tachado;
        public TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.Top;
        public override void Dibujar(Graphics g, int dx, int dy)
        {
            var r = new Rectangle(R.X + dx, R.Y + dy, R.Width, R.Height);
            Tema.Texto_(g, S, F, C, r, Flags);
            if (Subrayado || Tachado)
            {
                int y = Tachado ? r.Y + r.Height / 2 : r.Bottom - 1;
                using (var p = new Pen(Tema.Alpha(C, Tachado ? 190 : 120), 1f)) g.DrawLine(p, r.X, y, r.X + R.Width, y);
            }
        }
    }

    internal sealed class PRect : Pieza
    {
        public Color Fondo, Borde; public int Radio;
        public override void Dibujar(Graphics g, int dx, int dy)
        {
            var r = new RectangleF(R.X + dx + 0.5f, R.Y + dy + 0.5f, Math.Max(1, R.Width - 1), Math.Max(1, R.Height - 1));
            if (Radio > 0) Tema.Placa(g, r, Radio, Fondo, Borde);
            else
            {
                using (var b = new SolidBrush(Fondo)) g.FillRectangle(b, r);
                if (Borde.A > 0) using (var p = new Pen(Borde, 1f)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }
        }
    }

    internal sealed class PLinea : Pieza
    {
        public Color C; public float Grosor = 1f; public bool Marcas; public float Esc = 1f;
        public override void Dibujar(Graphics g, int dx, int dy)
        {
            int x1 = R.X + dx, y1 = R.Y + dy, x2 = R.Right + dx, y2 = R.Bottom + dy;
            using (var p = new Pen(C, Grosor)) g.DrawLine(p, x1, y1, x2, y2);
            if (Marcas)
            {
                int paso = Dpi.S(Esc, 14), alto = Dpi.S(Esc, 3);
                using (var p = new Pen(Tema.Alpha(C, 200), 1f))
                    for (int x = x1; x <= x2; x += paso) g.DrawLine(p, x, y1 - alto, x, y1);
            }
        }
    }

    internal sealed class PPunto : Pieza
    {
        public Color C; public bool Hueco;
        public override void Dibujar(Graphics g, int dx, int dy)
        {
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(R.X + dx, R.Y + dy, R.Width, R.Height);
            if (Hueco) { using (var p = new Pen(C, 1f)) g.DrawEllipse(p, r); }
            else { using (var b = new SolidBrush(C)) g.FillEllipse(b, r); }
            g.SmoothingMode = old;
        }
    }

    internal sealed class ZonaCodigo { public Rectangle R; public string Texto = ""; public string Lenguaje = ""; }
    internal sealed class ZonaEnlace { public Rectangle R; public string Url = ""; }

    /// <summary>El resultado de maquetar: piezas ya posicionadas + las zonas con las que se puede interactuar.</summary>
    internal sealed class Maqueta
    {
        public int Ancho, Alto;
        public List<Pieza> Piezas = new List<Pieza>();
        public List<ZonaCodigo> Codigos = new List<ZonaCodigo>();
        public List<ZonaEnlace> Enlaces = new List<ZonaEnlace>();

        public void Dibujar(Graphics g, int dx, int dy, int desdeY, int hastaY)
        {
            foreach (var p in Piezas)
            {
                if (p.R.Bottom < desdeY || p.R.Y > hastaY) continue;   // fuera de la ventana visible
                p.Dibujar(g, dx, dy);
            }
        }
    }

    /// <summary>
    /// El maquetador: convierte los bloques de Markdown en piezas con coordenadas. Se ejecuta una vez por
    /// (ancho × versión del texto) y el resultado se cachea, así el repintado durante el streaming es barato
    /// aunque el hilo tenga cincuenta mensajes.
    ///
    /// Regla: acá NO se dibuja nada. Sólo se miden y se ubican cosas. Eso es lo que permite que el mismo
    /// maquetado sirva para la pantalla y para la foto de cualquier tamaño.
    /// </summary>
    internal static class Maquetador
    {
        static Bitmap lienzo;
        static Graphics medidor;
        static readonly object candado = new object();

        /// <summary>Una superficie mínima sólo para medir: nunca depende de un Graphics prestado que puede morir.</summary>
        public static Graphics Medidor
        {
            get
            {
                lock (candado)
                {
                    if (medidor == null)
                    {
                        lienzo = new Bitmap(1, 1);
                        medidor = Graphics.FromImage(lienzo);
                        medidor.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    }
                    return medidor;
                }
            }
        }

        public sealed class Opciones
        {
            public float Esc = 1f;
            public float PtCuerpo = 10.5f;
            public float PtCodigo = 9.5f;
            public Color Texto = Tema.Texto;
            public Color Suave = Tema.Suave;
            public Color Acento = Tema.Malva;
            public bool Compacto;
        }

        public static Maqueta Maquetar(List<Bloque> bloques, int ancho, Opciones o)
        {
            var m = new Maqueta { Ancho = ancho };
            var g = Medidor;
            int S(int px) => Dpi.S(o.Esc, px);
            int y = 0;
            bool primero = true;

            foreach (var b in bloques)
            {
                if (!primero) y += EspacioAntes(b, o);
                primero = false;

                switch (b.Tipo)
                {
                    case TipoBloque.Titulo: y = Titulo(m, b, ancho, y, o); break;
                    case TipoBloque.Parrafo: y = Parrafo(m, b, ancho, y, o); break;
                    case TipoBloque.Lista: y = Lista(m, b, ancho, y, o); break;
                    case TipoBloque.Cita: y = Cita(m, b, ancho, y, o); break;
                    case TipoBloque.Codigo: y = Codigo(m, b, ancho, y, o); break;
                    case TipoBloque.Tabla: y = Tabla(m, b, ancho, y, o); break;
                    case TipoBloque.Regla:
                        y += S(6);
                        m.Piezas.Add(new PLinea { R = new Rectangle(0, y, ancho, 0), C = Tema.Filete, Marcas = true, Esc = o.Esc });
                        y += S(7);
                        break;
                }
            }
            m.Alto = y;
            return m;
        }

        static int EspacioAntes(Bloque b, Opciones o)
        {
            int S(int px) => Dpi.S(o.Esc, px);
            switch (b.Tipo)
            {
                case TipoBloque.Titulo: return S(o.Compacto ? 8 : 13);
                case TipoBloque.Codigo: return S(o.Compacto ? 6 : 9);
                case TipoBloque.Tabla: return S(o.Compacto ? 6 : 9);
                default: return S(o.Compacto ? 4 : 7);
            }
        }

        // ------------------------------------------------------------------ bloques

        static int Titulo(Maqueta m, Bloque b, int ancho, int y, Opciones o)
        {
            int S(int px) => Dpi.S(o.Esc, px);
            float pt = b.Nivel <= 1 ? o.PtCuerpo + 3f : b.Nivel == 2 ? o.PtCuerpo + 1.6f : o.PtCuerpo + 0.6f;
            var f = b.Nivel <= 2 ? Tema.Media(pt) : Tema.Fina(pt);
            var color = b.Nivel <= 2 ? o.Texto : Tema.Alpha(o.Texto, 225);
            // el número de sección al costado: es lo que lo vuelve un rótulo de consola y no un h2 cualquiera
            if (b.Nivel <= 2)
            {
                var fp = Tema.MonoMedia(o.PtCuerpo - 3f);
                string marca = b.Nivel == 1 ? "§" : "··";
                int wm = Tema.Medir(Medidor, marca, fp).Width;
                m.Piezas.Add(new PTexto { S = marca, F = fp, C = Tema.Alpha(o.Acento, 170), R = new Rectangle(0, y + S(3), wm + S(2), S(16)) });
            }
            int x0 = b.Nivel <= 2 ? S(16) : 0;
            y = Envolver(m, b.Lineas[0].Trozos, x0, y, ancho - x0, o, f, color, S((int)(pt * 1.7f)));
            if (b.Nivel <= 2)
            {
                y += S(3);
                m.Piezas.Add(new PLinea { R = new Rectangle(0, y, ancho, 0), C = Tema.Alpha(Tema.Filete, 200) });
                y += S(4);
            }
            return y;
        }

        static int Parrafo(Maqueta m, Bloque b, int ancho, int y, Opciones o)
        {
            var f = Tema.Fina(o.PtCuerpo);
            int alto = Alto(f, o);
            foreach (var l in b.Lineas) y = Envolver(m, l.Trozos, 0, y, ancho, o, f, o.Texto, alto);
            return y;
        }

        static int Lista(Maqueta m, Bloque b, int ancho, int y, Opciones o)
        {
            int S(int px) => Dpi.S(o.Esc, px);
            var f = Tema.Fina(o.PtCuerpo);
            var fn = Tema.MonoMedia(o.PtCuerpo - 1.5f);
            int alto = Alto(f, o);
            foreach (var l in b.Lineas)
            {
                int x0 = S(4) + l.Sangria * S(15);
                bool numerada = l.Viñeta.Length > 0 && char.IsDigit(l.Viñeta[0]);
                int sangriaTexto;
                if (numerada)
                {
                    int w = Tema.Medir(Medidor, l.Viñeta, fn).Width;
                    m.Piezas.Add(new PTexto { S = l.Viñeta, F = fn, C = Tema.Alpha(o.Acento, 210), R = new Rectangle(x0, y + S(2), w + S(2), alto) });
                    sangriaTexto = x0 + w + S(7);
                }
                else
                {
                    int d = S(4);
                    m.Piezas.Add(new PPunto { R = new Rectangle(x0 + S(2), y + alto / 2 - d / 2, d, d), C = Tema.Alpha(o.Acento, 200), Hueco = l.Sangria > 0 });
                    sangriaTexto = x0 + S(14);
                }
                y = Envolver(m, l.Trozos, sangriaTexto, y, ancho - sangriaTexto, o, f, o.Texto, alto);
                y += S(2);
            }
            return y;
        }

        static readonly Dictionary<string, Color> ColorAlerta = new Dictionary<string, Color>
        {
            ["NOTE"] = Tema.Cielo, ["TIP"] = Tema.Salvia, ["IMPORTANT"] = Tema.Malva,
            ["WARNING"] = Tema.Ambar, ["CAUTION"] = Tema.Rosa,
        };
        static readonly Dictionary<string, string> TextoAlerta = new Dictionary<string, string>
        {
            ["NOTE"] = "NOTA", ["TIP"] = "DATO", ["IMPORTANT"] = "IMPORTANTE",
            ["WARNING"] = "ATENCIÓN", ["CAUTION"] = "CUIDADO",
        };

        static int Cita(Maqueta m, Bloque b, int ancho, int y, Opciones o)
        {
            int S(int px) => Dpi.S(o.Esc, px);
            Color c = b.Alerta.Length > 0 && ColorAlerta.ContainsKey(b.Alerta) ? ColorAlerta[b.Alerta] : Tema.Suave;
            int y0 = y;
            int marca = m.Piezas.Count;          // todo lo que se agregue después va ARRIBA del fondo
            int pad = S(11), x0 = S(13);
            y += S(7);
            if (b.Alerta.Length > 0)
            {
                var fa = Tema.Media(o.PtCuerpo - 2.5f);
                string et = TextoAlerta.ContainsKey(b.Alerta) ? TextoAlerta[b.Alerta] : b.Alerta;
                int w = Tema.MedirTracking(Medidor, et, fa, S(2));
                m.Piezas.Add(new PTextoTracking { S = et, F = fa, C = c, Espacio = S(2), R = new Rectangle(x0 + pad, y, w + S(4), S(14)) });
                y += S(17);
            }
            var f = Tema.Fina(o.PtCuerpo);
            int alto = Alto(f, o);
            foreach (var l in b.Lineas)
                y = Envolver(m, l.Trozos, x0 + pad, y, ancho - x0 - pad * 2, o, f, b.Alerta.Length > 0 ? o.Texto : Tema.Alpha(o.Texto, 210), alto);
            y += S(8);
            // el fondo y el riel se INSERTAN en la marca para quedar por debajo del texto que ya se emitió
            m.Piezas.Insert(marca, new PRect
            {
                R = new Rectangle(0, y0, ancho, y - y0),
                Fondo = Tema.Alpha(c, b.Alerta.Length > 0 ? 16 : 9),
                Borde = Color.Transparent,
                Radio = 0
            });
            m.Piezas.Insert(marca + 1, new PRect
            {
                R = new Rectangle(0, y0, S(2), y - y0),
                Fondo = Tema.Alpha(c, 165),
                Borde = Color.Transparent
            });
            return y;
        }

        static int Codigo(Maqueta m, Bloque b, int ancho, int y, Opciones o)
        {
            int S(int px) => Dpi.S(o.Esc, px);
            var f = Tema.Mono(o.PtCodigo);
            var fEt = Tema.Media(o.PtCodigo - 2f);
            int altoLinea = Math.Max(S(13), Tema.Medir(Medidor, "Xy", f).Height + S(3));
            int pad = S(11);
            int y0 = y;
            int altoCab = S(19);

            string lg = b.Lenguaje.Length > 0 ? b.Lenguaje : Resaltador.Adivinar(b.Codigo);
            string lgVis = Resaltador.Normalizar(lg);

            // --- el cuerpo se mide primero para saber el alto de la placa
            int anchoTexto = ancho - pad * 2 - S(28);      // deja lugar para el canalón de números
            var visuales = new List<List<Token>>();
            var st = new Resaltador.Estado();
            foreach (var ln in b.Codigo)
            {
                var toks = Resaltador.Tokenizar(ln ?? "", lg, ref st);
                foreach (var parte in PartirCodigo(toks, anchoTexto, f)) visuales.Add(parte);
            }
            int altoCuerpo = Math.Max(altoLinea, visuales.Count * altoLinea);
            int alto = altoCab + S(7) + altoCuerpo + S(9);

            m.Piezas.Add(new PRect { R = new Rectangle(0, y0, ancho, alto), Fondo = Tema.Hex("#0a0b11"), Borde = Tema.Alpha(Tema.Filete, 220), Radio = S(4) });
            m.Piezas.Add(new PLinea { R = new Rectangle(0, y0 + altoCab, ancho, 0), C = Tema.Alpha(Tema.Filete, 190) });
            // canalón de números
            m.Piezas.Add(new PLinea { R = new Rectangle(pad + S(20), y0 + altoCab + S(4), 0, altoCuerpo - S(2)), C = Tema.Alpha(Tema.Filete, 150) });

            string etq = (lgVis.Length > 0 ? lgVis : lg.Length > 0 ? lg : "texto").ToUpperInvariant();
            m.Piezas.Add(new PTextoTracking { S = etq, F = fEt, C = Tema.Alpha(o.Acento, 205), Espacio = S(2), R = new Rectangle(pad, y0 + S(3), ancho, altoCab - S(4)) });
            string der = b.Codigo.Count + (b.Codigo.Count == 1 ? " línea" : " líneas");
            var fd = Tema.Mono(o.PtCodigo - 2f);
            int wd = Tema.Medir(Medidor, der, fd).Width;
            m.Piezas.Add(new PTexto { S = der, F = fd, C = Tema.Apagado, R = new Rectangle(ancho - wd - pad, y0 + S(4), wd + S(2), altoCab - S(4)) });

            int yy = y0 + altoCab + S(6);
            int nro = 0;
            var fNum = Tema.Mono(o.PtCodigo - 2f);
            foreach (var linea in visuales)
            {
                bool cont = linea.Count > 0 && linea[0].Texto == Marcador;
                if (cont) linea.RemoveAt(0);
                if (!cont) nro++;
                string etqN = cont ? "↪" : nro.ToString();
                int wn = Tema.Medir(Medidor, etqN, fNum).Width;
                m.Piezas.Add(new PTexto
                {
                    S = etqN,
                    F = fNum,
                    C = cont ? Tema.Fantasma : Tema.Alpha(Tema.Apagado, 150),
                    R = new Rectangle(pad + S(14) - wn, yy + S(1), wn + S(2), altoLinea),
                });
                int x = pad + S(28);
                foreach (var tk in linea)
                {
                    if (tk.Texto.Length == 0) continue;
                    int w = Tema.Medir(Medidor, tk.Texto, f).Width;
                    if (tk.Texto.Trim().Length > 0)
                        m.Piezas.Add(new PTexto { S = tk.Texto, F = f, C = Resaltador.ColorDe(tk.Clase), R = new Rectangle(x, yy, w + S(2), altoLinea) });
                    x += w;
                }
                yy += altoLinea;
            }

            m.Codigos.Add(new ZonaCodigo { R = new Rectangle(0, y0, ancho, alto), Texto = string.Join(Environment.NewLine, b.Codigo), Lenguaje = lgVis });
            return y0 + alto;
        }

        /// <summary>
        /// Parte una línea de código larga en varias visuales, cortando por tokens y, si un token solo no entra,
        /// por caracteres. Nada se pierde de vista: preferimos una continuación marcada con ↪ antes que un
        /// scroll horizontal que esconde la mitad del comando.
        /// </summary>
        const string Marcador = " ";
        static List<List<Token>> PartirCodigo(List<Token> toks, int ancho, Font f)
        {
            var res = new List<List<Token>>();
            var actual = new List<Token>();
            int x = 0;
            bool Vacia() => actual.Count == 0 || (actual.Count == 1 && actual[0].Texto == Marcador);
            foreach (var t in toks)
            {
                string resto = t.Texto;
                while (resto.Length > 0)
                {
                    int disponible = ancho - x;
                    int w = Tema.Medir(Medidor, resto, f).Width;
                    if (w <= disponible) { actual.Add(new Token(resto, t.Clase)); x += w; break; }

                    int cabe = CuantosCaben(resto, f, disponible);
                    if (cabe <= 0)
                    {
                        if (!Vacia())
                        {
                            // todavía no empezamos esta visual: cortar acá y seguir en la de abajo
                            res.Add(actual);
                            actual = new List<Token> { new Token(Marcador, Clase.Normal) };
                            x = 0;
                            continue;
                        }
                        cabe = 1;                  // 🚨 último recurso: garantiza que el bucle avance siempre
                    }
                    actual.Add(new Token(resto.Substring(0, cabe), t.Clase));
                    resto = resto.Substring(cabe);
                    res.Add(actual);
                    actual = new List<Token> { new Token(Marcador, Clase.Normal) };
                    x = 0;
                }
            }
            res.Add(actual);
            return res;
        }

        static int CuantosCaben(string s, Font f, int ancho)
        {
            if (ancho <= 0) return 0;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Tema.Medir(Medidor, s.Substring(0, mid), f).Width <= ancho) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        static int Tabla(Maqueta m, Bloque b, int ancho, int y, Opciones o)
        {
            int S(int px) => Dpi.S(o.Esc, px);
            if (b.Filas.Count == 0) return y;
            int cols = b.Filas.Max(f => f.Count);
            if (cols == 0) return y;
            var fCab = Tema.Media(o.PtCuerpo - 2f);
            var fCel = Tema.Fina(o.PtCuerpo - 0.5f);
            int pad = S(9);

            // ancho natural de cada columna, después repartido con techo
            var nat = new int[cols];
            for (int c = 0; c < cols; c++)
                foreach (var fila in b.Filas)
                    if (c < fila.Count)
                        nat[c] = Math.Max(nat[c], Tema.Medir(Medidor, fila[c].Plano, fCel).Width + pad * 2);
            // la misma regla que en todas las pestañas: proporcional al contenido, llenando el ancho entero
            int disponible = ancho - S(2);
            var minimos = new int[cols];
            for (int c = 0; c < cols; c++) minimos[c] = S(58);
            var anchoCol = Tema.Repartir(nat, disponible, minimos);

            int y0 = y;
            int altoCel = Alto(fCel, o);
            for (int r = 0; r < b.Filas.Count; r++)
            {
                var fila = b.Filas[r];
                bool cab = r == 0 && b.Encabezado;
                int x = 0;
                int yFila = y;
                int altoFila = 0;
                for (int c = 0; c < cols; c++)
                {
                    var celda = c < fila.Count ? fila[c] : new LineaRica();
                    int ancC = anchoCol[c] - pad * 2;
                    int yc;
                    if (cab)
                    {
                        int w = Tema.MedirTracking(Medidor, celda.Plano.ToUpperInvariant(), fCab, S(1));
                        m.Piezas.Add(new PTextoTracking
                        {
                            S = celda.Plano.ToUpperInvariant(), F = fCab, C = Tema.Suave, Espacio = S(1),
                            R = new Rectangle(x + pad, y + S(5), Math.Min(w + S(3), ancC), altoCel)
                        });
                        yc = y + S(5) + altoCel;
                    }
                    else yc = Envolver(m, celda.Trozos, x + pad, y + S(4), ancC, o, fCel, o.Texto, altoCel);
                    altoFila = Math.Max(altoFila, yc - y);
                    x += anchoCol[c];
                }
                y += altoFila + S(7);
                if (cab)
                {
                    m.Piezas.Add(new PLinea { R = new Rectangle(0, y - S(3), ancho, 0), C = Tema.Alpha(o.Acento, 90) });
                }
                else if (r < b.Filas.Count - 1)
                {
                    m.Piezas.Add(new PLinea { R = new Rectangle(0, y - S(3), ancho, 0), C = Tema.Alpha(Tema.Filete, 150) });
                }
            }
            // reglas verticales tenues entre columnas
            int xv = 0;
            for (int c = 0; c < cols - 1; c++)
            {
                xv += anchoCol[c];
                m.Piezas.Insert(0, new PLinea { R = new Rectangle(xv, y0 + S(2), 0, y - y0 - S(8)), C = Tema.Alpha(Tema.Filete, 110) });
            }
            return y;
        }

        // ------------------------------------------------------------------ envoltura de texto con formato

        static int Alto(Font f, Opciones o) => (int)Math.Round(Tema.Medir(Medidor, "Xygq", f).Height * 1.32f);

        /// <summary>
        /// Envuelve una línea rica dentro de `ancho`, emitiendo las piezas de texto ya ubicadas.
        /// Corta por palabras; si una palabra sola no entra (una URL larga), la parte por caracteres.
        /// </summary>
        static int Envolver(Maqueta m, List<Trozo> trozos, int x0, int y, int ancho, Opciones o, Font baseF, Color color, int altoLinea)
        {
            int S(int px) => Dpi.S(o.Esc, px);
            if (ancho < S(40)) ancho = S(40);
            if (trozos == null || trozos.Count == 0) return y + altoLinea;
            var g = Medidor;
            int x = x0;
            bool algo = false;

            foreach (var t in trozos)
            {
                if (t.Texto.Length == 0) continue;
                Font f = Fuente(t, baseF, o);
                Color c = t.Codigo ? Tema.Crema : t.Enlace.Length > 0 ? Tema.Cielo : t.Negrita ? Tema.Mezcla(color, Color.White, 0.22f) : color;

                foreach (var palabra in Palabras(t.Texto))
                {
                    string p = palabra;
                    if (p == "\n") { x = x0; y += altoLinea; continue; }
                    int w = Tema.Medir(g, p, f).Width;
                    if (x + w > x0 + ancho && x > x0)
                    {
                        x = x0; y += altoLinea;
                        if (p == " ") continue;                   // no arrastrar el espacio al principio del renglón
                    }
                    if (w > ancho)
                    {
                        // palabra imposible: cortarla por caracteres
                        while (p.Length > 0)
                        {
                            int cabe = CuantosCaben(p, f, x0 + ancho - x);
                            if (cabe <= 0) { x = x0; y += altoLinea; cabe = CuantosCaben(p, f, ancho); if (cabe <= 0) break; }
                            string parte = p.Substring(0, cabe);
                            int wp = Tema.Medir(g, parte, f).Width;
                            Emitir(m, t, parte, f, c, x, y, wp, altoLinea, o);
                            algo = true;
                            x += wp;
                            p = p.Substring(cabe);
                        }
                        continue;
                    }
                    if (p == " " && x == x0 && algo) continue;
                    Emitir(m, t, p, f, c, x, y, w, altoLinea, o);
                    algo = true;
                    x += w;
                }
            }
            return y + altoLinea;
        }

        static void Emitir(Maqueta m, Trozo t, string texto, Font f, Color c, int x, int y, int w, int alto, Opciones o)
        {
            if (texto.Trim().Length == 0 && !t.Codigo) return;
            int S(int px) => Dpi.S(o.Esc, px);
            if (t.Codigo)
                m.Piezas.Add(new PRect
                {
                    R = new Rectangle(x - S(2), y + S(1), w + S(4), alto - S(3)),
                    Fondo = Tema.Hex("#12141c"), Borde = Tema.Alpha(Tema.Filete, 160), Radio = S(2)
                });
            m.Piezas.Add(new PTexto { S = texto, F = f, C = c, R = new Rectangle(x, y, w + S(3), alto), Subrayado = t.Enlace.Length > 0, Tachado = t.Tachado });
            if (t.Enlace.Length > 0) m.Enlaces.Add(new ZonaEnlace { R = new Rectangle(x, y, w, alto), Url = t.Enlace });
        }

        static Font Fuente(Trozo t, Font baseF, Opciones o)
        {
            if (t.Codigo) return Tema.Mono(o.PtCodigo);
            if (t.Negrita && t.Italica) return Tema.Media(o.PtCuerpo);
            if (t.Negrita) return Tema.Media(o.PtCuerpo);
            if (t.Italica) return Tema.Italica(o.PtCuerpo);
            return baseF;
        }

        /// <summary>Parte en palabras conservando los espacios (y marcando los saltos de línea explícitos).</summary>
        static IEnumerable<string> Palabras(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\n') { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } yield return "\n"; continue; }
                if (c == ' ') { sb.Append(c); yield return sb.ToString(); sb.Clear(); continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }

    /// <summary>Texto con tracking, ya ubicado. Se usa para los rótulos en versalita dentro del contenido.</summary>
    internal sealed class PTextoTracking : Pieza
    {
        public string S; public Font F; public Color C; public float Espacio;
        public override void Dibujar(Graphics g, int dx, int dy)
        {
            Tema.Tracking(g, S, F, C, R.X + dx, R.Y + dy, R.Height, Espacio);
        }
    }
}
