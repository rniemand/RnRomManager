import { describe, expect, it } from 'vitest';

// T4: every table uses the house style (docs/development.md).
const required = ['table', 'table-sm', 'table-striped', 'table-hover'];

function tableStyleProblems(source: string): string[] {
	const problems: string[] = [];
	for (const [tag] of source.matchAll(/<table\b[^>]*>/g)) {
		const classes = /class="([^"]*)"/.exec(tag)?.[1].split(/\s+/) ?? [];
		const missing = required.filter((c) => !classes.includes(c));
		if (missing.length) problems.push(`${tag} lacks ${missing.join(', ')}`);
	}
	for (const [tag] of source.matchAll(/<thead\b[^>]*>/g)) {
		if (tag !== '<thead class="table-dark">') problems.push(`${tag} is not table-dark`);
	}
	return problems;
}

const sources = import.meta.glob<string>('/src/**/*.svelte', {
	query: '?raw',
	import: 'default',
	eager: true
});

describe('table style', () => {
	it('flags a table missing a class and a plain thead', () => {
		expect(tableStyleProblems('<table class="table table-sm table-hover"><thead>')).toEqual([
			'<table class="table table-sm table-hover"> lacks table-striped',
			'<thead> is not table-dark'
		]);
	});

	it('finds the svelte sources', () => {
		expect(Object.keys(sources).length).toBeGreaterThan(10);
	});

	it.each(Object.entries(sources))('%s follows the table style', (_file, source) => {
		expect(tableStyleProblems(source)).toEqual([]);
	});
});
