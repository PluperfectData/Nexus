// Unit tests: src/**/*.test.ts(x), run in a simulated browser (jsdom) with React Testing Library.
// Babel is used only here; Vite builds the app without it.

/** @type {import('jest').Config} */
export default {
  testEnvironment: 'jsdom',
  roots: ['<rootDir>/src'],
  testMatch: ['**/*.test.ts', '**/*.test.tsx'],
  setupFilesAfterEnv: ['<rootDir>/jest/setup.ts'],
  transform: {
    '^.+\\.[jt]sx?$': ['babel-jest', {
      babelrc: false,
      configFile: false,
      presets: [
        ['@babel/preset-env', { targets: { node: 'current' } }],
        ['@babel/preset-react', { runtime: 'automatic' }],
        '@babel/preset-typescript',
      ],
      // Jest runs CommonJS, which has no import.meta; this turns import.meta.env.X into process.env.X.
      plugins: ['babel-plugin-transform-vite-meta-env'],
    }],
  },
  moduleNameMapper: {
    // The library ships only an ES module, which Jest cannot load; tests mock what they use of it.
    '^@pluperfect/jwt$': '<rootDir>/jest/jwt-stub.cjs',
    '\\.module\\.css$': 'identity-obj-proxy',
    '\\.css$': '<rootDir>/jest/style-stub.cjs',
    '\\.(svg|png|jpe?g|gif|webp|woff2?)$': '<rootDir>/jest/file-stub.cjs',
    '\\?url$': '<rootDir>/jest/file-stub.cjs',
  },
}
