import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// Release editing B37, B39: the manifest and the icons it names, as committed under static/.
const statics = resolve(process.cwd(), 'static');
const manifest = JSON.parse(readFileSync(resolve(statics, 'manifest.webmanifest'), 'utf8'));

/** A PNG's width and height from its IHDR chunk. */
function pngSize(path: string): string {
	const bytes = readFileSync(path);
	expect(bytes.subarray(1, 4).toString('ascii')).toBe('PNG');
	return `${bytes.readUInt32BE(16)}x${bytes.readUInt32BE(20)}`;
}

describe('PWA manifest (release editing B37–B39)', () => {
	it('has the B37 fields', () => {
		expect({ ...manifest, icons: undefined }).toEqual({
			name: 'RnRomManager',
			short_name: 'RnRom',
			start_url: '/',
			scope: '/',
			display: 'standalone',
			background_color: '#212529',
			theme_color: '#212529',
			icons: undefined
		});
		expect(manifest.icons).toEqual([
			{ src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
			{ src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
			{
				src: '/icons/icon-maskable-512.png',
				sizes: '512x512',
				type: 'image/png',
				purpose: 'maskable'
			}
		]);
	});

	it('every icon exists with its stated size, and the apple-touch icon is 180x180', () => {
		for (const icon of manifest.icons as { src: string; sizes: string }[]) {
			const path = resolve(statics, icon.src.slice(1));
			expect(existsSync(path), icon.src).toBe(true);
			expect(pngSize(path)).toBe(icon.sizes);
		}
		expect(pngSize(resolve(statics, 'icons/apple-touch-icon.png'))).toBe('180x180');
	});

	it('app.html links the manifest, the apple-touch icon and the theme colour (B38)', () => {
		const html = readFileSync(resolve(process.cwd(), 'src/app.html'), 'utf8');
		for (const tag of [
			'<link rel="manifest" href="/manifest.webmanifest" />',
			'<link rel="apple-touch-icon" href="/icons/apple-touch-icon.png" />',
			'<meta name="theme-color" content="#212529" />',
			'<meta name="apple-mobile-web-app-capable" content="yes" />'
		])
			expect(html).toContain(tag);
	});
});
