// Anchor brand-asset generator (AF2 / issue #163).
//
// Single source of truth for every shipped icon/tile/splash/favicon: the
// Anchor mark from design/anchor-mark.svg, composed onto the correct
// per-surface background and rasterised to each target path. Re-run after any
// change to the mark or the brand colours:
//
//   cd design/icons && npm install && node generate.mjs
//
// Outputs are committed; this script is the recipe, not a build step. Surface
// treatments follow design/ANCHOR_BRAND.md: teacher dashboard = paper/light
// (indigo #34357A on paper), student agent + extension = ink/dark (indigo
// #7E80D2, on ink where a plate is needed). The accent is branding-only and
// never the magenta in-app spark.

import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Resvg } from "@resvg/resvg-js";
import pngToIco from "png-to-ico";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

// --- brand colours (design/ANCHOR_BRAND.md §2) ---
const ACCENT_PAPER = "#34357A"; // mark on paper (dashboard)
const ACCENT_INK = "#7E80D2"; // mark on ink (agent, extension)
const PAPER = "#FAF7F2";
const INK = "#1B1B23";

// The mark's content bounding box, squared and centred, so composed icons
// centre the device rather than the loose 56×56 authoring canvas.
const CONTENT_VIEWBOX = "6 4 44 44";

// Pull the mark geometry straight from the source SVG so this never drifts
// from AF1. We recolour the single accent value per surface.
const markSvg = readFileSync(resolve(REPO, "design", "anchor-mark.svg"), "utf8");
const markGroupRaw = markSvg.match(/<g[\s\S]*<\/g>/)[0];
const markGroup = (color) => markGroupRaw.replaceAll(ACCENT_PAPER, color);

function nestedMark(x, y, side, stroke) {
  return (
    `<svg x="${x}" y="${y}" width="${side}" height="${side}" ` +
    `viewBox="${CONTENT_VIEWBOX}">${markGroup(stroke)}</svg>`
  );
}

// Square icon: optional background plate + centred mark with `padFrac` clear space.
function squareIcon(size, { bg, stroke, padFrac }) {
  const pad = Math.round(size * padFrac);
  const side = size - 2 * pad;
  const plate = bg ? `<rect width="${size}" height="${size}" fill="${bg}"/>` : "";
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" ` +
    `viewBox="0 0 ${size} ${size}">${plate}${nestedMark(pad, pad, side, stroke)}</svg>`
  );
}

// Rectangular icon (wide tile, splash): mark squared to a fraction of the
// short edge and centred on a background plate.
function rectIcon(w, h, { bg, stroke, sideFrac }) {
  const side = Math.round(Math.min(w, h) * sideFrac);
  const x = Math.round((w - side) / 2);
  const y = Math.round((h - side) / 2);
  const plate = bg ? `<rect width="${w}" height="${h}" fill="${bg}"/>` : "";
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" ` +
    `viewBox="0 0 ${w} ${h}">${plate}${nestedMark(x, y, side, stroke)}</svg>`
  );
}

function renderPng(svg) {
  return new Resvg(svg, { fitTo: { mode: "original" } }).render().asPng();
}

function writePng(relPath, svg) {
  const out = resolve(REPO, relPath);
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, renderPng(svg));
  console.log("  ✓", relPath);
}

async function writeIco(relPath, sizes, opts) {
  const buffers = sizes.map((s) => renderPng(squareIcon(s, opts)));
  const ico = await pngToIco(buffers);
  const out = resolve(REPO, relPath);
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, ico);
  console.log("  ✓", relPath, `(${sizes.join(",")})`);
}

// ---------------------------------------------------------------------------

console.log("Dashboard (paper / light — teacher):");
// Favicon: transparent, indigo mark, near-full so it reads at tab size.
writePng("dashboard/web/favicon.png", squareIcon(32, { stroke: ACCENT_PAPER, padFrac: 0.06 }));
// PWA "any" icons: transparent, indigo mark with clear space.
for (const size of [192, 512]) {
  writePng(`dashboard/web/icons/Icon-${size}.png`, squareIcon(size, { stroke: ACCENT_PAPER, padFrac: 0.14 }));
}
// Maskable: full-bleed paper plate, mark held inside the circular safe zone.
for (const size of [192, 512]) {
  writePng(
    `dashboard/web/icons/Icon-maskable-${size}.png`,
    squareIcon(size, { bg: PAPER, stroke: ACCENT_PAPER, padFrac: 0.2 }),
  );
}

console.log("Agent (ink / dark — student):");
const agent = "agent/src/FocusAgent.App/Assets";
for (const size of [44, 150, 310]) {
  writePng(`${agent}/Square${size}x${size}Logo.png`, squareIcon(size, { bg: INK, stroke: ACCENT_INK, padFrac: 0.2 }));
}
writePng(`${agent}/StoreLogo.png`, squareIcon(50, { bg: INK, stroke: ACCENT_INK, padFrac: 0.18 }));
writePng(`${agent}/Wide310x150Logo.png`, rectIcon(310, 150, { bg: INK, stroke: ACCENT_INK, sideFrac: 0.62 }));
writePng(`${agent}/SplashScreen.png`, rectIcon(620, 300, { bg: INK, stroke: ACCENT_INK, sideFrac: 0.5 }));

console.log("Agent tray (transparent — floats on the taskbar):");
// Tray icon: transparent so it sits on the (dark, by default) taskbar; the
// on-ink indigo reads there. Multi-size .ico for crisp small rendering.
await writeIco(`${agent}/TrayIcon.ico`, [16, 20, 24, 32, 48, 256], { stroke: ACCENT_INK, padFrac: 0.06 });

console.log("Extension (ink / dark — student):");
// Toolbar action + store/management icons: transparent, on-ink indigo.
for (const size of [16, 32, 48, 128]) {
  writePng(`extension/src/icons/icon-${size}.png`, squareIcon(size, { stroke: ACCENT_INK, padFrac: 0.08 }));
}

console.log("Done.");
