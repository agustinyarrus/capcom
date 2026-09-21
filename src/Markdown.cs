using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Capcom
{
    internal enum TipoBloque { Parrafo, Titulo, Lista, Cita, Codigo, Regla, Tabla }

    /// <summary>Un tramo de texto con formato dentro de una línea.</summary>
    internal sealed class Trozo
    {
        public string Texto = "";
        public bool Negrita, Italica, Codigo, Tachado;
        public string Enlace = "";
        public Trozo Clon(string t) => new Trozo { Texto = t, Negrita = Negrita, Italica = Italica, Codigo = Codigo, Tachado = Tachado, Enlace = Enlace };
        public override string ToString() => Texto;
    }

    /// <summary>Una línea de contenido: la unidad que se envuelve y se dibuja.</summary>
    internal sealed class LineaRica
    {
        public List<Trozo> Trozos = new List<Trozo>();
        public int Sangria;          // niveles de anidamiento en listas
        public string Viñeta = "";   // "•" o "3." según la lista
        public string Plano => string.Concat(Trozos.Select(t => t.Texto));
    }

    internal sealed class Bloque
    {
        public TipoBloque Tipo = TipoBloque.Parrafo;
        public int Nivel;                       // título: 1-6 · lista: no se usa
        public List<LineaRica> Lineas = new List<LineaRica>();
        public string Lenguaje = "";            // bloque de código
        public List<string> Codigo = new List<string>();
        public string Alerta = "";              // NOTE / TIP / IMPORTANT / WARNING / CAUTION
        public List<List<LineaRica>> Filas = new List<List<LineaRica>>();   // tabla: filas × celdas
        public bool Encabezado;                 // la tabla trae fila de encabezado
    }

    /// <summary>
    /// Parser de Markdown propio, del subconjunto que un chat usa de verdad: títulos, párrafos, listas, citas
    /// (con alertas de GitHub), código con lenguaje, tablas y reglas. Nada de HTML crudo — lo que entra se
    /// dibuja, no se ejecuta.
    /// </summary>
    internal static class Markdown
    {
        static readonly Regex ReTitulo = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex ReRegla = new Regex(@"^\s*([-*_])\s*\1\s*\1[\s\-*_]*$", RegexOptions.Compiled);
        static readonly Regex ReVinieta = new Regex(@"^(\s*)([-*+])\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex ReNumerada = new Regex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex ReAlerta = new Regex(@"^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReSepTabla = new Regex(@"^\s*\|?[\s:\-|]+\|[\s:\-|]*$", RegexOptions.Compiled);

        public static List<Bloque> Parsear(string texto)
        {
            var res = new List<Bloque>();
            if (string.IsNullOrEmpty(texto)) return res;
            var lineas = texto.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;
            Bloque parrafo = null;

            Action cerrar = () => { if (parrafo != null && parrafo.Lineas.Count > 0) res.Add(parrafo); parrafo = null; };

            while (i < lineas.Length)
            {
                string ln = lineas[i];
                string tr = ln.TrimEnd();

                // --- valla de código (se acepta sin cerrar: durante el streaming llega a medias)
                if (tr.TrimStart().StartsWith("```"))
                {
                    cerrar();
                    string abre = tr.TrimStart();
                    var b = new Bloque { Tipo = TipoBloque.Codigo, Lenguaje = abre.Substring(3).Trim().ToLowerInvariant() };
                    i++;
                    while (i < lineas.Length && !lineas[i].TrimEnd().TrimStart().StartsWith("```")) { b.Codigo.Add(lineas[i]); i++; }
                    if (i < lineas.Length) i++;      // consumir el cierre
                    while (b.Codigo.Count > 0 && b.Codigo[b.Codigo.Count - 1].Trim().Length == 0) b.Codigo.RemoveAt(b.Codigo.Count - 1);
                    res.Add(b);
                    continue;
                }

                if (tr.Trim().Length == 0) { cerrar(); i++; continue; }

                if (ReRegla.IsMatch(tr)) { cerrar(); res.Add(new Bloque { Tipo = TipoBloque.Regla }); i++; continue; }

                var mt = ReTitulo.Match(tr);
                if (mt.Success)
                {
                    cerrar();
                    var b = new Bloque { Tipo = TipoBloque.Titulo, Nivel = mt.Groups[1].Value.Length };
                    b.Lineas.Add(new LineaRica { Trozos = Inline(mt.Groups[2].Value) });
                    res.Add(b);
                    i++;
                    continue;
                }

                // --- cita (y alertas de GitHub)
                if (tr.TrimStart().StartsWith(">"))
                {
                    cerrar();
                    var b = new Bloque { Tipo = TipoBloque.Cita };
                    while (i < lineas.Length && lineas[i].TrimStart().StartsWith(">"))
                    {
                        string c = lineas[i].TrimStart();
                        c = c.Substring(1);
                        if (c.StartsWith(" ")) c = c.Substring(1);
                        var ma = ReAlerta.Match(c.Trim());
                        if (ma.Success && b.Lineas.Count == 0) b.Alerta = ma.Groups[1].Value.ToUpperInvariant();
                        else b.Lineas.Add(new LineaRica { Trozos = Inline(c) });
                        i++;
                    }
                    if (b.Lineas.Count == 0 && b.Alerta.Length == 0) b.Lineas.Add(new LineaRica());
                    res.Add(b);
                    continue;
                }

                // --- tabla: la fila siguiente tiene que ser el separador |---|---|
                if (tr.Contains("|") && i + 1 < lineas.Length && ReSepTabla.IsMatch(lineas[i + 1]) && lineas[i + 1].Contains("-"))
                {
                    cerrar();
                    var b = new Bloque { Tipo = TipoBloque.Tabla, Encabezado = true };
                    b.Filas.Add(Celdas(tr));
                    i += 2;
                    while (i < lineas.Length && lineas[i].Contains("|") && lineas[i].Trim().Length > 0)
                    {
                        b.Filas.Add(Celdas(lineas[i]));
                        i++;
                    }
                    res.Add(b);
                    continue;
                }

                // --- listas
                var mv = ReVinieta.Match(ln);
                var mn = ReNumerada.Match(ln);
                if (mv.Success || mn.Success)
                {
                    cerrar();
                    var b = new Bloque { Tipo = TipoBloque.Lista };
                    while (i < lineas.Length)
                    {
                        var v = ReVinieta.Match(lineas[i]);
                        var n = ReNumerada.Match(lineas[i]);
                        if (!v.Success && !n.Success) break;
                        string espacios = (v.Success ? v.Groups[1].Value : n.Groups[1].Value);
                        int nivel = Math.Min(3, espacios.Replace("\t", "  ").Length / 2);
                        var lr = new LineaRica
                        {
                            Sangria = nivel,
                            Viñeta = v.Success ? "•" : n.Groups[2].Value + ".",
                            Trozos = Inline(v.Success ? v.Groups[3].Value : n.Groups[3].Value)
                        };
                        b.Lineas.Add(lr);
                        i++;
                    }
                    res.Add(b);
                    continue;
                }

                // --- párrafo: cada salto de línea suelto es un salto de verdad (así se escribe en un chat)
                if (parrafo == null) parrafo = new Bloque { Tipo = TipoBloque.Parrafo };
                parrafo.Lineas.Add(new LineaRica { Trozos = Inline(tr) });
                i++;
            }
            cerrar();
            return res;
        }

        static List<LineaRica> Celdas(string fila)
        {
            string s = fila.Trim();
            if (s.StartsWith("|")) s = s.Substring(1);
            if (s.EndsWith("|")) s = s.Substring(0, s.Length - 1);
            return s.Split('|').Select(c => new LineaRica { Trozos = Inline(c.Trim()) }).ToList();
        }

        /// <summary>
        /// Formato dentro de la línea: `código`, **negrita**, *itálica*, ~~tachado~~ y [texto](url).
        /// Se recorre una sola vez con un puntero; el código gana sobre todo lo demás, como corresponde.
        /// </summary>
        public static List<Trozo> Inline(string s)
        {
            var res = new List<Trozo>();
            if (string.IsNullOrEmpty(s)) return res;
            var actual = new StringBuilder();
            bool neg = false, ita = false, tac = false;

            Action volcar = () =>
            {
                if (actual.Length == 0) return;
                res.Add(new Trozo { Texto = actual.ToString(), Negrita = neg, Italica = ita, Tachado = tac });
                actual.Clear();
            };

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                if (c == '\\' && i + 1 < s.Length && "*_`~[]".IndexOf(s[i + 1]) >= 0) { actual.Append(s[++i]); continue; }

                if (c == '`')
                {
                    int cierre = s.IndexOf('`', i + 1);
                    if (cierre > i)
                    {
                        volcar();
                        res.Add(new Trozo { Texto = s.Substring(i + 1, cierre - i - 1), Codigo = true, Negrita = neg, Italica = ita });
                        i = cierre;
                        continue;
                    }
                }
                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    volcar(); neg = !neg; i++; continue;
                }
                if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
                {
                    volcar(); tac = !tac; i++; continue;
                }
                if ((c == '*' || c == '_') && !(i + 1 < s.Length && s[i + 1] == c))
                {
                    // un _ en medio de una palabra (nombre_de_variable) no es cursiva
                    bool pegadoIzq = i > 0 && !char.IsWhiteSpace(s[i - 1]);
                    bool pegadoDer = i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]);
                    if (c == '_' && pegadoIzq && pegadoDer) { actual.Append(c); continue; }
                    if (ita || pegadoDer) { volcar(); ita = !ita; continue; }
                    actual.Append(c);
                    continue;
                }
                if (c == '[')
                {
                    int cierre = s.IndexOf(']', i + 1);
                    if (cierre > i && cierre + 1 < s.Length && s[cierre + 1] == '(')
                    {
                        int fin = s.IndexOf(')', cierre + 2);
                        if (fin > cierre)
                        {
                            volcar();
                            res.Add(new Trozo
                            {
                                Texto = s.Substring(i + 1, cierre - i - 1),
                                Enlace = s.Substring(cierre + 2, fin - cierre - 2),
                                Negrita = neg, Italica = ita
                            });
                            i = fin;
                            continue;
                        }
                    }
                }
                actual.Append(c);
            }
            volcar();
            return res;
        }

        /// <summary>El texto pelado, para copiar al portapapeles o buscar.</summary>
        public static string Plano(List<Bloque> bloques)
        {
            var sb = new StringBuilder();
            foreach (var b in bloques)
            {
                switch (b.Tipo)
                {
                    case TipoBloque.Codigo: foreach (var l in b.Codigo) sb.AppendLine(l); break;
                    case TipoBloque.Regla: sb.AppendLine("---"); break;
                    case TipoBloque.Tabla:
                        foreach (var f in b.Filas) sb.AppendLine(string.Join("  ", f.Select(c => c.Plano)));
                        break;
                    default:
                        foreach (var l in b.Lineas) sb.AppendLine((l.Viñeta.Length > 0 ? l.Viñeta + " " : "") + l.Plano);
                        break;
                }
                sb.AppendLine();
            }
            return sb.ToString().Trim();
        }
    }
}
