import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';

test('capture the README screenshots', async ({ page }) => {
	const adminPassword = process.env.E2E_ADMIN_PASSWORD;
	if (!adminPassword) throw new Error('E2E_ADMIN_PASSWORD is not set (run scripts/test-e2e.ps1).');
	const newPassword = `${adminPassword}-changed`;
	const imagePath = (name: string) => fileURLToPath(new URL(`../../../docs/images/${name}.png`, import.meta.url));

	await page.goto('/');
	await expect(page).toHaveURL(/\/login\?returnUrl=/);
	await page.getByLabel('Username').fill('admin');
	await page.getByLabel('Password').fill(adminPassword);
	await page.getByRole('button', { name: 'Sign in' }).click();
	await expect(page).toHaveURL(/\/change-password$/);
	await page.getByLabel('Current password').fill(adminPassword);
	await page.getByLabel('New password', { exact: true }).fill(newPassword);
	await page.getByLabel('Confirm new password').fill(newPassword);
	await page.getByRole('button', { name: 'Change password' }).click();

	await page.getByRole('link', { name: 'Go to Ingestion' }).click();
	await expect(page.getByRole('cell', { name: '91-unmatched.gb', exact: true })).toBeVisible();
	await expect(page.getByText('Password changed.', { exact: true })).toBeHidden();
	await page.screenshot({ path: imagePath('ingestion'), animations: 'disabled' });
	await page.getByRole('button', { name: 'Import all' }).click();
	await page.getByRole('tab', { name: 'Last runs' }).click();
	await expect(page.getByRole('row').filter({ hasText: 'Completed' })).toContainText(
		'Ingested 34 · Duplicate 1 · Review 2 · Failed 1',
		{ timeout: 60_000 }
	);

	await page.goto('/?page=1');
	await expect(page.getByRole('button', { name: 'System: All' })).toBeVisible();
	await expect(page.getByText('25 games', { exact: true })).toBeVisible();
	await page.screenshot({ path: imagePath('library'), animations: 'disabled' });
	await page.getByRole('link', { name: 'Orbit Blocks', exact: true }).click();
	await expect(page.getByRole('heading', { level: 1, name: 'Orbit Blocks' })).toBeVisible();
	await page.screenshot({ path: imagePath('game-detail'), animations: 'disabled' });
});
