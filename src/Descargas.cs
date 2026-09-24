using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Capcom
{
    /// <summary>
    /// Un repo de HuggingFace: uno del catálogo, con por qué está en la lista, o uno que salió de buscar en todo
    /// HuggingFace, con lo que dice la API de él.
    /// </summary>
    internal sealed class Repo
    {
        public string Nombre = "", Ruta = "", Nota = "";
        public string Prefiere = "";        // la cuantización que conviene bajar primero
        public double GbAprox;

        // --- lo que trae la búsqueda
        public bool Buscado;                // no es del catálogo: salió de huggingface.co
        public long Bajadas, MeGusta;
        public bool Restringido;            // «gated»: hay que aceptar sus condiciones con una cuenta
        public bool Privado;

        public bool PideSesion => Restringido || Privado;
        public string Autor => Ruta.IndexOf('/') > 0 ? Ruta.Substring(0, Ruta.IndexOf('/')) : "";
        public string Corto => Ruta.Substring(Ruta.IndexOf('/') + 1);
    }

    /// <summary>
    /// Un .gguf del repo, tal como lo lista la API. Un modelo partido (`x-00001-of-00003.gguf`) es UNA fila con
    /// sus partes adentro: se bajan juntas, porque una parte suelta no sirve para nada.
    /// </summary>
    internal sealed class ArchivoRemoto
    {
        public string Repo = "", Ruta = "", Sha256 = "";
        public long Bytes;
        /// <summary>Las partes en orden, si el modelo viene partido; null si es un archivo solo.</summary>
        public List<ArchivoRemoto> Partes;

        public string Nombre => Path.GetFileName(Ruta);
        public double Gb => Bytes / 1073741824.0;
        public bool Partido => Partes != null && Partes.Count > 1;
        /// <summary>Lo que se muestra: un modelo partido va sin el `-00001-of-00003` y con cuántas partes son.</summary>
        public string Visible => Partido ? Descargas.SinParte(Nombre) + "  ·  " + Partes.Count + " partes" : Nombre;
        public string Cuant => Descargas.Cuantizacion(Nombre);
        /// <summary>🚨 Cada tramo de la ruta se escapa aparte: con sólo cambiar los espacios, un `#` o un `+` rompían la URL.</summary>
        public string Url => "https://huggingface.co/" + Repo + "/resolve/main/" + string.Join("/", Ruta.Split('/').Select(Uri.EscapeDataString)) + "?download=true";
    }

    /// <summary>Por qué HuggingFace no deja ver o bajar algo. Cada una tiene su remedio.</summary>
    internal enum Traba { Ninguna, Login, Licencia, Espera, TokenMalo, NoExiste, Limite, Otra }

    /// <summary>
    /// Bajar modelos de HuggingFace, con reanudación: los del catálogo o cualquier repo con .gguf que se busque,
    /// también los que piden iniciar sesión.
    ///
    /// 🚨🚨 En muchas redes la descarga SE CORTA: el gateway estrangula y las conexiones largas se caen a la mitad.
    /// Por eso el archivo se baja a `.partial` y cada reintento manda `Range: bytes=<lo que ya tengo>-`. Un
    /// descargador sin reanudación no termina nunca un archivo de 2 GB.
    ///
    /// 🚨 El proxy: `Cliente` anula `WebRequest.DefaultWebProxy` para todo el proceso (sin eso el primer pedido
    /// a 127.0.0.1 se cuelga en el WPAD). Para salir a internet hay que volver a ponerlo A MANO en el pedido:
    /// se intenta directo y, si falla la conexión, se reintenta con el proxy del sistema.
    ///
    /// 🚨 La sesión: los repos restringidos («gated») y los privados piden el token de una cuenta. Se usa el de
    /// AJUSTES o, si no hay, el mismo que la herramienta oficial (HF_TOKEN, o el que deja `hf auth login`). Viaja
    /// SÓLO a huggingface.co: las redirecciones se siguen a mano para que no llegue nunca a la CDN.
    /// </summary>
    internal sealed class Descargas
    {
        readonly Logger log;
        readonly Config cfg;
        Thread hilo;
        volatile bool cortar;
        long baseHechos;                    // lo que ya pesan las partes anteriores de un modelo partido

        public bool Bajando { get; private set; }
        public ArchivoRemoto Actual;
        public long Hechos, Totales;
        public double BytesPorSeg;
        public string Estado = "";
        public string Ultimo = "";
        /// <summary>Si la última bajada frenó porque HF no la dejó (sesión, condiciones), por qué.</summary>
        public Traba UltimaTraba { get; private set; }

        public event Action Cambio;
        public event Action<string> Listo;          // ruta del archivo terminado (la parte 1, si viene partido)
        public event Action SesionCambio;

        public Descargas(Logger l, Config c) { log = l; cfg = c; }

        void Avisar() { try { Cambio?.Invoke(); } catch { } }
        void AvisarSesion() { try { SesionCambio?.Invoke(); } catch { } }

        public double Fraccion => Totales > 0 ? Math.Max(0, Math.Min(1, Hechos / (double)Totales)) : 0;
        public TimeSpan Falta => BytesPorSeg > 1024 && Totales > Hechos
            ? TimeSpan.FromSeconds((Totales - Hechos) / BytesPorSeg) : TimeSpan.Zero;

        // ------------------------------------------------------------------ catálogo

        /// <summary>
        /// Modelos chicos que tienen sentido en una notebook común (4 núcleos físicos, sin GPU útil para esto).
        /// No están por fama: están porque entran en RAM y contestan en segundos, no en minutos.
        /// </summary>
        public static readonly List<Repo> Catalogo = new List<Repo>
        {
            new Repo { Nombre = "Qwen3.5-1.5B", Ruta = "Qwen/Qwen2.5-1.5B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 1.0,
                       Nota = "chico y correcto en castellano; buen punto de partida" },
            new Repo { Nombre = "Qwen3.5-3B", Ruta = "Qwen/Qwen2.5-3B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 2.0,
                       Nota = "el salto de calidad más barato en RAM" },
            new Repo { Nombre = "Qwen3.5-7B", Ruta = "Qwen/Qwen2.5-7B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 4.7,
                       Nota = "escribe notablemente mejor; ya se siente lento para chatear" },
            new Repo { Nombre = "Qwen3.5-Coder-7B", Ruta = "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", Prefiere = "q4_k_m", GbAprox = 4.7,
                       Nota = "para el examen de código: es lo suyo" },
            new Repo { Nombre = "Llama-3.2-3B", Ruta = "bartowski/Llama-3.2-3B-Instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 2.0,
                       Nota = "ojo: en las pruebas viejas contestaba en inglés" },
            new Repo { Nombre = "Phi-3.5-mini", Ruta = "bartowski/Phi-3.5-mini-instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 2.4,
                       Nota = "razona bien para lo que pesa; flojo en castellano" },
            new Repo { Nombre = "Mistral-7B-v0.3", Ruta = "bartowski/Mistral-7B-Instruct-v0.3-GGUF", Prefiere = "Q4_K_M", GbAprox = 4.4,
                       Nota = "clásico, sólido, sin sorpresas" },
            new Repo { Nombre = "granite-3.1-2b", Ruta = "bartowski/granite-3.1-2b-instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 1.6,
                       Nota = "de IBM, entrenado fuerte en instrucciones" },
            new Repo { Nombre = "SmolLM2-1.7B", Ruta = "bartowski/SmolLM2-1.7B-Instruct-GGUF", Prefiere = "Q4_K_M", GbAprox = 1.1,
                       Nota = "el más liviano que todavía sirve para algo" },
            new Repo { Nombre = "gemma-2-2b", Ruta = "bartowski/gemma-2-2b-it-GGUF", Prefiere = "Q4_K_M", GbAprox = 1.7,
                       Nota = "hermano chico del gemma que ya usás" },
        };

        // ------------------------------------------------------------------ la sesión en HuggingFace

        const string UrlWhoami = "https://huggingface.co/api/whoami-v2";

        readonly object candado = new object();
        string tokenVisto = "";             // el último token resuelto: si cambia, se olvida lo que se sabía del anterior
        string tokenVerificado = "";        // el último que ya pasó por `whoami` (bien o rechazado)
        bool tokenRechazado;                // HF contestó 401 con este token: vencido, revocado o mal copiado
        bool verificando, otraVez;

        /// <summary>Con qué cuenta estamos adentro, según `whoami`. Vacío: sin sesión o sin verificar todavía.</summary>
        public string Usuario { get; private set; } = "";
        /// <summary>Cómo está la sesión, dicho para mostrar: «sin sesión», «sesión de x», «verificando…».</summary>
        public string Sesion { get; private set; } = "sin sesión";
        /// <summary>De dónde salió el token: «ajustes», «HF_TOKEN», «HUGGING_FACE_HUB_TOKEN» o «huggingface-cli».</summary>
        public string OrigenToken { get; private set; } = "";

        public bool HayToken { get { lock (candado) return tokenVisto.Length > 0 && !tokenRechazado; } }
        public bool TokenRechazado { get { lock (candado) return tokenRechazado; } }

        /// <summary>
        /// Resuelve el token (sin salir a la red) y, si cambió, olvida lo que se sabía del anterior. Devuelve el
        /// token tal cual, aunque HF lo haya rechazado: el que decide si se manda es <see cref="Token"/>.
        /// </summary>
        public string MirarToken()
        {
            string origen;
            string t = ElegirToken(cfg != null ? cfg.HfToken : "", Environment.GetEnvironmentVariable, LeerSiHay, out origen);
            bool cambio = false;
            lock (candado)
            {
                if (t != tokenVisto)
                {
                    cambio = true;
                    tokenVisto = t;
                    tokenRechazado = false;
                    Usuario = "";
                    OrigenToken = t.Length > 0 ? origen : "";
                    Sesion = t.Length > 0 ? "token sin verificar" : "sin sesión";
                }
            }
            if (cambio) AvisarSesion();
            return t;
        }

        /// <summary>El token que se manda: ninguno si no hay o si HF ya lo rechazó.</summary>
        string Token()
        {
            string t = MirarToken();
            lock (candado) return tokenRechazado ? "" : t;
        }

        /// <summary>
        /// 🚨 HF contesta 401 a CUALQUIER pedido que traiga un token inválido, aunque el repo sea público: un
        /// HF_TOKEN viejo en el entorno dejaría sin bajar ni lo que no pide nada. Se lo marca y se sigue sin él.
        /// </summary>
        void Rechazar(string t)
        {
            lock (candado)
            {
                if (t != tokenVisto || tokenRechazado) return;
                tokenRechazado = true;
                tokenVerificado = t;
                Usuario = "";
                Sesion = "HuggingFace rechazó el token";
            }
            log?.Aviso("HuggingFace rechazó el token (" + OrigenToken + "): sigo sin sesión · se cambia en AJUSTES → huggingface");
            AvisarSesion();
        }

        /// <summary>Verifica la sesión si el token cambió desde la última vez. Sin token no sale a la red.</summary>
        public void Sesionar()
        {
            string t = MirarToken();
            bool falta;
            lock (candado) falta = t.Length > 0 && t != tokenVerificado;
            if (falta) VerificarSesion();
        }

        /// <summary>
        /// Le pregunta a HF con qué cuenta estamos adentro (`/api/whoami-v2`), en un hilo aparte, y avisa con
        /// SesionCambio. Así AJUSTES y el panel de descargas dicen si el token sirve sin tener que bajar nada.
        /// Si el token cambia mientras tanto, se vuelve a preguntar con el nuevo.
        /// </summary>
        public void VerificarSesion()
        {
            lock (candado)
            {
                if (verificando) { otraVez = true; return; }
                verificando = true;
            }
            new Thread(() =>
            {
                while (true)
                {
                    lock (candado) otraVez = false;
                    UnaVerificacion();
                    lock (candado) if (!otraVez) { verificando = false; break; }
                }
                AvisarSesion();
            })
            { IsBackground = true, Name = "capcom-sesion" }.Start();
        }

        void UnaVerificacion()
        {
            try
            {
                string t = MirarToken();
                if (t.Length == 0) return;
                lock (candado)
                {
                    if (t != tokenVisto || tokenRechazado) return;
                    Sesion = "verificando…";
                }
                AvisarSesion();
                string usuario = "", sesion;
                try
                {
                    using (var r = AbrirCon(UrlWhoami, 15000, 0, "GET", t, false))
                    using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        usuario = UsuarioDe(sr.ReadToEnd());
                    sesion = usuario.Length > 0 ? "sesión de " + usuario : "el token sirve";
                }
                catch (WebException ex) when (Codigo(ex) == 401) { Rechazar(t); return; }
                catch (Exception ex)
                {
                    // sin red no se sabe nada del token: queda sin verificar y se vuelve a probar la próxima vez
                    lock (candado) if (t == tokenVisto) Sesion = "no pude verificar el token: " + Corto(ex.Message, 60);
                    return;
                }
                lock (candado)
                {
                    if (t != tokenVisto) return;
                    tokenVerificado = t;
                    Usuario = usuario;
                    Sesion = sesion;
                }
                log?.Ok("Sesión en HuggingFace" + (usuario.Length > 0 ? ": " + usuario : "") + " (token de " + OrigenToken + ")");
            }
            catch { }
        }

        /// <summary>
        /// El token a usar y de dónde salió. Manda el de AJUSTES; si no hay, el mismo que usaría la herramienta
        /// oficial de HuggingFace: las variables HF_TOKEN o HUGGING_FACE_HUB_TOKEN, o el archivo que deja
        /// `hf auth login` (HF_TOKEN_PATH, o `token` dentro de HF_HOME, que por defecto es ~/.cache/huggingface).
        /// </summary>
        public static string ElegirToken(string deAjustes, Func<string, string> entorno, Func<string, string> leer, out string origen)
        {
            origen = "";
            string t = LimpiarToken(deAjustes);
            if (t.Length > 0) { origen = "ajustes"; return t; }
            foreach (var v in new[] { "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN" })
            {
                t = LimpiarToken(entorno(v));
                if (t.Length > 0) { origen = v; return t; }
            }
            try
            {
                string ruta = entorno("HF_TOKEN_PATH");
                if (string.IsNullOrWhiteSpace(ruta))
                {
                    string casa = entorno("HF_HOME");
                    if (string.IsNullOrWhiteSpace(casa))
                    {
                        string cache = entorno("XDG_CACHE_HOME");
                        if (string.IsNullOrWhiteSpace(cache))
                        {
                            string usuario = entorno("USERPROFILE");
                            if (string.IsNullOrWhiteSpace(usuario)) return "";
                            cache = Path.Combine(usuario, ".cache");
                        }
                        casa = Path.Combine(cache, "huggingface");
                    }
                    ruta = Path.Combine(casa, "token");
                }
                t = LimpiarToken(leer(ruta));
                if (t.Length > 0) { origen = "huggingface-cli"; return t; }
            }
            catch { }
            return "";
        }

        /// <summary>Un token tal como se pega: sin espacios ni saltos alrededor, sin comillas y sin el «Bearer» de adelante.</summary>
        public static string LimpiarToken(string t)
        {
            t = (t ?? "").Trim().Trim('"', '\'').Trim();
            if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7).Trim();
            return t;
        }

        static string LeerSiHay(string ruta)
        {
            try { return File.Exists(ruta) ? File.ReadAllText(ruta) : ""; }
            catch { return ""; }
        }

        public static string UsuarioDe(string json) => Json.S(Json.Parsear(json), "name");

        // ------------------------------------------------------------------ buscar en todo HuggingFace

        public const int TopeBusqueda = 60;

        /// <summary>
        /// Busca en TODO HuggingFace los repos que tienen .gguf, los más bajados primero. Con varias palabras, si la
        /// API no encuentra la frase tal cual, se le pide la palabra más larga y se filtra acá por todas.
        /// </summary>
        public List<Repo> Buscar(string q, out string porque)
        {
            porque = "";
            q = (q ?? "").Trim();
            var res = new List<Repo>();
            if (q.Length == 0) return res;
            try
            {
                res = BuscarEnApi(q);
                var palabras = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (res.Count == 0 && palabras.Length > 1)
                {
                    string larga = palabras.OrderByDescending(p => p.Length).First();
                    res = BuscarEnApi(larga)
                        .Where(r => palabras.All(p => r.Ruta.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                }
                if (res.Count == 0) porque = "nada con «" + q + "» que tenga .gguf";
            }
            catch (WebException ex) when (DeHf(ex)) { Traba t; porque = Explicar(ex, "", out t); }
            catch (Exception ex) { porque = Corto(ex.Message); }
            return res;
        }

        List<Repo> BuscarEnApi(string q)
        {
            string url = "https://huggingface.co/api/models?search=" + Uri.EscapeDataString(q) +
                         "&filter=gguf&sort=downloads&direction=-1&limit=" + TopeBusqueda;
            string siguiente;
            try { return ParsearBusqueda(Traer(url + "&expand=downloads&expand=likes&expand=gated&expand=private", 20000, out siguiente)); }
            catch (WebException ex) when (Codigo(ex) == 400)
            {
                // si la API no acepta pedir esos campos, igual trae bajadas y me gusta: sólo se pierde el candado
                return ParsearBusqueda(Traer(url, 20000, out siguiente));
            }
        }

        /// <summary>Los repos de una respuesta de `/api/models`. `gated` viene `false`, `"auto"` o `"manual"`.</summary>
        public static List<Repo> ParsearBusqueda(string json)
        {
            var res = new List<Repo>();
            foreach (var d in Json.ParsearLista(json))
            {
                string id = Json.S(d, "id", Json.S(d, "modelId"));
                if (id.IndexOf('/') <= 0) continue;
                object g;
                bool restringido = d.TryGetValue("gated", out g) &&
                    (g is string s ? s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase) : g is bool b && b);
                var r = new Repo
                {
                    Nombre = id, Ruta = id, Buscado = true, Prefiere = "Q4_K_M",
                    Bajadas = (long)Json.D(d, "downloads"), MeGusta = (long)Json.D(d, "likes"),
                    Restringido = restringido, Privado = Json.B(d, "private"),
                };
                r.Nota = r.Autor + " · " + Cantidad(r.Bajadas) + " bajadas" + (r.MeGusta > 0 ? " · " + Cantidad(r.MeGusta) + " me gusta" : "");
                res.Add(r);
            }
            return res;
        }

        /// <summary>
        /// Lee un repo de lo que se haya escrito o pegado: `autor/repo` o un link de huggingface.co (a la página, a un
        /// archivo, a lo que sea dentro del repo). Devuelve "" si no es un repo de modelos: una búsqueda común, un
        /// dataset, un space.
        /// </summary>
        public static string RepoDe(string texto)
        {
            string t = (texto ?? "").Trim();
            if (t.Length == 0 || t.IndexOf(' ') >= 0) return "";
            var m = Regex.Match(t, @"^(?:https?://)?(?:www\.)?(?:huggingface\.co|hf\.co)/(.*)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string resto = m.Groups[1].Value;
                int corte = resto.IndexOfAny(new[] { '?', '#' });
                if (corte >= 0) resto = resto.Substring(0, corte);
                var tramos = resto.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (tramos.Length < 2) return "";
                t = tramos[0] + "/" + tramos[1];
            }
            if (!RxRepo.IsMatch(t)) return "";
            return Reservados.Contains(t.Substring(0, t.IndexOf('/')).ToLowerInvariant()) ? "" : t;
        }

        static readonly Regex RxRepo = new Regex(@"^[A-Za-z0-9][\w.-]*/[\w.-]+$");
        /// <summary>Lo que va primero en una URL de huggingface.co sin ser un autor.</summary>
        static readonly HashSet<string> Reservados = new HashSet<string>
            { "datasets", "spaces", "models", "docs", "api", "settings", "organizations", "collections", "papers", "blog", "tasks", "login", "join" };

        /// <summary>1234 → «1,2 k»; 3400000 → «3,4 M». Para las bajadas y los me gusta de la búsqueda.</summary>
        public static string Cantidad(long n) =>
            n >= 1000000 ? (n / 1000000.0).ToString("0.#") + " M" : n >= 1000 ? (n / 1000.0).ToString("0.#") + " k" : n.ToString();

        // ------------------------------------------------------------------ los archivos de un repo

        /// <summary>
        /// Le pregunta a la API qué .gguf tiene el repo. Anónimo: `?blobs=true` pide token, el tree no. La API corta
        /// los árboles largos y pasa la página siguiente en la cabecera `Link`: se siguen todas. Con sesión se ven
        /// también los privados.
        /// </summary>
        public List<ArchivoRemoto> Listar(Repo r, out string porque, out Traba traba)
        {
            porque = "";
            traba = Traba.Ninguna;
            var res = new List<ArchivoRemoto>();
            try
            {
                string url = "https://huggingface.co/api/models/" + r.Ruta + "/tree/main?recursive=true";
                for (int pagina = 0; pagina < 50 && url.Length > 0; pagina++)
                {
                    string siguiente;
                    res.AddRange(ParsearArbol(Traer(url, 20000, out siguiente), r.Ruta));
                    url = siguiente;
                }
                res = Agrupar(res);
                if (res.Count == 0) porque = "el repo no tiene ningún .gguf";
            }
            catch (WebException ex) when (DeHf(ex)) { porque = Explicar(ex, r.Ruta, out traba); }
            catch (Exception ex) { porque = Corto(ex.Message); }
            return res;
        }

        /// <summary>Los .gguf de una página del árbol que devuelve la API.</summary>
        public static List<ArchivoRemoto> ParsearArbol(string json, string repo)
        {
            var res = new List<ArchivoRemoto>();
            foreach (var d in Json.ParsearLista(json))
            {
                if (Json.S(d, "type", "file") != "file") continue;
                string ruta = Json.S(d, "path");
                if (!ruta.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                var a = new ArchivoRemoto { Repo = repo, Ruta = ruta, Bytes = (long)Json.D(d, "size") };
                if (d.TryGetValue("lfs", out var lfs) && lfs is Dictionary<string, object> ld)
                {
                    // ⭐ para un archivo LFS, `oid` ES el sha256: alcanza para verificar sin pedir nada más
                    a.Sha256 = Json.S(ld, "oid");
                    long t = (long)Json.D(ld, "size");
                    if (t > 0) a.Bytes = t;
                }
                res.Add(a);
            }
            return res;
        }

        /// <summary>
        /// Junta las partes de cada modelo partido en una sola fila, en orden (la API no las lista en orden). Si al
        /// repo le falta alguna parte, quedan sueltas: bajarlas juntas daría un modelo que no levanta.
        /// </summary>
        public static List<ArchivoRemoto> Agrupar(List<ArchivoRemoto> archivos)
        {
            var res = new List<ArchivoRemoto>();
            var grupos = new Dictionary<string, List<ArchivoRemoto>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in archivos)
            {
                int parte, de;
                Parte(a.Nombre, out parte, out de);
                if (de < 2) { res.Add(a); continue; }
                string clave = SinParte(a.Ruta) + "|" + de;
                if (!grupos.TryGetValue(clave, out var l)) grupos[clave] = l = new List<ArchivoRemoto>();
                l.Add(a);
            }
            foreach (var l in grupos.Values)
            {
                var orden = l.OrderBy(x => { int p, d; Parte(x.Nombre, out p, out d); return p; }).ToList();
                int p1, de1;
                Parte(orden[0].Nombre, out p1, out de1);
                bool completo = orden.Count == de1 && orden.Select((x, i) => { int p, d; Parte(x.Nombre, out p, out d); return p == i + 1; }).All(v => v);
                if (!completo) { res.AddRange(orden); continue; }
                res.Add(new ArchivoRemoto { Repo = orden[0].Repo, Ruta = orden[0].Ruta, Bytes = orden.Sum(x => x.Bytes), Partes = orden });
            }
            return res.OrderBy(x => x.Bytes).ToList();
        }

        static readonly Regex RxParte = new Regex(@"-(\d{5})-of-(\d{5})(\.gguf)?$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Qué parte es y de cuántas, según el nombre que les pone `gguf-split`: (1, 3) para `x-00001-of-00003.gguf`,
        /// (0, 0) si no viene partido.
        /// </summary>
        public static void Parte(string nombre, out int parte, out int de)
        {
            var m = RxParte.Match(nombre ?? "");
            parte = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            de = m.Success ? int.Parse(m.Groups[2].Value) : 0;
        }

        /// <summary>El nombre sin el `-00001-of-00003` de los modelos partidos.</summary>
        public static string SinParte(string nombre) => RxParte.Replace(nombre ?? "", "$3");

        /// <summary>
        /// La cuantización, leída del nombre: `Q4_K_M`, `IQ4_XS`, `Q4_0`, `F16`, `BF16`... Con una lista fija se
        /// escapaban las que no estaban en ella (los q4_0 y q5_0 salían en blanco); con el patrón entran todas.
        /// </summary>
        public static string Cuantizacion(string nombre)
        {
            var m = RxCuant.Match(Path.GetFileNameWithoutExtension(nombre ?? ""));
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        static readonly Regex RxCuant = new Regex(@"(?:^|[-_.])(I?Q\d+(?:_[A-Z0-9]+)*|BF16|FP16|F16|FP32|F32|MXFP4(?:_MOE)?|TQ\d_\d)(?=[-_.]|$)", RegexOptions.IgnoreCase);

        /// <summary>
        /// ¿Se puede bajar? Un HEAD al primer archivo que NO sigue hasta la CDN: si HF redirige, hay acceso. Así el
        /// «pide sesión» aparece al elegir el repo y no después de apretar BAJAR. Devuelve "" si no hay traba.
        /// </summary>
        public string Acceso(ArchivoRemoto a, out Traba traba)
        {
            traba = Traba.Ninguna;
            if (a == null) return "";
            var primero = a.Partido ? a.Partes[0] : a;
            try
            {
                using (Abrir(primero.Url, 15000, 0, "HEAD", true)) { }
                return "";
            }
            catch (WebException ex) when (DeHf(ex)) { return Explicar(ex, a.Repo, out traba); }
            catch { return ""; }        // sin red o un error de la CDN: si sigue, lo va a decir la bajada
        }

        // ------------------------------------------------------------------ la bajada

        public bool Bajar(ArchivoRemoto a, string carpeta, out string porque)
        {
            porque = "";
            if (Bajando) { porque = "ya hay una descarga en curso"; return false; }
            if (a == null) { porque = "no elegiste ningún archivo"; return false; }
            if (string.IsNullOrWhiteSpace(carpeta)) { porque = "no hay carpeta de modelos configurada"; return false; }
            try { Directory.CreateDirectory(carpeta); }
            catch (Exception ex) { porque = "no puedo escribir en " + carpeta + ": " + ex.Message; return false; }

            var partes = a.Partido ? a.Partes : new List<ArchivoRemoto> { a };
            var faltan = partes.Where(p => !File.Exists(Path.Combine(carpeta, p.Nombre))).ToList();
            if (faltan.Count == 0) { porque = "ya lo tenés: " + Path.Combine(carpeta, a.Nombre); return false; }

            // lo que ya está en el .partial no se vuelve a pedir: una reanudación no necesita el archivo entero libre
            double pide = faltan.Sum(p => Math.Max(0, p.Bytes - Largo(Path.Combine(carpeta, p.Nombre) + ".partial"))) / 1073741824.0;
            double libre = EspacioLibre(carpeta);
            if (libre > 0 && pide > libre - 1)
            {
                porque = $"no entra en el disco: pide {pide:0.0} GB y quedan {libre:0.0} GB";
                return false;
            }

            cortar = false;
            Bajando = true;
            UltimaTraba = Traba.Ninguna;
            Actual = a;
            Hechos = 0;
            Totales = partes.Sum(p => p.Bytes);
            Estado = "conectando";
            Avisar();
            hilo = new Thread(() => Rutina(a, partes, carpeta)) { IsBackground = true, Name = "capcom-descarga" };
            hilo.Start();
            return true;
        }

        public void Cortar()
        {
            if (!Bajando) return;
            cortar = true;
            Estado = "cortando";
            Avisar();
        }

        void Rutina(ArchivoRemoto a, List<ArchivoRemoto> partes, string carpeta)
        {
            var reloj = Stopwatch.StartNew();
            try
            {
                long previas = 0;
                for (int i = 0; i < partes.Count; i++)
                {
                    string destino = Path.Combine(carpeta, partes[i].Nombre);
                    if (!File.Exists(destino))
                    {
                        string etiqueta = partes.Count > 1 ? "parte " + (i + 1) + " de " + partes.Count + " · " : "";
                        if (!BajarParte(partes[i], destino + ".partial", previas, etiqueta)) return;   // Estado ya dice por qué
                    }
                    previas += partes[i].Bytes;
                }

                // ⭐ recién con TODAS las partes bajadas y verificadas quedan con su nombre: un modelo partido a medias
                //    no aparece en el inventario como si se pudiera levantar
                foreach (var p in partes)
                {
                    string destino = Path.Combine(carpeta, p.Nombre);
                    if (!File.Exists(destino)) File.Move(destino + ".partial", destino);
                }
                reloj.Stop();
                string primero = Path.Combine(carpeta, partes[0].Nombre);
                Estado = $"listo en {reloj.Elapsed.TotalMinutes:0.0} min";
                Ultimo = primero;
                log?.Ok($"Bajado {a.Visible} · {Tema.Bytes(a.Bytes)} en {reloj.Elapsed.TotalMinutes:0.0} min" +
                        (partes.All(p => p.Sha256.Length == 64) ? " · SHA256 verificado" : ""));
                try { Listo?.Invoke(primero); } catch { }
            }
            catch (Exception ex) { Estado = "falló: " + Corto(ex.Message); log?.Error("Descarga fallida: " + ex.Message); }
            finally { Bajando = false; Actual = null; Avisar(); }
        }

        /// <summary>
        /// Baja un archivo (o una parte) a su `.partial`, reanudando lo que haya, y lo verifica. Devuelve false si
        /// se cortó o no se pudo, con el porqué en <see cref="Estado"/>.
        /// </summary>
        bool BajarParte(ArchivoRemoto a, string parcial, long previas, string etiqueta)
        {
            baseHechos = previas;
            int intento = 0;
            while (intento < 40 && !cortar)
            {
                intento++;
                long desde = Largo(parcial);
                if (a.Bytes > 0 && desde > a.Bytes)
                {
                    // más grande que el original: no es de este archivo (o cambió en el repo). De cero.
                    Borrar(parcial);
                    desde = 0;
                }
                Hechos = previas + desde;
                if (a.Bytes > 0 && desde == a.Bytes) break;
                Estado = etiqueta + (intento == 1 ? (desde > 0 ? "reanudando" : "bajando") : "reanudando (intento " + intento + ")");
                Avisar();
                try
                {
                    if (Tramo(a, parcial, desde)) break;
                }
                catch (WebException ex) when (EsDefinitivo(ex))
                {
                    // 🚨 un 401/403/404 no se arregla reintentando: antes eran 40 intentos con espera para
                    //    terminar diciendo «se cortó», cuando lo que faltaba era iniciar sesión
                    Traba t;
                    Estado = etiqueta + Explicar(ex, a.Repo, out t);
                    UltimaTraba = t;
                    log?.Error("Descarga de " + a.Nombre + " frenada · " + Estado);
                    return false;
                }
                catch (WebException ex) when (Codigo(ex) == 416)
                {
                    // «no hay nada desde ahí»: el .partial no calza con el archivo del repo
                    if (a.Bytes <= 0) break;
                    log?.Aviso("El .partial de " + a.Nombre + " no calza con el del repo: se baja de nuevo desde el principio");
                    Borrar(parcial);
                }
                catch (Exception ex)
                {
                    log?.Aviso($"Descarga cortada en {Tema.Bytes(Hechos)}: {Corto(ex.Message)} · reintento {intento}");
                    int espera = Math.Min(8000, 800 * intento);
                    if (ex is WebException we && Codigo(we) == 429)
                    {
                        // HF pide bajar el ritmo: se respeta lo que diga Retry-After (o medio minuto)
                        espera = Math.Max(espera, Math.Min(120000, EsperaPedida(we) * 1000));
                        Estado = etiqueta + "HuggingFace pide esperar · sigo en " + (espera / 1000) + " s";
                    }
                    else Estado = etiqueta + "se cortó · " + Corto(ex.Message, 60);
                    Avisar();
                    Esperar(espera);
                }
            }

            if (cortar) { Estado = "cortada · lo bajado queda para seguir después"; log?.Info("Descarga cortada: " + a.Nombre); return false; }

            long final = Largo(parcial);
            if (a.Bytes > 0 && final != a.Bytes)
            {
                Estado = etiqueta + $"quedó incompleta ({Tema.Bytes(final)} de {Tema.Bytes(a.Bytes)})";
                log?.Error("Descarga incompleta: " + a.Nombre + " · " + Estado);
                return false;
            }

            if (a.Sha256.Length == 64)
            {
                Estado = etiqueta + "verificando el SHA256";
                Avisar();
                string sha = Sha256(parcial);
                if (!string.Equals(sha, a.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // 🚨 se borra: un .partial completo y corrupto no se arreglaba nunca, porque cada reintento lo
                    //    daba por terminado y lo volvía a verificar
                    Borrar(parcial);
                    Estado = etiqueta + "el SHA256 no da: el archivo llegó corrupto · lo borré, volvé a bajarlo";
                    log?.Error("SHA256 distinto en " + a.Nombre + " · esperaba " + a.Sha256.Substring(0, 12) + "… y vino " + sha.Substring(0, 12) + "…");
                    return false;
                }
            }
            return true;
        }

        /// <summary>Un tramo de descarga. Devuelve true si llegó al final del archivo.</summary>
        bool Tramo(ArchivoRemoto a, string parcial, long desde)
        {
            using (var resp = Abrir(a.Url, 60000, desde))
            {
                if (desde > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    // el servidor ignoró el Range: hay que empezar de cero para no pegar dos mitades distintas
                    log?.Aviso("El servidor no acepta reanudar: se baja de nuevo desde el principio");
                    Borrar(parcial);
                    desde = 0;
                    Hechos = baseHechos;
                }
                long esperado = a.Bytes > 0 ? a.Bytes : resp.ContentLength > 0 ? resp.ContentLength + desde : 0;
                if (a.Bytes <= 0 && esperado > 0) Totales = baseHechos + esperado;

                var buffer = new byte[128 * 1024];
                var reloj = Stopwatch.StartNew();
                long enEsteTramo = 0;
                using (var st = resp.GetResponseStream())
                using (var fs = new FileStream(parcial, desde > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                {
                    int n;
                    var ultimoAviso = Stopwatch.StartNew();
                    while ((n = st.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (cortar) { fs.Flush(); return false; }
                        fs.Write(buffer, 0, n);
                        Hechos += n;
                        enEsteTramo += n;
                        if (ultimoAviso.ElapsedMilliseconds > 250)
                        {
                            BytesPorSeg = enEsteTramo / Math.Max(0.001, reloj.Elapsed.TotalSeconds);
                            ultimoAviso.Restart();
                            Avisar();
                        }
                    }
                    fs.Flush();
                }
                return esperado <= 0 || desde + enEsteTramo >= esperado;
            }
        }

        // ------------------------------------------------------------------ qué dijo HuggingFace

        /// <summary>
        /// Traduce una respuesta de error de HF a qué pasó y qué hacer. HF usa el 401 para «no sé quién sos» (repo
        /// restringido o privado sin sesión; también uno que no existe, para no revelar cuáles sí) y el 403 para «sé
        /// quién sos, pero no aceptaste las condiciones».
        /// </summary>
        public static string Diagnosticar(int codigo, string codigoHf, string mensaje, bool conToken, bool tokenRechazado, string repo, out Traba traba)
        {
            codigoHf = codigoHf ?? "";
            mensaje = mensaje ?? "";
            string pagina = "huggingface.co/" + repo;
            bool restringido = codigoHf == "GatedRepo" || Dice(mensaje, "restricted") || Dice(mensaje, "gated");
            if (tokenRechazado || (codigo == 401 && conToken))
            {
                traba = Traba.TokenMalo;
                return "HuggingFace rechazó el token (vencido, revocado o mal copiado): pegá uno nuevo en AJUSTES → huggingface";
            }
            switch (codigo)
            {
                case 401:
                    traba = Traba.Login;
                    return restringido
                        ? "este repo pide iniciar sesión: aceptá sus condiciones en " + pagina + " con tu cuenta y pegá tu token en AJUSTES → huggingface"
                        : "sin sesión no lo veo: o no existe, o es privado y hace falta tu token (AJUSTES → huggingface)";
                case 403:
                    if (Dice(mensaje, "awaiting"))
                    {
                        traba = Traba.Espera;
                        return "tu pedido de acceso está esperando que lo aprueben los autores de " + pagina;
                    }
                    traba = Traba.Licencia;
                    if (Dice(mensaje, "fine-grained"))
                        return "tu token es de permisos finos y no tiene habilitados los repos restringidos: tildalo en huggingface.co/settings/tokens";
                    return "tu cuenta todavía no tiene acceso: aceptá las condiciones en " + pagina + " y volvé a probar";
                case 404:
                    traba = Traba.NoExiste;
                    if (codigoHf == "EntryNotFound") return "ese archivo ya no está en el repo";
                    return conToken ? "no existe, o tu cuenta no tiene acceso a " + pagina : "no existe " + pagina;
                case 429:
                    traba = Traba.Limite;
                    return "HuggingFace pide que bajes el ritmo (demasiados pedidos seguidos)" + (conToken ? "" : ": con sesión el límite es más alto");
            }
            traba = Traba.Otra;
            if (codigo >= 500) return "HuggingFace está con problemas (error " + codigo + "): probá en un rato";
            return mensaje.Length > 0 ? Corto(mensaje) : "error " + codigo;
        }

        static bool Dice(string texto, string que) => texto.IndexOf(que, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>El diagnóstico de un error de HF con lo que se sabe de la sesión en este momento.</summary>
        string Explicar(WebException ex, string repo, out Traba traba)
        {
            int codigo; string codigoHf, mensaje;
            QueDijo(ex, out codigo, out codigoHf, out mensaje);
            return Diagnosticar(codigo, codigoHf, mensaje, HayToken, TokenRechazado, repo, out traba);
        }

        /// <summary>Qué dijo HF del error: las cabeceras X-Error-Code y X-Error-Message o, si no están, el `error` del JSON.</summary>
        static void QueDijo(WebException ex, out int codigo, out string codigoHf, out string mensaje)
        {
            codigo = 0; codigoHf = ""; mensaje = "";
            var r = ex.Response as HttpWebResponse;
            if (r == null) { mensaje = ex.Message; return; }
            codigo = (int)r.StatusCode;
            codigoHf = r.Headers["X-Error-Code"] ?? "";
            mensaje = r.Headers["X-Error-Message"] ?? "";
            if (mensaje.Length > 0) return;
            try
            {
                using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                {
                    var buf = new char[8192];
                    int n = sr.ReadBlock(buf, 0, buf.Length);
                    mensaje = Json.S(Json.Parsear(new string(buf, 0, n)), "error");
                }
            }
            catch { }
        }

        static int Codigo(WebException ex) => ex.Response is HttpWebResponse r ? (int)r.StatusCode : 0;

        /// <summary>¿El error lo contestó huggingface.co? (y no la CDN ni la red)</summary>
        static bool DeHf(WebException ex) => ex.Response is HttpWebResponse r && LlevaToken(r.ResponseUri);

        static bool EsDefinitivo(WebException ex)
        {
            int c = Codigo(ex);
            return DeHf(ex) && (c == 401 || c == 403 || c == 404 || c == 410 || c == 451);
        }

        static int EsperaPedida(WebException ex)
        {
            int s;
            var r = ex.Response as HttpWebResponse;
            return r != null && int.TryParse(r.Headers["Retry-After"], out s) && s > 0 ? s : 30;
        }

        // ------------------------------------------------------------------ plomería

        static double EspacioLibre(string carpeta)
        {
            try
            {
                var raiz = Path.GetPathRoot(Path.GetFullPath(carpeta));
                return new DriveInfo(raiz).AvailableFreeSpace / 1073741824.0;
            }
            catch { return 0; }
        }

        static long Largo(string ruta)
        {
            try { return File.Exists(ruta) ? new FileInfo(ruta).Length : 0; }
            catch { return 0; }
        }

        static void Borrar(string ruta) { try { File.Delete(ruta); } catch { } }

        /// <summary>Espera de a poco, para que CORTAR no tenga que esperar a que termine la pausa.</summary>
        void Esperar(int ms)
        {
            for (int i = 0; i < ms && !cortar; i += 200) Thread.Sleep(200);
        }

        static string Sha256(string ruta)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
            using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return string.Concat(h.ComputeHash(fs).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// Un pedido a internet con todo lo que hace falta: el token (sólo para huggingface.co), las redirecciones
        /// a mano y el proxy del sistema si la conexión directa falla. `soloHf` no sigue la redirección a la CDN:
        /// devuelve el 302 tal cual, que para saber si hay acceso alcanza.
        /// </summary>
        HttpWebResponse Abrir(string url, int timeoutMs, long desde = 0, string metodo = "GET", bool soloHf = false)
        {
            string token = Token();
            try { return AbrirCon(url, timeoutMs, desde, metodo, token, soloHf); }
            catch (WebException ex) when (token.Length > 0 && Codigo(ex) == 401 && DeHf(ex))
            {
                Rechazar(token);
                return AbrirCon(url, timeoutMs, desde, metodo, "", soloHf);
            }
        }

        static HttpWebResponse AbrirCon(string url, int timeoutMs, long desde, string metodo, string token, bool soloHf)
        {
            try { return Saltar(new Uri(url), timeoutMs, desde, metodo, token, soloHf, false); }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.ConnectFailure || ex.Status == WebExceptionStatus.NameResolutionFailure ||
                                          ex.Status == WebExceptionStatus.ProxyNameResolutionFailure)
            {
                // sin salida directa: probamos con el proxy del sistema
                return Saltar(new Uri(url), timeoutMs, desde, metodo, token, soloHf, true);
            }
        }

        /// <summary>
        /// 🚨 Las redirecciones se siguen A MANO. El `resolve` de HF contesta con un 302 a la CDN, con la URL ya
        /// firmada: si el token viajara hasta ahí, la CDN rechaza el pedido por traer dos autenticaciones y además se
        /// lo estaríamos dando a otro dominio. Y la redirección automática de .NET lo borra en CUALQUIER salto,
        /// también en los que se quedan en huggingface.co (un repo renombrado), donde sí hace falta. Salto por salto,
        /// el token va sólo si el destino es huggingface.co.
        /// </summary>
        static HttpWebResponse Saltar(Uri url, int timeoutMs, long desde, string metodo, string token, bool soloHf, bool conProxy)
        {
            for (int salto = 0; salto < 10; salto++)
            {
                var req = Armar(url, timeoutMs, conProxy);
                req.Method = metodo;
                if (token.Length > 0 && LlevaToken(url)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                if (desde > 0) req.AddRange(desde);
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException ex) when (ex.Response is HttpWebResponse r && EsRedireccion(r.StatusCode)) { resp = r; }
                if (!EsRedireccion(resp.StatusCode)) return resp;
                string destino = resp.Headers[HttpResponseHeader.Location];
                if (string.IsNullOrEmpty(destino)) { resp.Close(); throw new WebException("HuggingFace redirigió sin decir adónde"); }
                var siguiente = new Uri(url, destino);
                if (soloHf && !LlevaToken(siguiente)) return resp;
                resp.Close();
                url = siguiente;
            }
            throw new WebException("demasiadas redirecciones");
        }

        static bool EsRedireccion(HttpStatusCode c)
        {
            int n = (int)c;
            return n == 301 || n == 302 || n == 303 || n == 307 || n == 308;
        }

        /// <summary>El token va sólo a huggingface.co, y por https: ni a la CDN ni a ningún otro lado.</summary>
        public static bool LlevaToken(Uri u) => u != null && u.Scheme == Uri.UriSchemeHttps &&
            (string.Equals(u.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase) || string.Equals(u.Host, "hf.co", StringComparison.OrdinalIgnoreCase));

        static HttpWebRequest Armar(Uri url, int timeoutMs, bool conProxy)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "capcom/1.0";
            req.KeepAlive = true;
            req.AllowAutoRedirect = false;
            req.Proxy = conProxy ? WebRequest.GetSystemWebProxy() : null;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            return req;
        }

        /// <summary>Un pedido de texto; `siguiente` es la página que sigue según la cabecera `Link`, o "".</summary>
        string Traer(string url, int timeoutMs, out string siguiente)
        {
            using (var r = Abrir(url, timeoutMs))
            {
                siguiente = SiguientePagina(r.Headers["Link"]);
                if (siguiente.Length > 0) siguiente = new Uri(r.ResponseUri, siguiente).AbsoluteUri;
                using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
        }

        /// <summary>La URL con `rel="next"` de una cabecera `Link` (`&lt;url&gt;; rel="next"`), o "" si no hay.</summary>
        public static string SiguientePagina(string link)
        {
            if (string.IsNullOrEmpty(link)) return "";
            foreach (Match m in Regex.Matches(link, @"<([^>]*)>([^<]*)"))
                if (Regex.IsMatch(m.Groups[2].Value, @"rel\s*=\s*""?next""?", RegexOptions.IgnoreCase)) return m.Groups[1].Value;
            return "";
        }

        static string Corto(string s, int n = 90)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
