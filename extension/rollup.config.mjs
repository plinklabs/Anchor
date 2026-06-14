import typescript from '@rollup/plugin-typescript';
import nodeResolve from '@rollup/plugin-node-resolve';
import copy from 'rollup-plugin-copy';

const tsPlugin = () => typescript({ tsconfig: './tsconfig.json' });

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
          { src: 'src/manifest.json', dest: 'dist' },
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
