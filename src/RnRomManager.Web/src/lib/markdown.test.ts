import { describe, expect, it } from 'vitest';
import { maxNoteBytes, noteBytes, normaliseNote, renderMarkdown } from './markdown';

describe('renderMarkdown', () => {
	it('keeps headings, lists, emphasis and code', () => {
		const html = renderMarkdown('# Title\n\n- one\n- **two**\n\n```\ncode()\n```\n\n`inline`');
		const div = document.createElement('div');
		div.innerHTML = html;
		expect(div.querySelector('h1')!.textContent).toBe('Title');
		expect([...div.querySelectorAll('li')].map((li) => li.textContent)).toEqual(['one', 'two']);
		expect(div.querySelector('strong')!.textContent).toBe('two');
		expect(div.querySelector('pre code')!.textContent).toBe('code()\n');
		expect(div.querySelectorAll('code')).toHaveLength(2);
	});

	it('strips <script>, event handlers and javascript: links', () => {
		const html = renderMarkdown(
			'<script>alert(1)</script>\n\n<img src="x" onerror="alert(2)">\n\n[click](javascript:alert(3))\n\n<a href="javascript:alert(4)">raw</a>'
		);
		expect(html).not.toMatch(/<script/i);
		expect(html).not.toMatch(/onerror/i);
		expect(html).not.toMatch(/javascript:/i);
		expect(html).toContain('<img src="x">');
	});

	it('opens links in a new tab without an opener', () => {
		const div = document.createElement('div');
		div.innerHTML = renderMarkdown('[docs](https://example.com/a) and <https://example.com/b>');
		const links = [...div.querySelectorAll('a')];
		expect(links).toHaveLength(2);
		for (const a of links) {
			expect(a.getAttribute('target')).toBe('_blank');
			expect(a.getAttribute('rel')).toBe('noopener noreferrer');
		}
	});

	it('empty text renders nothing', () => {
		expect(renderMarkdown('')).toBe('');
	});
});

describe('note bytes', () => {
	it('normalises CRLF and CR to LF', () => {
		expect(normaliseNote('a\r\nb\rc\n')).toBe('a\nb\nc\n');
	});

	it('counts UTF-8 bytes after normalisation', () => {
		expect(noteBytes('a\r\nb')).toBe(3);
		expect(noteBytes('é')).toBe(2);
		expect(noteBytes('😀')).toBe(4);
	});

	it('the cap is 64 KiB', () => {
		expect(maxNoteBytes).toBe(65536);
	});
});
