import { readFileSync } from 'node:fs';
import typescript from '@rollup/plugin-typescript';
import nodeResolve from '@rollup/plugin-node-resolve';
import copy from 'rollup-plugin-copy';

const tsPlugin = () => typescript({ tsconfig: './tsconfig.json' });

// #208: package.json is the single source of truth for the extension version.
// The build stamps it into the manifest copied to dist/, so the shipped manifest
// can never drift from package.json — there's only one number to bump. (The
// committed src/manifest.json keeps a mirror that manifest.test.ts locks, so an
// editor reading the unbuilt source still sees the right value.)
const pkgVersion = JSON.parse(readFileSync('./package.json', 'utf8')).version;
const stampManifestVersion = (contents) => {
  const manifest = JSON.parse(contents.toString());
  manifest.version = pkgVersion;
  return JSON.stringify(manifest, null, 2) + '\n';
};

export default [
  {
    input: 'src/background.ts',
    output: {
      file: 'dist/background.js',
      format: 'esm',
      sourcemap: true,
    },
    plugins: [
      nodeResolve(),
      tsPlugin(),
      copy({
        targets: [
          { src: 'src/manifest.json', dest: 'dist', transform: stampManifestVersion },
          // i18n (#322): the chrome.i18n catalogues. `_locales/<lang>/messages.json`
          // must sit at the extension root for the browser to find them, and
          // manifest `default_locale: en` resolves any `__MSG_*__` / missing key.
          { src: 'src/_locales', dest: 'dist' },
          { src: 'src/content/block-page.html', dest: 'dist' },
          // AE2 (#178): the toolbar-action status popup, served from dist root
          // (manifest action.default_popup) — same wiring as the block page.
          { src: 'src/content/popup.html', dest: 'dist' },
          { src: 'src/icons', dest: 'dist' },
          // Design-system vanilla binding (AF3, #164): the block page links
          // plink.css from here. Copied with assets/fonts kept as a sibling of
          // the stylesheet so its `../assets/fonts/…` @font-face paths resolve
          // unmodified. (Upstream ships it under dist/; vendored as css/ here so
          // the extension's dist/ .gitignore can't swallow it.) See
          // src/vendor/plink-design-system/README.md for provenance.
          { src: 'src/vendor/plink-design-system', dest: 'dist/vendor' },
        ],
      }),
    ],
  },
  {
    input: 'src/content/block-page.ts',
    output: {
      file: 'dist/block-page.js',
      format: 'iife',
      sourcemap: true,
    },
    plugins: [nodeResolve(), tsPlugin()],
  },
  {
    input: 'src/content/popup.ts',
    output: {
      file: 'dist/popup.js',
      format: 'iife',
      sourcemap: true,
    },
    plugins: [nodeResolve(), tsPlugin()],
  },
];
