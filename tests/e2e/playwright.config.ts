import { defineConfig, devices } from '@playwright/test';

// Run through scripts/test-e2e.ps1 (spec §15.3), which starts docker-compose.e2e.yml and sets these variables.
// With E2E_BASE_URL_B set, the specs run on two stacks at once: tests tagged @shard-b on the second, the rest on the
// first, and @both (sign-in, ingest) on each (see specs/mvp.spec.ts).
const stackB = process.env.E2E_BASE_URL_B;
const chromium = {
	...devices['Desktop Chrome'],
	// PSP B3: PPSSPP needs WebGL 2; on GPU-less runners Chromium only offers it through SwiftShader. The PSP test still
	// checks for a real WebGL 2 context.
	launchOptions: { args: ['--enable-unsafe-swiftshader'] }
};

export default defineConfig({
	// Each stack's specs share one seeded library and walk through it in order.
	fullyParallel: false,
	workers: stackB ? 2 : 1,
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
			grepInvert: stackB ? /@shard-b/ : undefined,
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
