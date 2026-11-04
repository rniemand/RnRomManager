import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { svelteTesting } from '@testing-library/svelte/vite';
import { defineConfig } from 'vitest/config';

const api = 'http://localhost:5080';

export default defineConfig({
	plugins: [
		sveltekit({
			compilerOptions: {
				// Force runes mode for the project, except for libraries. Can be removed in svelte 6.
				runes: ({ filename }) =>
					filename.split(/[/\\]/).includes('node_modules') ? undefined : true
			},
			// SPA mode: the API serves build/ and falls back to index.html (spec §11, §23).
			adapter: adapter({ fallback: 'index.html' })
		}),
		svelteTesting()
	],
	server: {
		proxy: { '/api': api, '/health': api }
	},
	test: {
		environment: 'jsdom',
		include: ['src/**/*.test.ts']
	}
});
