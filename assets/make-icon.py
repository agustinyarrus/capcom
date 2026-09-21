#!/usr/bin/env python3
"""
make-icon.py - arma assets\\app.ico desde assets\\galaxia.png (la insignia de CAPCOM desde el 21-sep-2026).

Un frame PNG por cada tamano que Windows pide de verdad, para no dejarlo reescalar a el:
    16 20 24 30 32 36 40 48 60 64 72 80 96 128 256
  - 24 = bandeja y titulo al 150 %, 36 = barra de tareas al 150 %, 48 = Alt+Tab / escritorio.
  - Nada por encima de 256: en el recurso del exe un segundo frame "de 256" (el byte 0) confunde la eleccion.

Calidad:
  - Reduccion con ALFA PREMULTIPLICADO: el PNG trae RGB=(0,0,0) debajo de lo transparente y un LANCZOS por canal
    mezclaria ese negro en el contorno.
  - Realce (unsharp) progresivo en los frames chicos, hecho TAMBIEN en premultiplicado y acotado por el alfa: asi
    el borde semitransparente no se aclara contra el negro de afuera (el halo claro que deja un unsharp en RGB).

    python assets\\make-icon.py            (lo llama build.ps1 -Icono)
"""
import io
import os
import struct

import numpy as np
from PIL import Image
from scipy import ndimage

AQUI = os.path.dirname(os.path.abspath(__file__))
FUENTE = os.path.join(AQUI, 'galaxia.png')
DESTINO = os.path.join(AQUI, 'app.ico')

TAMANOS = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256]

# (sigma, cantidad): cuanto mas chico el frame, mas detalle se pierde en la reduccion y mas realce necesita.
# Los de icon-pack (16/24/32/48/64) interpolados para los intermedios que agrega Windows 11 (20, 30, 36, 40, 60).
REALCE = {
    16: (0.50, 1.30), 20: (0.50, 1.22), 24: (0.50, 1.15), 30: (0.55, 1.04), 32: (0.60, 1.00),
    36: (0.62, 0.94), 40: (0.65, 0.90), 48: (0.70, 0.80), 60: (0.78, 0.62), 64: (0.80, 0.55),
}


def reducir(base, n):
    """RGBA 8 bits -> frame n x n, premultiplicado y realzado. Devuelve RGBA 8 bits."""
    chico = base.convert('RGBa').resize((n, n), Image.LANCZOS)
    pm = np.asarray(chico, np.float32) / 255.0          # R,G,B ya multiplicados por A
    if n in REALCE:
        sigma, k = REALCE[n]
        rgb, a = pm[..., :3], pm[..., 3:4]
        borroso = np.stack([ndimage.gaussian_filter(rgb[..., i], sigma, mode='nearest') for i in range(3)], -1)
        rgb = np.clip(rgb + k * (rgb - borroso), 0.0, a)   # premultiplicado: el color nunca supera al alfa
        pm = np.concatenate([rgb, a], -1)
    pm8 = Image.fromarray(np.clip(np.round(pm * 255), 0, 255).astype(np.uint8), 'RGBa')
    return pm8.convert('RGBA')


def escribir_ico(frames, destino):
    pngs = []
    for im in frames:
        b = io.BytesIO()
        im.save(b, 'PNG', optimize=True)
        pngs.append((im.size[0], b.getvalue()))
    with open(destino, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, len(pngs)))
        off = 6 + 16 * len(pngs)
        for n, d in pngs:
            b = 0 if n >= 256 else n
            f.write(struct.pack('<BBBBHHII', b, b, 0, 0, 1, 32, len(d), off))
            off += len(d)
        for _, d in pngs:
            f.write(d)
    return pngs


def main():
    base = Image.open(FUENTE).convert('RGBA')
    if base.width != base.height:                         # a un cuadrado, centrado
        lado = max(base.size)
        c = Image.new('RGBA', (lado, lado), (0, 0, 0, 0))
        c.paste(base, ((lado - base.width) // 2, (lado - base.height) // 2))
        base = c
    frames = [base if base.width == n else reducir(base, n) for n in TAMANOS if n <= base.width]
    pngs = escribir_ico(frames, DESTINO)
    total = os.path.getsize(DESTINO)
    print('app.ico -> {:,} bytes, {} tamanos: {}'.format(total, len(pngs), ' '.join(str(n) for n, _ in pngs)))


if __name__ == '__main__':
    main()
