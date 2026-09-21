using System;
using System.IO;
using System.Media;
using System.Threading;

namespace Capcom
{
    /// <summary>
    /// Campanitas generadas en memoria (WAV 44,1 kHz mono 16 bit). Sin archivos, sin dependencias.
    ///
    /// ⭐ Los dos tonos "quindar" son los de verdad: en Apollo, el circuito de tierra no podía mandar la portadora
    /// de PTT por la misma línea que la voz, así que se codificaba con un tono de **2525 Hz al ABRIR** el micrófono
    /// y **2475 Hz al CERRARLO**. Son esos dos bips que se escuchan en cada transmisión de las misiones. Acá suenan
    /// igual: al empezar a generar y al terminar.
    /// </summary>
    internal static class Sonidos
    {
        const int Fs = 44100;
        public static bool Silencio;

        /// <summary>Intro quindar: 2525 Hz, 250 ms. Abre la transmisión.</summary>
        public static void Abrir() => Tocar(new[] { (2525.0, 0.22, 0.13) }, 0.10);
        /// <summary>Outro quindar: 2475 Hz, 250 ms. La cierra.</summary>
        public static void Cerrar() => Tocar(new[] { (2475.0, 0.22, 0.13) }, 0.10);
        /// <summary>Dos notas ascendentes: algo salió bien.</summary>
        public static void Ok() => Tocar(new[] { (659.25, 0.10, 0.28), (987.77, 0.20, 0.24) }, 0.5);
        /// <summary>Tres descendentes: algo falló.</summary>
        public static void Falla() => Tocar(new[] { (523.25, 0.10, 0.30), (415.30, 0.10, 0.28), (329.63, 0.26, 0.26) }, 0.5);
        /// <summary>Un tic corto.</summary>
        public static void Tic() => Tocar(new[] { (1318.5, 0.035, 0.16) }, 0.4);

        static void Tocar((double hz, double seg, double vol)[] notas, double armonico)
        {
            if (Silencio) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var bytes = Wav(notas, armonico);
                    using (var ms = new MemoryStream(bytes))
                    using (var sp = new SoundPlayer(ms)) sp.PlaySync();
                }
                catch { }
            });
        }

        static byte[] Wav((double hz, double seg, double vol)[] notas, double armonico)
        {
            int total = 0;
            foreach (var n in notas) total += (int)(n.seg * Fs);
            total += Fs / 24;
            var pcm = new short[total];
            int pos = 0;
            foreach (var n in notas)
            {
                int len = (int)(n.seg * Fs);
                int ataque = Math.Min(Math.Max(len / 12, 64), 700), caida = Math.Min(len / 2, 2600);
                for (int i = 0; i < len && pos < total; i++, pos++)
                {
                    double env = 1.0;
                    if (i < ataque) env = i / (double)ataque;
                    else if (i > len - caida) env = (len - i) / (double)caida;
                    double t = i / (double)Fs;
                    double s = Math.Sin(2 * Math.PI * n.hz * t) * (1 - armonico) + Math.Sin(2 * Math.PI * n.hz * 2 * t) * armonico;
                    pcm[pos] = (short)(s * env * n.vol * short.MaxValue);
                }
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int datos = pcm.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + datos); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(datos);
                foreach (var s in pcm) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
