import { defineConfig, devices } from '@playwright/test';

// Run through scripts/test-e2e.ps1 (spec §15.3), which starts docker-compose.e2e.yml and sets these variables.
// With E2E_BASE_URL_B and E2E_BASE_URL_C set, the specs run on three stacks at once: tests tagged @shard-b on the
// second, @shard-c on the third, the rest on the first, and @both (sign-in, ingest) on each (see specs/mvp.spec.ts).
const stackB = process.env.E2E_BASE_URL_B;
const stackC = process.env.E2E_BASE_URL_C;
const sharded = /@shard-b|@shard-c/;
const chromium = {
	...devices['Desktop Chrome'],
	// PSP B3: PPSSPP needs WebGL 2; on GPU-less runners Chromium only offers it through SwiftShader. The PSP test still
	// checks for a real WebGL 2 context.
	launchOptions: { args: ['--enable-unsafe-swiftshader'] }
};

export default defineConfig({
	// Each stack's specs share one seeded library and walk through it in order.
	fullyParallel: false,
	workers: 1 + (stackB ? 1 : 0) + (stackC ? 1 : 0),
	retries: 0,
	forbidOnly: !!process.env.CI,
	timeout: 60_000,
	expect: { timeout: 15_000 },
	reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
	use: {
		baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:18080',
		trace: 'retain-on-failure',
		screenshot: 'only-on-failure'
	},
	projects: [
		{
			name: 'chromium',
			testDir: './specs',
			grepInvert: stackB || stackC ? sharded : undefined,
			metadata: { ingest: process.env.E2E_INGEST },
			use: chromium
		},
		...(stackB
			? [
					{
						name: 'chromium-b',
						testDir: './specs',
						grep: /@both|@shard-b/,
						metadata: { ingest: process.env.E2E_INGEST_B },
						use: { ...chromium, baseURL: stackB }
					}
				]
			: []),
		...(stackC
			? [
					{
						name: 'chromium-c',
						testDir: './specs',
						grep: /@both|@shard-c/,
						metadata: { ingest: process.env.E2E_INGEST_C },
						use: { ...chromium, baseURL: stackC }
					}
				]
			: []),
		...(process.env.E2E_SCREENSHOTS === '1'
			? [
					{
						name: 'screenshots',
						testDir: './screenshots',
						use: {
							...devices['Desktop Chrome'],
							viewport: { width: 1440, height: 900 },
							colorScheme: 'light' as const
						}
					}
				]
			: [])
	]
});
