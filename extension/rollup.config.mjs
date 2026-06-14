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
          { src: 'src/icons', dest: 'dist' },
          // Design-system vanilla binding (AF3, #164): the block page links
          // plink.css from here. Copied with its upstream layout intact
          // (dist/plink.css + assets/fonts one level up) so the stylesheet's
          // `../assets/fonts/…` @font-face paths resolve unmodified. See
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
];
