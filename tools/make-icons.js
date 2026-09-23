'use strict';
/*
 * Generates assets/app.ico (multi-size, classic 32bpp DIB entries) and
 * assets/app.png from vector math - no third-party dependencies.
 *
 * DIB entries are used instead of PNG-compressed entries on purpose:
 * System.Drawing.Icon / Win32 CreateIconFromResourceEx are most reliable
 * with BMP-format icon entries.
 */

const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const OUT_DIR = path.join(__dirname, '..', 'assets');
const ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];

/* ------------------------------------------------------------------ PNG --- */

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(buf) {
  let c = -1;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function pngChunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}

function encodePng(rgba, width, height) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0; // filter: none
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }
  return Buffer.concat([
    signature,
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

/* -------------------------------------------------------------- DRAWING --- */

const BACKGROUND_TOP = [0x26, 0x2c, 0x3e];
const BACKGROUND_BOTTOM = [0x11, 0x14, 0x1c];
const GLYPH = [0xff, 0xff, 0xff];

function insideRoundedRect(u, v, radius) {
  const x = Math.min(u, 1 - u);
  const y = Math.min(v, 1 - v);
  if (x >= radius || y >= radius) return true;
  const dx = radius - x;
  const dy = radius - y;
  return dx * dx + dy * dy <= radius * radius;
}

/** The pi glyph: a top bar with two legs. */
function insidePi(u, v) {
  const bar = u >= 0.235 && u <= 0.765 && v >= 0.285 && v <= 0.395;
  const leftLeg = u >= 0.315 && u <= 0.425 && v >= 0.355 && v <= 0.745;
  const rightLeg = u >= 0.575 && u <= 0.685 && v >= 0.355 && v <= 0.745;
  return bar || leftLeg || rightLeg;
}

function mix(a, b, t) {
  return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
}

/** Renders one square icon into an RGBA buffer using 4x supersampling. */
function drawIcon(size) {
  const SS = 4;
  const W = size * SS;
  const background = new Float32Array(W * W);
  const glyph = new Float32Array(W * W);
  const radius = 0.21;

  for (let y = 0; y < W; y++) {
    const v = (y + 0.5) / W;
    for (let x = 0; x < W; x++) {
      const u = (x + 0.5) / W;
      const i = y * W + x;
      background[i] = insideRoundedRect(u, v, radius) ? 1 : 0;
      glyph[i] = insidePi(u, v) ? 1 : 0;
    }
  }

  const out = Buffer.alloc(size * size * 4);
  const samples = SS * SS;

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let bgCoverage = 0;
      let glyphCoverage = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const i = (y * SS + sy) * W + (x * SS + sx);
          bgCoverage += background[i];
          glyphCoverage += glyph[i];
        }
      }
      bgCoverage /= samples;
      glyphCoverage /= samples;

      const glyphAlpha = Math.min(glyphCoverage, bgCoverage);
      const baseAlpha = bgCoverage * (1 - glyphAlpha);
      const alpha = glyphAlpha + baseAlpha;

      let color = [0, 0, 0];
      if (alpha > 0) {
        const gradient = mix(BACKGROUND_TOP, BACKGROUND_BOTTOM, (y + 0.5) / size);
        color = [
          (GLYPH[0] * glyphAlpha + gradient[0] * baseAlpha) / alpha,
          (GLYPH[1] * glyphAlpha + gradient[1] * baseAlpha) / alpha,
          (GLYPH[2] * glyphAlpha + gradient[2] * baseAlpha) / alpha,
        ];
      }

      const offset = (y * size + x) * 4;
      out[offset] = Math.round(Math.min(255, Math.max(0, color[0])));
      out[offset + 1] = Math.round(Math.min(255, Math.max(0, color[1])));
      out[offset + 2] = Math.round(Math.min(255, Math.max(0, color[2])));
      out[offset + 3] = Math.round(Math.min(255, Math.max(0, alpha * 255)));
    }
  }

  return out;
}

/* ------------------------------------------------------------------ ICO --- */

function dibEntry(rgba, size) {
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0); // biSize
  header.writeInt32LE(size, 4); // biWidth
  header.writeInt32LE(size * 2, 8); // biHeight (XOR + AND)
  header.writeUInt16LE(1, 12); // biPlanes
  header.writeUInt16LE(32, 14); // biBitCount
  header.writeUInt32LE(0, 16); // BI_RGB
  header.writeUInt32LE(size * size * 4, 20); // biSizeImage

  const xor = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    const sourceRow = size - 1 - y; // DIB rows are bottom-up
    for (let x = 0; x < size; x++) {
      const s = (sourceRow * size + x) * 4;
      const d = (y * size + x) * 4;
      xor[d] = rgba[s + 2]; // B
      xor[d + 1] = rgba[s + 1]; // G
      xor[d + 2] = rgba[s]; // R
      xor[d + 3] = rgba[s + 3]; // A
    }
  }

  const maskStride = Math.ceil(size / 32) * 4;
  const andMask = Buffer.alloc(maskStride * size); // zeros => use alpha channel

  return Buffer.concat([header, xor, andMask]);
}

function encodeIco(sizes) {
  const images = sizes.map((size) => ({ size, data: dibEntry(drawIcon(size), size) }));
  const directory = Buffer.alloc(6 + images.length * 16);
  directory.writeUInt16LE(0, 0); // reserved
  directory.writeUInt16LE(1, 2); // type: icon
  directory.writeUInt16LE(images.length, 4);

  let offset = directory.length;
  images.forEach((image, index) => {
    const entry = 6 + index * 16;
    const dimension = image.size >= 256 ? 0 : image.size;
    directory[entry] = dimension;
    directory[entry + 1] = dimension;
    directory[entry + 2] = 0; // palette count
    directory[entry + 3] = 0; // reserved
    directory.writeUInt16LE(1, entry + 4); // color planes
    directory.writeUInt16LE(32, entry + 6); // bits per pixel
    directory.writeUInt32LE(image.data.length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += image.data.length;
  });

  return Buffer.concat([directory, ...images.map((image) => image.data)]);
}

/* ----------------------------------------------------------------- MAIN --- */

fs.mkdirSync(OUT_DIR, { recursive: true });

const ico = encodeIco(ICO_SIZES);
fs.writeFileSync(path.join(OUT_DIR, 'app.ico'), ico);

const png = encodePng(drawIcon(256), 256, 256);
fs.writeFileSync(path.join(OUT_DIR, 'app.png'), png);

console.log(`app.ico  ${ICO_SIZES.join(',')}px  ${ico.length} bytes`);
console.log(`app.png  256px  ${png.length} bytes`);
