import { defineConfig, devices } from '@playwright/test';

// Run through scripts/test-e2e.ps1 (spec §15.3), which starts docker-compose.e2e.yml and sets these variables.
export default defineConfig({
	// The specs share one seeded library and walk through it in order.
	fullyParallel: false,
	workers: 1,
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
		{ name: 'chromium', testDir: './specs', use: { ...devices['Desktop Chrome'] } },
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
