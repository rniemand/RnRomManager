import { describe, expect, it } from 'vitest';
import { gameSearchQuery, googleSearchUrl, reviewSearchQuery } from './googleSearch';

const item = (originalFileName: string, title?: unknown) => ({
	originalFileName,
	crc32: 'deadbeef',
	header: title === undefined ? null : { title }
});

describe('reviewSearchQuery', () => {
	it('strips the extension, brackets and underscores (the RV2 example)', () => {
		expect(reviewSearchQuery(item('Orbt_Blcks (U) [!].gb'), 'Game Boy')).toBe(
			'"Orbt Blcks" Game Boy rom'
		);
	});

	it('uses the header title when it has at least 3 alphanumerics', () => {
		expect(reviewSearchQuery(item('x.gb', 'TETRIS'), 'Game Boy')).toBe('"TETRIS" Game Boy rom');
		expect(reviewSearchQuery(item('x.gb', 'DR.MARIO'), 'Game Boy')).toBe('"DR MARIO" Game Boy rom');
	});

	it('falls back to the file name for a short or empty title', () => {
		expect(reviewSearchQuery(item('Some.Game.gbc', 'A!'), 'Game Boy Color')).toBe(
			'"Some Game" Game Boy Color rom'
		);
		expect(reviewSearchQuery(item('Quest.gb', ''), 'Game Boy')).toBe('"Quest" Game Boy rom');
	});

	it('uses the CRC32 when the cleaned name is empty', () => {
		expect(reviewSearchQuery(item('(Beta) [b].gb'), 'Game Boy')).toBe('deadbeef Game Boy rom');
	});

	it('builds an encoded Google URL', () => {
		expect(googleSearchUrl(reviewSearchQuery(item('a & b.gb'), 'Game Boy'))).toBe(
			'https://www.google.com/search?q=%22a%20%26%20b%22%20Game%20Boy%20rom'
		);
	});
});

describe('gameSearchQuery', () => {
	it('quotes the display title and adds the system name', () => {
		expect(gameSearchQuery({ displayTitle: 'Quest, The', systemName: 'Game Boy' })).toBe(
			'"Quest, The" Game Boy'
		);
	});
});
