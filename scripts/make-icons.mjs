#!/usr/bin/env node
/**
 * Renders the toolbar / store icons (a shield with a "no entry" bar) as PNGs
 * without any image library: shapes are defined analytically, supersampled
 * for anti-aliasing and written with a minimal PNG encoder.
 *
 *   icons/icon-{16,32,48,128}.png   active (red)
 *   icons/icon-off-{16,32}.png      paused (grey), used by chrome.action.setIcon
 */
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const OUT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'icons');
const SAMPLES = 6;

const PALETTES = {
  on: { top: [240, 74, 82], bottom: [178, 30, 43] },
  off: { top: [163, 166, 173], bottom: [106, 109, 117] },
};

const VARIANTS = [
  ['icon-16.png', 16, 'on'],
  ['icon-32.png', 32, 'on'],
  ['icon-48.png', 48, 'on'],
  ['icon-128.png', 128, 'on'],
  ['icon-off-16.png', 16, 'off'],
  ['icon-off-32.png', 32, 'off'],
];

function insideShield(x, y) {
  const top = 0.06;
  const shoulder = 0.5;
  const tip = 0.96;
  const half = 0.4;
  const r = 0.13;
  if (y < top || y > tip) return false;
  const dx = Math.abs(x - 0.5);
  if (y <= shoulder) {
    if (dx > half) return false;
    if (y < top + r && dx > half - r) return (dx - (half - r)) ** 2 + (y - (top + r)) ** 2 <= r * r;
    return true;
  }
  const t = (y - shoulder) / (tip - shoulder);
  return dx <= half * (1 - t * t);
}

function insideBar(x, y, size) {
  // Thicker at small sizes so the bar survives at 16px.
  const hh = size <= 16 ? 0.1 : 0.075;
  const cy = 0.46;
  const left = 0.27 + hh;
  const right = 0.73 - hh;
  if (Math.abs(y - cy) > hh) return false;
  const ex = Math.min(Math.max(x, left), right);
  return (x - ex) ** 2 + (y - cy) ** 2 <= hh * hh;
}

function render(size, palette) {
  const px = Buffer.alloc(size * size * 4);
  for (let py = 0; py < size; py++) {
    for (let pxX = 0; pxX < size; pxX++) {
      let shield = 0;
      let bar = 0;
      for (let sy = 0; sy < SAMPLES; sy++) {
        for (let sx = 0; sx < SAMPLES; sx++) {
          const x = (pxX + (sx + 0.5) / SAMPLES) / size;
          const y = (py + (sy + 0.5) / SAMPLES) / size;
          if (insideShield(x, y)) {
            shield++;
            if (insideBar(x, y, size)) bar++;
          }
        }
      }
      const i = (py * size + pxX) * 4;
      if (!shield) continue;
      const t = (py + 0.5) / size;
      const grad = palette.top.map((c, k) => c + (palette.bottom[k] - c) * t);
      const fill = shield - bar;
      for (let k = 0; k < 3; k++) px[i + k] = Math.round((grad[k] * fill + 255 * bar) / shield);
      px[i + 3] = Math.round((255 * shield) / SAMPLES ** 2);
    }
  }
  return encodePng(size, size, px);
}

/* ------------------------------ PNG encoder ------------------------------ */

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function encodePng(width, height, rgba) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0; // filter: none
    rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

await mkdir(OUT, { recursive: true });
for (const [name, size, palette] of VARIANTS) {
  await writeFile(path.join(OUT, name), render(size, PALETTES[palette]));
  console.log(`icons/${name}`);
}
