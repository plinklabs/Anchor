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
