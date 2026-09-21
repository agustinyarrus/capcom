using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace Capcom
{
    internal enum Clase { Normal, Palabra, Tipo, Cadena, Numero, Comentario, Simbolo, Funcion, Anotacion }

    internal struct Token
    {
        public string Texto;
        public Clase Clase;
        public Token(string t, Clase c) { Texto = t; Clase = c; }
    }

    /// <summary>
    /// Resaltador de sintaxis propio, por línea y con memoria entre líneas (comentarios de bloque y cadenas de
    /// varias líneas). No es un parser: es un escáner léxico, que es exactamente lo que hace falta para pintar.
    ///
    /// ⭐ Los matices se reparten por FRECUENCIA del token en cada lenguaje, no solo por rol: lo que más aparece
    /// se lleva el color más calmo, así el bloque no termina siendo un arcoíris.
    /// </summary>
    internal static class Resaltador
    {
        public struct Estado { public bool EnComentario; public bool EnCadena; public char Comilla; }

        static readonly Dictionary<string, string[]> Palabras = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield record nameof when".Split(' '),
            ["js"] = "async await break case catch class const continue debugger default delete do else export extends false finally for from function get if import in instanceof let new null of return set static super switch this throw true try typeof undefined var void while with yield as interface type enum implements readonly public private protected".Split(' '),
            ["py"] = "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield match case self".Split(' '),
            ["sql"] = "select from where group by order having join inner left right outer full on as and or not in exists between like is null insert into values update set delete create table alter drop index view primary key foreign references distinct union all case when then else end with top limit offset asc desc count sum avg min max cast convert declare begin commit rollback".Split(' '),
            ["go"] = "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var nil true false make new len cap append copy delete panic recover".Split(' '),
            ["sh"] = "if then else elif fi for while do done case esac function return exit export local readonly set unset shift source alias echo cd ls rm cp mv mkdir grep sed awk cat curl git sudo".Split(' '),
            ["ps"] = "function param begin process end if else elseif switch foreach for while do until try catch finally throw return break continue filter workflow class enum using module true false null not and or".Split(' '),
            ["css"] = "important media supports keyframes import from to and not only screen print".Split(' '),
            ["yaml"] = "true false null yes no on off".Split(' '),
            ["json"] = "true false null".Split(' '),
        };

        static readonly Dictionary<string, string[]> Tipos = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "String Int32 Int64 Double Boolean List Dictionary IEnumerable Task Action Func Exception Console Math DateTime TimeSpan Guid StringBuilder Color Rectangle Point Size Font Graphics Control Form".Split(' '),
            ["js"] = "Array Object String Number Boolean Promise Map Set JSON Math Date RegExp Error console window document".Split(' '),
            ["py"] = "int str float bool list dict set tuple bytes object Exception print len range open enumerate zip map filter sorted sum min max abs isinstance super".Split(' '),
            ["go"] = "string int int8 int16 int32 int64 uint uint8 uint32 uint64 byte rune float32 float64 bool error any".Split(' '),
        };

        public static string Normalizar(string lenguaje)
        {
            switch ((lenguaje ?? "").Trim().ToLowerInvariant())
            {
                case "c#": case "cs": case "csharp": case "dotnet": return "cs";
                case "js": case "javascript": case "jsx": case "ts": case "tsx": case "typescript": case "mjs": return "js";
                case "py": case "python": case "python3": return "py";
                case "sql": case "tsql": case "mysql": case "postgres": case "psql": return "sql";
                case "go": case "golang": return "go";
                case "sh": case "bash": case "shell": case "zsh": case "console": return "sh";
                case "ps": case "ps1": case "powershell": case "pwsh": return "ps";
                case "json": return "json";
                case "css": case "scss": case "less": return "css";
                case "yaml": case "yml": return "yaml";
                case "html": case "xml": case "svg": case "xaml": return "xml";
                case "md": case "markdown": return "md";
                default: return "";
            }
        }

        public static Color ColorDe(Clase c)
        {
            switch (c)
            {
                case Clase.Palabra: return Tema.Malva;
                case Clase.Tipo: return Tema.Crema;
                case Clase.Cadena: return Tema.Salvia;
                case Clase.Numero: return Tema.Ambar;
                case Clase.Comentario: return Tema.Apagado;
                case Clase.Funcion: return Tema.Cielo;
                case Clase.Anotacion: return Tema.Rosa;
                case Clase.Simbolo: return Tema.Suave;
                default: return Tema.Texto;
            }
        }

        /// <summary>Tokeniza una línea. `st` viaja entre líneas para los comentarios de bloque y las cadenas largas.</summary>
        public static List<Token> Tokenizar(string linea, string lenguaje, ref Estado st)
        {
            var res = new List<Token>();
            string lg = Normalizar(lenguaje);
            if (linea == null) return res;
            if (lg == "xml") return Xml(linea, ref st);
            if (lg == "md") { res.Add(new Token(linea, Clase.Normal)); return res; }

            string unaLinea = lg == "py" || lg == "sh" || lg == "yaml" || lg == "ps" ? "#" : lg == "sql" ? "--" : "//";
            bool bloque = lg == "cs" || lg == "js" || lg == "go" || lg == "css" || lg == "sql";
            var palabras = Palabras.ContainsKey(lg) ? new HashSet<string>(Palabras[lg], StringComparer.Ordinal) : new HashSet<string>();
            var tipos = Tipos.ContainsKey(lg) ? new HashSet<string>(Tipos[lg], StringComparer.Ordinal) : new HashSet<string>();

            var sb = new StringBuilder();
            Action<Clase> volcar = cl => { if (sb.Length > 0) { res.Add(new Token(sb.ToString(), cl)); sb.Clear(); } };

            int i = 0;
            while (i < linea.Length)
            {
                // continúa un comentario de bloque abierto en una línea anterior
                if (st.EnComentario)
                {
                    int fin = linea.IndexOf("*/", i, StringComparison.Ordinal);
                    if (fin < 0) { res.Add(new Token(linea.Substring(i), Clase.Comentario)); return res; }
                    res.Add(new Token(linea.Substring(i, fin - i + 2), Clase.Comentario));
                    st.EnComentario = false;
                    i = fin + 2;
                    continue;
                }
                if (st.EnCadena)
                {
                    int fin = linea.IndexOf(st.Comilla, i);
                    if (fin < 0) { res.Add(new Token(linea.Substring(i), Clase.Cadena)); return res; }
                    res.Add(new Token(linea.Substring(i, fin - i + 1), Clase.Cadena));
                    st.EnCadena = false;
                    i = fin + 1;
                    continue;
                }

                char c = linea[i];

                // comentario de línea
                if (unaLinea.Length > 0 && i + unaLinea.Length <= linea.Length && string.CompareOrdinal(linea, i, unaLinea, 0, unaLinea.Length) == 0)
                {
                    res.Add(new Token(linea.Substring(i), Clase.Comentario));
                    return res;
                }
                if (bloque && c == '/' && i + 1 < linea.Length && linea[i + 1] == '*')
                {
                    int fin = linea.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (fin < 0) { res.Add(new Token(linea.Substring(i), Clase.Comentario)); st.EnComentario = true; return res; }
                    res.Add(new Token(linea.Substring(i, fin - i + 2), Clase.Comentario));
                    i = fin + 2;
                    continue;
                }

                // cadenas
                if (c == '"' || c == '\'' || (c == '`' && lg == "js"))
                {
                    int j = i + 1;
                    bool cerrada = false;
                    while (j < linea.Length)
                    {
                        if (linea[j] == '\\' && lg != "ps") { j += 2; continue; }
                        if (linea[j] == c) { cerrada = true; break; }
                        j++;
                    }
                    if (cerrada) { res.Add(new Token(linea.Substring(i, j - i + 1), Clase.Cadena)); i = j + 1; }
                    else { res.Add(new Token(linea.Substring(i), Clase.Cadena)); st.EnCadena = c == '`'; st.Comilla = c; return res; }
                    continue;
                }

                // atributos / decoradores / variables con sigilo
                if ((c == '[' && lg == "cs" && i + 1 < linea.Length && char.IsUpper(linea[i + 1])) ||
                    (c == '@' && (lg == "py" || lg == "js" || lg == "cs")) ||
                    (c == '$' && (lg == "ps" || lg == "sh" || lg == "js")))
                {
                    int j = i + 1;
                    while (j < linea.Length && (char.IsLetterOrDigit(linea[j]) || linea[j] == '_' || linea[j] == '.' || linea[j] == ':')) j++;
                    res.Add(new Token(linea.Substring(i, j - i), Clase.Anotacion));
                    i = j;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < linea.Length && char.IsDigit(linea[i + 1])))
                {
                    int j = i;
                    while (j < linea.Length && (char.IsLetterOrDigit(linea[j]) || linea[j] == '.' || linea[j] == 'x' || linea[j] == '_')) j++;
                    res.Add(new Token(linea.Substring(i, j - i), Clase.Numero));
                    i = j;
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < linea.Length && (char.IsLetterOrDigit(linea[j]) || linea[j] == '_')) j++;
                    string pal = linea.Substring(i, j - i);
                    bool llamada = j < linea.Length && linea[j] == '(';
                    Clase cl = palabras.Contains(pal) ? Clase.Palabra
                             : palabras.Contains(pal.ToLowerInvariant()) && (lg == "sql" || lg == "sh" || lg == "ps") ? Clase.Palabra
                             : tipos.Contains(pal) ? Clase.Tipo
                             : llamada ? Clase.Funcion
                             : lg == "yaml" && j < linea.Length && linea[j] == ':' ? Clase.Tipo
                             : Clase.Normal;
                    res.Add(new Token(pal, cl));
                    i = j;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    int j = i;
                    while (j < linea.Length && char.IsWhiteSpace(linea[j])) j++;
                    res.Add(new Token(linea.Substring(i, j - i), Clase.Normal));
                    i = j;
                    continue;
                }

                res.Add(new Token(c.ToString(), Clase.Simbolo));
                i++;
            }
            volcar(Clase.Normal);
            return res;
        }

        static List<Token> Xml(string linea, ref Estado st)
        {
            var res = new List<Token>();
            int i = 0;
            while (i < linea.Length)
            {
                if (st.EnComentario)
                {
                    int fin = linea.IndexOf("-->", i, StringComparison.Ordinal);
                    if (fin < 0) { res.Add(new Token(linea.Substring(i), Clase.Comentario)); return res; }
                    res.Add(new Token(linea.Substring(i, fin - i + 3), Clase.Comentario));
                    st.EnComentario = false; i = fin + 3; continue;
                }
                if (linea.IndexOf("<!--", i, StringComparison.Ordinal) == i)
                {
                    int fin = linea.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (fin < 0) { res.Add(new Token(linea.Substring(i), Clase.Comentario)); st.EnComentario = true; return res; }
                    res.Add(new Token(linea.Substring(i, fin - i + 3), Clase.Comentario));
                    i = fin + 3; continue;
                }
                if (linea[i] == '<')
                {
                    int fin = linea.IndexOf('>', i);
                    string etq = fin < 0 ? linea.Substring(i) : linea.Substring(i, fin - i + 1);
                    // dentro de la etiqueta: nombre en malva, atributos normales, valores en salvia
                    int k = 0;
                    var sb = new StringBuilder();
                    while (k < etq.Length)
                    {
                        if (etq[k] == '"' || etq[k] == '\'')
                        {
                            char q = etq[k];
                            int j = etq.IndexOf(q, k + 1);
                            if (j < 0) j = etq.Length - 1;
                            if (sb.Length > 0) { res.Add(new Token(sb.ToString(), Clase.Tipo)); sb.Clear(); }
                            res.Add(new Token(etq.Substring(k, j - k + 1), Clase.Cadena));
                            k = j + 1;
                            continue;
                        }
                        sb.Append(etq[k]); k++;
                    }
                    if (sb.Length > 0) res.Add(new Token(sb.ToString(), Clase.Tipo));
                    i = fin < 0 ? linea.Length : fin + 1;
                    continue;
                }
                int sig = linea.IndexOf('<', i);
                if (sig < 0) { res.Add(new Token(linea.Substring(i), Clase.Normal)); break; }
                res.Add(new Token(linea.Substring(i, sig - i), Clase.Normal));
                i = sig;
            }
            return res;
        }

        /// <summary>Adivina el lenguaje cuando la valla vino sin nombre. Barato y sin pretensiones.</summary>
        public static string Adivinar(IEnumerable<string> lineas)
        {
            string todo = string.Join("\n", lineas.Take(30));
            if (todo.Contains("using System") || todo.Contains("namespace ") || todo.Contains("public class")) return "cs";
            if (todo.Contains("def ") && todo.Contains(":")) return "py";
            if (todo.Contains("func ") && todo.Contains("package ")) return "go";
            if (todo.Contains("SELECT ") || todo.Contains("select ") && todo.Contains(" from ")) return "sql";
            if (todo.TrimStart().StartsWith("{") && todo.Contains("\":")) return "json";
            if (todo.Contains("const ") || todo.Contains("=>") || todo.Contains("function ")) return "js";
            if (todo.Contains("$") && (todo.Contains("Get-") || todo.Contains("param("))) return "ps";
            if (todo.TrimStart().StartsWith("<")) return "xml";
            if (todo.Contains("#!/") || todo.Contains("sudo ") || todo.Contains("apt ")) return "sh";
            return "";
        }
    }
}
