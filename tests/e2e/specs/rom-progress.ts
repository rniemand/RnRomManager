import { expect, type FrameLocator, type Page } from '@playwright/test';

type PlayerWindow = Window & {
	advanceRomDownload: (fraction: number) => void;
	EJS_onGameStart: () => void;
};

export async function stageRomDownload(page: Page, name: string): Promise<void> {
	await page.addInitScript((name) => {
		if (
			location.pathname !== '/emulator/play.html' ||
			new URLSearchParams(location.search).get('name') !== name
		)
			return;

		const player = window as unknown as PlayerWindow;
		player.EJS_onGameStart = () => {
			document.body.dataset.gameStarted = 'true';
		};
		const fetch = window.fetch.bind(window);
		window.fetch = async (input, init) => {
			const response = await fetch(input, init);
			if (
				!response.ok ||
				typeof input !== 'string' ||
				!/^\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(input)
			)
				return response;

			// Use the seeded ROM from the real endpoint. Gate delivery to the player so even a tiny ROM has observable intermediate progress.
			const bytes = new Uint8Array(await response.arrayBuffer());
			let offset = 0;
			const body = new ReadableStream<Uint8Array>({
				start(controller) {
					player.advanceRomDownload = (fraction) => {
						const end = Math.floor(bytes.length * fraction);
						controller.enqueue(bytes.subarray(offset, end));
						offset = end;
						if (end === bytes.length) controller.close();
					};
				}
			});
			return new Response(body, { headers: { 'Content-Length': String(bytes.length) } });
		};
	}, name);
}

export async function expectRomDownloadProgress(frame: FrameLocator): Promise<void> {
	const bar = frame.locator('progress');
	const advance = (fraction: number) =>
		bar.evaluate(
			(_, fraction) => (window as unknown as PlayerWindow).advanceRomDownload(fraction),
			fraction
		);
	await expect(bar).toBeVisible();
	await expect(bar).toHaveJSProperty('position', 0);
	for (const fraction of [0.25, 0.75]) {
		await advance(fraction);
		await expect.poll(() => bar.evaluate((el: HTMLProgressElement) => el.position)).toBe(fraction);
		await expect(bar).toBeVisible();
	}
	await advance(1);
	await expect(bar).toBeHidden();
	await expect(frame.locator('#status')).toBeEmpty();
}
